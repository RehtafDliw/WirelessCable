package com.wirelesscable.app.core.network

/** 连接结果。UI 只消费这个类型，不接触 OkHttp/异常细节。 */
sealed interface ApiOutcome<out T> {

    data class Success<T>(val value: T, val traceId: String, val serverTimeUnixMs: Long) : ApiOutcome<T>

    /**
     * @param code 稳定错误码；网络层错误用 [NetworkErrorCodes] 里的本地码。
     * @param message 面向用户的短说明。
     */
    data class Failure(val code: String, val message: String, val cause: Throwable? = null) : ApiOutcome<Nothing>
}

/** 本地产生的错误码（不属于服务端 ErrorCodes）。 */
object NetworkErrorCodes {
    const val CONNECT_FAILED = "connect_failed"
    const val TLS_PIN_MISMATCH = "tls_pin_mismatch"
    const val TIMEOUT = "timeout"
    const val BAD_RESPONSE = "bad_response"
    const val INVALID_ENDPOINT = "invalid_endpoint"
}
