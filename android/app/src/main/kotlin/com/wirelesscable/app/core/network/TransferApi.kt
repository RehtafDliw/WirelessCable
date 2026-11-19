package com.wirelesscable.app.core.network

import com.wirelesscable.app.core.protocol.ApiResult
import com.wirelesscable.app.core.protocol.ChunkAck
import com.wirelesscable.app.core.protocol.TransferBatchActionRequest
import com.wirelesscable.app.core.protocol.TransferBatchActionResult
import com.wirelesscable.app.core.protocol.ChunkInfo
import com.wirelesscable.app.core.protocol.DownloadChunkAckRequest
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.TransferCompleteRequest
import com.wirelesscable.app.core.protocol.TransferCompleteResponse
import com.wirelesscable.app.core.protocol.TransferDeleteRecordsRequest
import com.wirelesscable.app.core.protocol.TransferDeleteRecordsResult
import com.wirelesscable.app.core.protocol.TransferPaths
import com.wirelesscable.app.core.protocol.TransferStartRequest
import com.wirelesscable.app.core.protocol.TransferTask
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.io.InputStream
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.TrustManager

/** 下载分块的元信息，来自响应头。 */
data class ChunkMeta(
    val index: Int,
    val totalBytes: Long,
    val sha256: String,
    val length: Long,
    /** 本块内容的 SHA-256，落盘自检与回执的依据。 */
    val chunkSha256: String,
)

/**
 * 传输专用客户端。
 *
 * 与 [WcApiClient] 分开的原因：分块请求是长连接上的大块读写，20 秒的总超时会把慢速 Wi-Fi 上的
 * 正常传输直接掐断。这里关闭 callTimeout，只保留连接/读写超时；指纹固定策略完全一致。
 */
class TransferApi private constructor(
    private val endpoint: ServerEndpoint,
    private val client: OkHttpClient,
) {

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()
        val OCTET_MEDIA = "application/octet-stream".toMediaType()

        fun create(endpoint: ServerEndpoint, pins: Set<String>): TransferApi {
            val trustManager = PinnedTrustManager(pins)
            val sslContext = SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<TrustManager>(trustManager), SecureRandom())
            }

            val client = OkHttpClient.Builder()
                .sslSocketFactory(sslContext.socketFactory, trustManager)
                .hostnameVerifier { _, _ -> true }
                .connectTimeout(8, TimeUnit.SECONDS)
                .readTimeout(60, TimeUnit.SECONDS)
                .writeTimeout(60, TimeUnit.SECONDS)
                // 不设总超时：大文件传输本来就可能持续几分钟
                .callTimeout(0, TimeUnit.MILLISECONDS)
                .retryOnConnectionFailure(false)
                .build()

            return TransferApi(endpoint, client)
        }
    }

    suspend fun uploadStart(token: String, request: TransferStartRequest): ApiOutcome<TransferTask> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance.encodeToString(request).toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(TransferPaths.UPLOADS, token).post(body).build())
        }

    suspend fun putChunk(
        token: String,
        taskId: String,
        index: Int,
        chunkSha256: String,
        body: RequestBody,
    ): ApiOutcome<ChunkAck> = withContext(Dispatchers.IO) {
        executeJson(
            buildRequest(TransferPaths.chunk(taskId, index), token)
                .header(TransferPaths.CHUNK_SHA256_HEADER, chunkSha256)
                .put(body)
                .build(),
        )
    }

    /** 任务的分块位图：续传时只补未完成的分块。 */
    suspend fun chunks(token: String, taskId: String): ApiOutcome<List<ChunkInfo>> =
        withContext(Dispatchers.IO) {
            executeJson(buildRequest(TransferPaths.chunks(taskId), token).get().build())
        }

    /** 下载分块回执：确认该块已落盘并通过本地校验。 */
    suspend fun ackChunk(
        token: String,
        taskId: String,
        index: Int,
        sha256: String,
    ): ApiOutcome<ChunkAck> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(DownloadChunkAckRequest(sha256))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest(TransferPaths.chunkAck(taskId, index), token).post(body).build())
    }

    /** 恢复一个中断的任务。幂等：重复调用返回同一份服务端状态。 */
    suspend fun resume(token: String, taskId: String): ApiOutcome<TransferTask> =
        withContext(Dispatchers.IO) {
            val body = "{}".toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(TransferPaths.resume(taskId), token).post(body).build())
        }

    suspend fun complete(
        token: String,
        taskId: String,
        sha256: String,
    ): ApiOutcome<TransferCompleteResponse> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(TransferCompleteRequest(sha256))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest(TransferPaths.complete(taskId), token).post(body).build())
    }

    suspend fun acknowledge(
        token: String,
        taskId: String,
        sha256: String,
    ): ApiOutcome<TransferCompleteResponse> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(TransferCompleteRequest(sha256))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest(TransferPaths.ack(taskId), token).post(body).build())
    }

    /** 暂停：服务端保留临时文件与分块位图，继续时从已有偏移接上。 */
    suspend fun pause(token: String, taskId: String): ApiOutcome<TransferTask> =
        withContext(Dispatchers.IO) {
            val body = "{}".toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(TransferPaths.pause(taskId), token).post(body).build())
        }

    suspend fun cancel(token: String, taskId: String): ApiOutcome<TransferTask> =
        withContext(Dispatchers.IO) {
            val body = "{}".toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(TransferPaths.cancel(taskId), token).post(body).build())
        }

    /** 把一个已经中断的下载任务退回"等待中"，随后可以重新从第 0 块拉取。 */
    suspend fun restart(token: String, taskId: String): ApiOutcome<TransferTask> =
        withContext(Dispatchers.IO) {
            val body = "{}".toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(TransferPaths.restart(taskId), token).post(body).build())
        }

    /**
     * 一批任务上的同一个动作。
     *
     * 整文件夹的 200 个任务挂在同一行，逐个发请求光往返就要十几秒，
     * 中途断网还会只做一半；服务端一次处理完整批。
     */
    suspend fun batchAction(
        token: String,
        taskIds: List<String>,
        action: String,
    ): ApiOutcome<TransferBatchActionResult> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(TransferBatchActionRequest(taskIds, action))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest(TransferPaths.BATCH, token).post(body).build())
    }

    /**
     * 删除若干条传输记录。
     *
     * 只清列表；文件要不要一起删由 [deleteFiles] 决定（服务端只会删手机传过去的文件）。
     */
    suspend fun deleteRecords(
        token: String,
        taskIds: List<String>,
        deleteFiles: Boolean,
    ): ApiOutcome<TransferDeleteRecordsResult> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(TransferDeleteRecordsRequest(taskIds, deleteFiles))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest(TransferPaths.DELETE, token).post(body).build())
    }

    suspend fun tasks(token: String): ApiOutcome<List<TransferTask>> =
        withContext(Dispatchers.IO) {
            // 取够一个"整文件夹下载"批次：330 个文件要能被完整收成一行，
            // 默认 50 条会让同一个文件夹被切成好几行、数字也对不上
            executeJson(buildRequest("${TransferPaths.TASKS}?limit=1000", token).get().build())
        }

    /**
     * 拉取一个下载分块，并把输入流交给 [consume] 消费。
     * 流在 [consume] 返回后自动关闭，调用方只需在回调里写完目标文件。
     */
    suspend fun downloadChunk(
        token: String,
        taskId: String,
        index: Int,
        consume: suspend (InputStream, ChunkMeta) -> Unit,
    ): ApiOutcome<ChunkMeta> = withContext(Dispatchers.IO) {
        val request = buildRequest(TransferPaths.chunk(taskId, index), token).get().build()

        try {
            client.newCall(request).execute().use { response ->
                if (response.code !in 200..299) {
                    val text = response.body?.string().orEmpty()
                    return@withContext failureFromBody(text, response.code)
                }

                val body = response.body
                    ?: return@withContext ApiOutcome.Failure(
                        NetworkErrorCodes.BAD_RESPONSE,
                        "服务器返回空响应",
                    )

                val meta = ChunkMeta(
                    index = index,
                    totalBytes = response.header("X-WC-Total-Bytes")?.toLongOrNull() ?: 0L,
                    sha256 = response.header("X-WC-Sha256").orEmpty(),
                    length = body.contentLength(),
                    chunkSha256 = response.header(TransferPaths.CHUNK_SHA256_HEADER).orEmpty(),
                )

                body.byteStream().use { stream -> consume(stream, meta) }

                ApiOutcome.Success(meta, response.header(ProtocolConstants.TRACE_ID_HEADER).orEmpty(), 0L)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: SSLHandshakeException) {
            ApiOutcome.Failure(NetworkErrorCodes.TLS_PIN_MISMATCH, "证书校验失败，指纹与预期不符", e)
        } catch (e: SocketTimeoutException) {
            ApiOutcome.Failure(NetworkErrorCodes.TIMEOUT, "传输超时", e)
        } catch (e: ConnectException) {
            ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "无法连接到电脑", e)
        } catch (e: UnknownHostException) {
            ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "地址无法解析", e)
        } catch (e: IOException) {
            ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "网络中断：${e.message ?: "未知"}", e)
        }
    }

    private fun buildRequest(path: String, token: String): Request.Builder =
        Request.Builder()
            .url(endpoint.baseUrl + path)
            .header(ProtocolConstants.VERSION_HEADER, ProtocolConstants.CURRENT_VERSION.toString())
            .header("Authorization", "Bearer $token")

    private suspend inline fun <reified T> executeJson(request: Request): ApiOutcome<T> =
        withContext(Dispatchers.IO) {
            try {
                client.newCall(request).execute().use { response ->
                    val text = response.body?.string().orEmpty()
                    if (text.isBlank()) {
                        return@withContext ApiOutcome.Failure(
                            NetworkErrorCodes.BAD_RESPONSE,
                            "服务器返回空响应（HTTP ${response.code}）",
                        )
                    }

                    val parsed = WcJson.instance.decodeFromString<ApiResult<T>>(text)
                    val data = parsed.data
                    if (parsed.ok && data != null) {
                        ApiOutcome.Success(data, parsed.traceId, parsed.serverTimeUnixMs)
                    } else {
                        ApiOutcome.Failure(
                            parsed.error?.code ?: NetworkErrorCodes.BAD_RESPONSE,
                            parsed.error?.message ?: "请求失败（HTTP ${response.code}）",
                        )
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: SSLHandshakeException) {
                ApiOutcome.Failure(NetworkErrorCodes.TLS_PIN_MISMATCH, "证书校验失败，指纹与预期不符", e)
            } catch (e: SocketTimeoutException) {
                ApiOutcome.Failure(NetworkErrorCodes.TIMEOUT, "连接超时", e)
            } catch (e: ConnectException) {
                ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "无法连接到电脑", e)
            } catch (e: UnknownHostException) {
                ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "地址无法解析", e)
            } catch (e: IOException) {
                ApiOutcome.Failure(NetworkErrorCodes.CONNECT_FAILED, "网络错误：${e.message ?: "未知"}", e)
            } catch (e: Exception) {
                ApiOutcome.Failure(NetworkErrorCodes.BAD_RESPONSE, "响应解析失败", e)
            }
        }

    private fun failureFromBody(text: String, statusCode: Int): ApiOutcome<Nothing> {
        val parsed = runCatching {
            WcJson.instance.decodeFromString<ApiResult<ChunkAck>>(text)
        }.getOrNull()

        return ApiOutcome.Failure(
            parsed?.error?.code ?: NetworkErrorCodes.BAD_RESPONSE,
            parsed?.error?.message ?: "请求失败（HTTP $statusCode）",
        )
    }
}

