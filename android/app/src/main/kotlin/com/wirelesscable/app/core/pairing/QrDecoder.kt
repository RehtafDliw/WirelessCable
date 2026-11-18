package com.wirelesscable.app.core.pairing

import androidx.camera.core.ImageProxy
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer

/**
 * 二维码解码。只用 Y 平面（灰度）做识别，避免 YUV→RGB 转换开销。
 *
 * 用 ZXing core 而不是 Google Play Services 的 ML Kit：本机无 GMS、
 * 无网络下载模型，纯离线解码更符合"局域网内可用、不依赖外部服务"。
 */
class QrDecoder {

    private val reader = MultiFormatReader().apply {
        setHints(
            mapOf(
                DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
                DecodeHintType.TRY_HARDER to true,
                DecodeHintType.CHARACTER_SET to "UTF-8",
            ),
        )
    }

    /** 从相机帧解码，识别不到返回 null。 */
    fun decode(image: ImageProxy): String? {
        val plane = image.planes.firstOrNull() ?: return null
        val buffer = plane.buffer
        val data = ByteArray(buffer.remaining())
        buffer.get(data)
        buffer.rewind()

        return decodeLuminance(
            data = data,
            width = image.width,
            height = image.height,
            rowStride = plane.rowStride,
        )
    }

    /** 纯字节解码，便于单测（不依赖 CameraX）。 */
    fun decodeLuminance(data: ByteArray, width: Int, height: Int, rowStride: Int): String? {
        if (width <= 0 || height <= 0 || rowStride < width) {
            return null
        }

        return try {
            val source = PlanarYUVLuminanceSource(data, rowStride, height, 0, 0, width, height, false)
            val bitmap = BinaryBitmap(HybridBinarizer(source))
            reader.decodeWithState(bitmap).text
        } catch (e: Exception) {
            // NotFoundException 是常态（画面里没有码），不必记录
            null
        } finally {
            reader.reset()
        }
    }
}
