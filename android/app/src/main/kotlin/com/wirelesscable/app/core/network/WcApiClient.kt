package com.wirelesscable.app.core.network

import com.wirelesscable.app.core.protocol.ApiResult
import com.wirelesscable.app.core.protocol.DeviceInfo
import com.wirelesscable.app.core.protocol.HealthInfo
import com.wirelesscable.app.core.protocol.HeartbeatResponse
import com.wirelesscable.app.core.protocol.PairingChallenge
import com.wirelesscable.app.core.protocol.PairingConfirmRequest
import com.wirelesscable.app.core.protocol.PairingConfirmResponse
import com.wirelesscable.app.core.protocol.PairingRequest
import com.wirelesscable.app.core.protocol.PairingStatus
import com.wirelesscable.app.core.protocol.ProtocolConstants
import com.wirelesscable.app.core.protocol.TrustedDevice
import com.wirelesscable.app.core.protocol.WcJson
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

/** 服务器地址。 */
data class ServerEndpoint(val host: String, val port: Int) {

    val baseUrl: String get() = "https://$host:$port"

    companion object {
        /** 支持 "192.168.1.10"、"[fe80::1]" 与 "192.168.1.10:47110"。 */
        fun parse(rawHost: String, rawPort: String): ServerEndpoint? {
            val hostText = rawHost.trim().removePrefix("https://").removeSuffix("/")
            if (hostText.isEmpty()) return null

            var host = hostText
            var port = rawPort.trim().toIntOrNull() ?: ProtocolConstants.DEFAULT_HTTPS_PORT

            if (hostText.count { it == ':' } == 1 && !hostText.startsWith("[")) {
                val parts = hostText.split(':')
                host = parts[0]
                port = parts[1].toIntOrNull() ?: return null
            }

            host = host.trim('[', ']')
            if (host.isEmpty() || port !in 1..65535) return null

            return ServerEndpoint(host, port)
        }
    }
}

/**
 * Windows 服务端客户端：健康检查、设备信息、配对、设备管理、心跳。
 * 证书校验完全交给 [PinnedTrustManager]，不存在"信任所有证书"的降级分支。
 */
class WcApiClient private constructor(
    private val endpoint: ServerEndpoint,
    private val client: OkHttpClient,
) {

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        fun create(endpoint: ServerEndpoint, pins: Set<String>): WcApiClient {
            val trustManager = PinnedTrustManager(pins)
            val sslContext = SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<TrustManager>(trustManager), SecureRandom())
            }

            val client = OkHttpClient.Builder()
                .sslSocketFactory(sslContext.socketFactory, trustManager)
                // 自签证书 CN 与局域网 IP 不对应；身份由固定指纹保证
                .hostnameVerifier { _, _ -> true }
                .connectTimeout(6, TimeUnit.SECONDS)
                .readTimeout(15, TimeUnit.SECONDS)
                .callTimeout(20, TimeUnit.SECONDS)
                .retryOnConnectionFailure(false)
                .build()

            return WcApiClient(endpoint, client)
        }
    }

    /**
     * 健康检查。匿名可调但只得到基础字段；带令牌才拿到运行时长、数据库状态等细节。
     * 令牌无效/已撤销时服务端直接回 401/403，不会悄悄降级成匿名。
     */
    suspend fun health(token: String? = null): ApiOutcome<HealthInfo> =
        get(ProtocolConstants.HEALTH_PATH, token)

    /** 设备信息。服务端要求设备令牌，未配对调用会得到 401。 */
    suspend fun deviceInfo(token: String): ApiOutcome<DeviceInfo> =
        get(ProtocolConstants.DEVICE_INFO_PATH, token)

    suspend fun pairRequest(request: PairingRequest): ApiOutcome<PairingChallenge> =
        post(ProtocolConstants.PAIR_REQUEST_PATH, WcJson.instance.encodeToString(request))

    suspend fun pairConfirm(request: PairingConfirmRequest): ApiOutcome<PairingConfirmResponse> =
        post(ProtocolConstants.PAIR_CONFIRM_PATH, WcJson.instance.encodeToString(request))

    suspend fun pairStatus(requestId: String): ApiOutcome<PairingStatus> =
        get(ProtocolConstants.PAIR_STATUS_PATH + requestId)

    suspend fun devices(token: String): ApiOutcome<List<TrustedDevice>> =
        get(ProtocolConstants.DEVICES_PATH, token)

    suspend fun heartbeat(token: String): ApiOutcome<HeartbeatResponse> =
        post(ProtocolConstants.HEARTBEAT_PATH, "{}", token)

    private suspend inline fun <reified T> get(path: String, token: String? = null): ApiOutcome<T> =
        execute(buildRequest(path, token).get().build())

    private suspend inline fun <reified T> post(path: String, body: String, token: String? = null): ApiOutcome<T> =
        execute(buildRequest(path, token).post(body.toRequestBody(JSON_MEDIA)).build())

    private fun buildRequest(path: String, token: String?): Request.Builder {
        val builder = Request.Builder()
            .url(endpoint.baseUrl + path)
            .header(ProtocolConstants.VERSION_HEADER, ProtocolConstants.CURRENT_VERSION.toString())

        if (!token.isNullOrBlank()) {
            builder.header("Authorization", "Bearer $token")
        }

        return builder
    }

    private suspend inline fun <reified T> execute(request: Request): ApiOutcome<T> = withContext(Dispatchers.IO) {
        try {
            client.newCall(request).execute().use { response ->
                val body = response.body?.string().orEmpty()
                if (body.isBlank()) {
                    return@withContext ApiOutcome.Failure(
                        NetworkErrorCodes.BAD_RESPONSE,
                        "服务器返回空响应（HTTP ${response.code}）",
                    )
                }

                val parsed = WcJson.instance.decodeFromString<ApiResult<T>>(body)
                val data = parsed.data
                if (parsed.ok && data != null) {
                    ApiOutcome.Success(data, parsed.traceId, parsed.serverTimeUnixMs)
                } else {
                    ApiOutcome.Failure(
                        code = parsed.error?.code ?: NetworkErrorCodes.BAD_RESPONSE,
                        message = parsed.error?.message ?: "请求失败（HTTP ${response.code}）",
                    )
                }
            }
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
}
