package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.TransferTask
import com.wirelesscable.app.core.protocol.ApiResult
import com.wirelesscable.app.core.protocol.ChunkAck
import com.wirelesscable.app.core.protocol.TransferDirections
import com.wirelesscable.app.core.protocol.TransferStates
import com.wirelesscable.app.core.protocol.TransferPaths
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 手机端对传输契约的解析。基准 JSON 与 Windows 端 TransferEndpointTests 使用同一字段名，
 * 任一端改名都会在这里立刻暴露。
 */
class TransferProtocolTest {

    @Test
    fun pathsMatchContract() {
        assertEquals("/api/v1/transfer/uploads", TransferPaths.UPLOADS)
        assertEquals("/api/v1/transfer/tasks", TransferPaths.TASKS)
        assertEquals("/api/v1/transfer/tasks/abc/chunks/7", TransferPaths.chunk("abc", 7))
        assertEquals("/api/v1/transfer/tasks/abc/complete", TransferPaths.complete("abc"))
        assertEquals("/api/v1/transfer/tasks/abc/ack", TransferPaths.ack("abc"))
        assertEquals("/api/v1/transfer/tasks/abc/cancel", TransferPaths.cancel("abc"))
    }

    @Test
    fun taskFixtureParsesEveryField() {
        val json =
            """{"taskId":"t-1","direction":"download","deviceName":"客厅电脑","fileName":"报告 2024.pdf","relativePath":"报告 2024.pdf","totalBytes":1048576,"transferredBytes":524288,"chunkSize":8388608,"chunkCount":1,"state":"transferring","sha256":"ab12","errorCode":null,"createdAtUnixMs":1700000000000,"updatedAtUnixMs":1700000001000}"""

        val task = WcJson.instance.decodeFromString<TransferTask>(json)

        assertEquals("t-1", task.taskId)
        assertEquals(TransferDirections.DOWNLOAD, task.direction)
        assertEquals("报告 2024.pdf", task.fileName)
        assertEquals(1_048_576L, task.totalBytes)
        assertEquals(524_288L, task.transferredBytes)
        assertEquals(8_388_608, task.chunkSize)
        assertEquals(TransferStates.TRANSFERRING, task.state)
        assertNull(task.errorMessage)
        assertTrue(TransferStates.isActive(task.state))
    }

    @Test
    fun taskRoundTripKeepsNumbers() {
        val task = TransferTask(
            taskId = "t-2",
            direction = TransferDirections.UPLOAD,
            fileName = "a b#c?.bin",
            totalBytes = 5L * 1024 * 1024 * 1024,
            chunkCount = 640,
            chunkSize = 8 * 1024 * 1024,
            state = TransferStates.COMPLETED,
            sha256 = "ff",
        )

        val restored = WcJson.instance.decodeFromString<TransferTask>(WcJson.instance.encodeToString(task))

        // 5 GiB 必须精确，不能被转成 Double 丢精度
        assertEquals(5L * 1024 * 1024 * 1024, restored.totalBytes)
        assertEquals(640, restored.chunkCount)
        assertEquals("a b#c?.bin", restored.fileName)
    }

    @Test
    fun chunkAckParsesNextIndex() {
        val json =
            """{"taskId":"t-3","index":0,"bytesThisChunk":8388608,"transferredBytes":8388608,"totalBytes":16777216,"nextIndex":1}"""

        val ack = WcJson.instance.decodeFromString<ChunkAck>(json)

        assertEquals(0, ack.index)
        assertEquals(1, ack.nextIndex)
        assertEquals(16_777_216L, ack.totalBytes)
    }

    @Test
    fun envelopeCarriesTaskList() {
        val json =
            """{"ok":true,"traceId":"trace-1","serverTimeUnixMs":1700000000000,"data":[{"taskId":"t-4","direction":"upload","fileName":"x.txt","totalBytes":10,"state":"waiting"}]}"""

        val envelope = WcJson.instance.decodeFromString<ApiResult<List<TransferTask>>>(json)

        assertTrue(envelope.ok)
        val list = envelope.data
        assertEquals(1, list?.size)
        assertEquals("x.txt", list!![0].fileName)
        // 缺省字段必须有稳定默认值，避免 null 传播到界面
        assertEquals(0, list[0].chunkCount)
        assertEquals(TransferDirections.UPLOAD, list[0].direction)
    }
}

/** 阶段 4 新增契约：分块位图、恢复字段与冲突策略。 */
class TransferProtocolStage4Test {

    @Test
    fun pathsCoverResumeAndChunkAck() {
        assertEquals("/api/v1/transfer/tasks/abc/chunks", TransferPaths.chunks("abc"))
        assertEquals("/api/v1/transfer/tasks/abc/chunks/7/ack", TransferPaths.chunkAck("abc", 7))
        assertEquals("/api/v1/transfer/tasks/abc/resume", TransferPaths.resume("abc"))
        assertEquals("X-WC-Chunk-Sha256", TransferPaths.CHUNK_SHA256_HEADER)
    }

    @Test
    fun chunkInfoParsesServerBitmap() {
        val json =
            """[{"transferId":"t","index":0,"offset":0,"length":8388608,"sha256":"ab","status":"completed"},{"transferId":"t","index":1,"offset":8388608,"length":123,"sha256":null,"status":"pending"}]"""

        val chunks = WcJson.instance.decodeFromString<List<com.wirelesscable.app.core.protocol.ChunkInfo>>(json)

        assertEquals(2, chunks.size)
        assertTrue(chunks[0].isCompleted)
        assertEquals(8_388_608L, chunks[1].offset)
        assertEquals(123, chunks[1].length)
        assertNull(chunks[1].sha256)
    }

    @Test
    fun taskWithCompletedChunksParses() {
        val json =
            """{"taskId":"t-9","direction":"upload","deviceName":"手机","fileName":"a.bin","relativePath":"a.bin","totalBytes":100,"transferredBytes":50,"chunkSize":65536,"chunkCount":2,"state":"interrupted","completedChunks":[0],"createdAtUnixMs":1,"updatedAtUnixMs":2}"""

        val task = WcJson.instance.decodeFromString<TransferTask>(json)

        assertEquals(listOf(0), task.completedChunks)
        assertEquals(TransferStates.INTERRUPTED, task.state)
        assertTrue(TransferStates.isResumable(task.state))
        assertTrue(!TransferStates.isActive(task.state))
        assertTrue(!TransferStates.isTerminal(task.state))
    }

    @Test
    fun startRequestCarriesConflict() {
        val request = com.wirelesscable.app.core.protocol.TransferStartRequest(
            fileName = "a.bin",
            totalBytes = 10,
            conflict = com.wirelesscable.app.core.protocol.TransferConflictPolicies.RENAME,
        )
        val json = WcJson.instance.encodeToString(request)
        assertTrue(json.contains("\"conflict\":\"rename\""))
    }

    @Test
    fun chunkAckRequestSerializes() {
        val json = WcJson.instance.encodeToString(
            com.wirelesscable.app.core.protocol.DownloadChunkAckRequest("ab".repeat(32)),
        )
        assertTrue(json.contains("\"sha256\":\"abab"))
    }
}
