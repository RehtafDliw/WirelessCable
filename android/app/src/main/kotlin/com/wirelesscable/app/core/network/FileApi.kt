package com.wirelesscable.app.core.network

import com.wirelesscable.app.core.protocol.ApiResult
import com.wirelesscable.app.core.protocol.DeviceFileIndexDto
import com.wirelesscable.app.core.protocol.DeviceFileSyncDto
import com.wirelesscable.app.core.protocol.FileSyncResult
import com.wirelesscable.app.core.protocol.FileBatchRequest
import com.wirelesscable.app.core.protocol.FileBatchResult
import com.wirelesscable.app.core.protocol.FileCopyRequest
import com.wirelesscable.app.core.protocol.FileCreateDirectoryRequest
import com.wirelesscable.app.core.protocol.FileDownloadRequest
import com.wirelesscable.app.core.protocol.FolderDownloadResult
import com.wirelesscable.app.core.protocol.FileDeleteRequest
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileJobDto
import com.wirelesscable.app.core.protocol.FileJobRequest
import com.wirelesscable.app.core.protocol.FileJobResult
import com.wirelesscable.app.core.protocol.FileListingDto
import com.wirelesscable.app.core.protocol.FileMoveRequest
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.FileRenameRequest
import com.wirelesscable.app.core.protocol.FileRootDto
import com.wirelesscable.app.core.protocol.JobAckDto
import com.wirelesscable.app.core.protocol.JobLeaseDto
import com.wirelesscable.app.core.protocol.JobTaskAttachRequest
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.TransferTask
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.TrustManager

/**
 * 文件管理客户端。与 [TransferApi] 分开：文件操作请求体小、超时短，
 * 不需要传输那套长连接配置。指纹固定策略完全一致。
 */
class FileApi private constructor(
    private val endpoint: ServerEndpoint,
    private val client: OkHttpClient,
) {

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        /**
         * 取作业请求的挂起时长。服务端上限 5 秒，这里取 3 秒：
         * 比读超时（15 秒）留足余量，同时保证一次挂起不会跨过一个心跳周期。
         */
        private const val DEFAULT_JOB_WAIT_MS = 3_000

        fun create(endpoint: ServerEndpoint, pins: Set<String>): FileApi {
            val trustManager = PinnedTrustManager(pins)
            val sslContext = SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<TrustManager>(trustManager), SecureRandom())
            }

            val client = OkHttpClient.Builder()
                .sslSocketFactory(sslContext.socketFactory, trustManager)
                .hostnameVerifier { _, _ -> true }
                .connectTimeout(8, TimeUnit.SECONDS)
                .readTimeout(30, TimeUnit.SECONDS)
                .writeTimeout(30, TimeUnit.SECONDS)
                .callTimeout(40, TimeUnit.SECONDS)
                .retryOnConnectionFailure(false)
                .build()

            return FileApi(endpoint, client)
        }
    }

    /** 列出可见的根：被授权的共享目录 + 手机自己授权给电脑的目录。 */
    suspend fun roots(token: String): ApiOutcome<List<FileRootDto>> = withContext(Dispatchers.IO) {
        executeJson(buildRequest("${FileProtocol.FS_PATH}/roots", token).get().build())
    }

    /** 列出目录内容。 */
    suspend fun list(token: String, rootId: String, path: String): ApiOutcome<FileListingDto> =
        withContext(Dispatchers.IO) {
            val url = buildRequest("${FileProtocol.FS_PATH}/entries", token)
                .url(
                    endpoint.baseUrl +
                        "${FileProtocol.FS_PATH}/entries?rootId=" +
                        java.net.URLEncoder.encode(rootId, "UTF-8") +
                        "&path=" +
                        java.net.URLEncoder.encode(path, "UTF-8"),
                )
                .get()
                .build()
            executeJson(url)
        }

    /** 查询单个条目。 */
    suspend fun info(token: String, rootId: String, path: String): ApiOutcome<FileEntryDto> =
        withContext(Dispatchers.IO) {
            val url = buildRequest("${FileProtocol.FS_PATH}/info", token)
                .url(
                    endpoint.baseUrl +
                        "${FileProtocol.FS_PATH}/info?rootId=" +
                        java.net.URLEncoder.encode(rootId, "UTF-8") +
                        "&path=" +
                        java.net.URLEncoder.encode(path, "UTF-8"),
                )
                .get()
                .build()
            executeJson(url)
        }

    suspend fun createDirectory(
        token: String,
        rootId: String,
        parentPath: String,
        name: String,
    ): ApiOutcome<Boolean> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileCreateDirectoryRequest(rootId, parentPath, name))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/mkdir", token).post(body).build())
    }

    suspend fun rename(token: String, rootId: String, path: String, newName: String): ApiOutcome<Boolean> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance
                .encodeToString(FileRenameRequest(rootId, path, newName))
                .toRequestBody(JSON_MEDIA)
            executeJson(buildRequest("${FileProtocol.FS_PATH}/rename", token).post(body).build())
        }

    suspend fun delete(
        token: String,
        rootId: String,
        path: String,
        recursive: Boolean,
    ): ApiOutcome<Boolean> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileDeleteRequest(rootId, path, recursive))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/delete", token).post(body).build())
    }

    suspend fun move(
        token: String,
        rootId: String,
        sourcePath: String,
        destinationPath: String,
    ): ApiOutcome<Boolean> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileMoveRequest(rootId, sourcePath, destinationPath))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/move", token).post(body).build())
    }

    /** 复制：源不动，目标必须是新名字（服务端拒绝覆盖）。 */
    suspend fun copy(
        token: String,
        rootId: String,
        sourcePath: String,
        destinationPath: String,
    ): ApiOutcome<Boolean> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileCopyRequest(rootId, sourcePath, destinationPath))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/copy", token).post(body).build())
    }

    suspend fun batchDelete(
        token: String,
        rootId: String,
        paths: List<String>,
        recursive: Boolean,
    ): ApiOutcome<List<FileBatchResult>> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileBatchRequest(rootId, paths, recursive))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/batch-delete", token).post(body).build())
    }

    /** 上报授权目录索引。 */
    suspend fun publishIndex(token: String, index: DeviceFileIndexDto): ApiOutcome<FileRootDto> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance.encodeToString(index).toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(FileProtocol.DEVICE_INDEX_PATH, token).post(body).build())
        }

    /**
     * 只同步根的集合，不上报条目。
     *
     * 授权被**全部**撤销时没有任何索引可以上报，没有这个调用服务端就会一直保留
     * 那几条已经不存在的授权目录（电脑端和手机端都会显示）。
     */
    suspend fun syncIndexes(token: String, activeRootIds: List<String>): ApiOutcome<FileSyncResult> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance
                .encodeToString(DeviceFileSyncDto(activeRootIds))
                .toRequestBody(JSON_MEDIA)
            executeJson(buildRequest(FileProtocol.DEVICE_INDEX_SYNC_PATH, token).post(body).build())
        }

    /**
     * 取待执行作业。
     *
     * [waitMs] 是长轮询等待时长：服务端没有作业时把请求挂住，一有作业立刻返回。
     * 原来 3 秒一轮的空转，平均要等 1.5 秒才开始执行；挂起式轮询把这段压到一次往返。
     */
    suspend fun takeJobs(
        token: String,
        waitMs: Int = DEFAULT_JOB_WAIT_MS,
    ): ApiOutcome<List<FileJobDto>> = withContext(Dispatchers.IO) {
        val path = if (waitMs > 0) {
            "${FileProtocol.DEVICE_JOBS_PATH}?wait=$waitMs"
        } else {
            FileProtocol.DEVICE_JOBS_PATH
        }

        executeJson(buildRequest(path, token).get().build())
    }

    /** 回报作业结果。 */
    suspend fun completeJob(token: String, jobId: String, result: FileJobResult): ApiOutcome<FileJobDto> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance.encodeToString(result).toRequestBody(JSON_MEDIA)
            executeJson(
                buildRequest("${FileProtocol.DEVICE_JOBS_PATH}/$jobId/result", token)
                    .post(body)
                    .build(),
            )
        }

    /**
     * 续租 + 取控制指令。
     *
     * 返回作业当前状态：`running` 继续，`paused`/`cancelled` 立刻停手，
     * `gone` 表示作业已经不在队列里。暂停/取消就是这样"打到"手机上的。
     */
    suspend fun leaseJob(token: String, jobId: String): ApiOutcome<JobLeaseDto> =
        withContext(Dispatchers.IO) {
            val empty = ByteArray(0).toRequestBody(JSON_MEDIA)
            executeJson(
                buildRequest("${FileProtocol.DEVICE_JOBS_PATH}/$jobId/lease", token)
                    .post(empty)
                    .build(),
            )
        }

    /** 把建出来的传输任务挂到作业上：暂停后继续要认出上次那个任务，别重复建。 */
    suspend fun attachJobTask(token: String, jobId: String, taskId: String): ApiOutcome<JobAckDto> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance
                .encodeToString(JobTaskAttachRequest(taskId))
                .toRequestBody(JSON_MEDIA)
            executeJson(
                buildRequest("${FileProtocol.DEVICE_JOBS_PATH}/$jobId/task", token)
                    .post(body)
                    .build(),
            )
        }

    /** 下发一个作业（手机侧极少用，主要是测试与调试）。 */
    suspend fun createJob(token: String, request: FileJobRequest): ApiOutcome<FileJobDto> =
        withContext(Dispatchers.IO) {
            val body = WcJson.instance.encodeToString(request).toRequestBody(JSON_MEDIA)
            executeJson(buildRequest("${FileProtocol.FS_PATH}/jobs", token).post(body).build())
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
    /**
     * 请求下载某个根下的文件。服务端按 rootId + path 定位源文件并建下载任务，
     * 手机随后用既有的分块通道接收（阶段 3/4 已验证）。
     *
     * 手机只给相对路径：绝对路径由服务端解析，手机无权指定电脑上的任意文件。
     */
    suspend fun download(
        token: String,
        rootId: String,
        path: String,
    ): ApiOutcome<TransferTask> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileDownloadRequest(rootId, path))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/download", token).post(body).build())
    }

    /**
     * 下载整个文件夹：服务端递归展开成一批任务，返回批次信息。
     *
     * 手机不列目录、不逐个建任务——那样每文件一次 HTTP，几十个文件就先卡住了。
     */
    suspend fun downloadFolder(
        token: String,
        rootId: String,
        path: String,
    ): ApiOutcome<FolderDownloadResult> = withContext(Dispatchers.IO) {
        val body = WcJson.instance
            .encodeToString(FileDownloadRequest(rootId, path))
            .toRequestBody(JSON_MEDIA)
        executeJson(buildRequest("${FileProtocol.FS_PATH}/download-folder", token).post(body).build())
    }

}
