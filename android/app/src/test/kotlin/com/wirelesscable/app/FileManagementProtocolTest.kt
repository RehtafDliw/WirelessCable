package com.wirelesscable.app

import com.wirelesscable.app.core.files.FileCapabilities
import com.wirelesscable.app.core.files.FileResult
import com.wirelesscable.app.core.files.FileStatus
import com.wirelesscable.app.core.files.SafFileProvider
import com.wirelesscable.app.core.protocol.DeviceFileIndexDto
import com.wirelesscable.app.core.protocol.FileBatchResult
import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileJobDto
import com.wirelesscable.app.core.protocol.FileJobResult
import com.wirelesscable.app.core.protocol.FileListingDto
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.FileRootDto
import com.wirelesscable.app.core.protocol.WcJson
import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 阶段 5 文件管理协议与路径逻辑测试。
 *
 * 覆盖：路径规范化、协议序列化与 C# 端字段名一致、失败原因码稳定。
 */
class FileManagementProtocolTest {

    @Test
    fun normalize_stripsEmptyAndDotSegments() {
        assertEquals("a/b", SafFileProvider.normalize("/a//b/"))
        assertEquals("a/b", SafFileProvider.normalize("./a/b"))
        assertEquals("", SafFileProvider.normalize(""))
        assertEquals("", SafFileProvider.normalize(null))
    }

    @Test
    fun normalize_convertsBackslashToSlash() {
        assertEquals("a/b", SafFileProvider.normalize("a\\b"))
    }

    @Test
    fun parentOf_returnsEmptyForRootLevel() {
        assertEquals("", SafFileProvider.parentOf("a.txt"))
        assertEquals("sub", SafFileProvider.parentOf("sub/a.txt"))
        assertEquals("a/b", SafFileProvider.parentOf("a/b/c.txt"))
    }

    @Test
    fun nameOf_returnsLastSegment() {
        assertEquals("a.txt", SafFileProvider.nameOf("a.txt"))
        assertEquals("c.txt", SafFileProvider.nameOf("a/b/c.txt"))
    }

    @Test
    fun capabilityBits_matchServerDefinition() {
        // 数值必须与 C# 端 FileProviderCapabilities 一致，改一处就崩
        assertEquals(1, FileCapabilities.LIST_DIRECTORY)
        assertEquals(2, FileCapabilities.READ_FILE)
        assertEquals(4, FileCapabilities.WRITE_FILE)
        assertEquals(8, FileCapabilities.CREATE_DIRECTORY)
        assertEquals(16, FileCapabilities.RENAME)
        assertEquals(32, FileCapabilities.DELETE)
        assertEquals(64, FileCapabilities.MOVE)
        assertEquals(128, FileCapabilities.GET_FILE_INFO)
        assertEquals(255, FileCapabilities.ALL)
    }

    @Test
    fun fileRoot_serializesWithCamelCaseFields() {
        val dto = FileRootDto(
            rootId = "root-1",
            kind = FileProtocol.KIND_SHARED,
            displayName = "共享",
            deviceId = "dev",
            capabilities = FileCapabilities.ALL,
            permissions = 3,
        )

        val json = WcJson.instance.encodeToString(dto)

        assertTrue(json.contains("\"rootId\""))
        assertTrue(json.contains("\"displayName\""))
        assertTrue(json.contains("\"capabilities\""))
        assertTrue(json.contains("\"permissions\""))
    }

    @Test
    fun deviceFileIndex_roundTrips() {
        val index = DeviceFileIndexDto(
            rootId = "root-1",
            displayName = "内部存储",
            providerKind = "saf",
            capabilities = FileCapabilities.ALL,
            entries = listOf(
                FileEntryDto("照片", "照片", true, 0, 0),
                FileEntryDto("a.txt", "a.txt", false, 100, 1700000000000),
            ),
        )

        val decoded = WcJson.instance.decodeFromString<DeviceFileIndexDto>(
            WcJson.instance.encodeToString(index),
        )

        assertEquals("root-1", decoded.rootId)
        assertEquals(2, decoded.entries.size)
        assertTrue(decoded.entries[0].isDirectory)
        assertEquals(100L, decoded.entries[1].sizeBytes)
    }

    @Test
    fun fileJob_keepsTransferTaskLink() {
        val job = FileJobDto(
            jobId = "job-1",
            rootId = "root-1",
            operation = FileProtocol.OP_DOWNLOAD,
            sourcePath = "a.txt",
            transferTaskId = "task-9",
            destinationDirectoryId = "dir-2",
            status = FileProtocol.JOB_PENDING,
        )

        val decoded = WcJson.instance.decodeFromString<FileJobDto>(
            WcJson.instance.encodeToString(job),
        )

        assertEquals("task-9", decoded.transferTaskId)
        assertEquals("dir-2", decoded.destinationDirectoryId)
        assertEquals(FileProtocol.OP_DOWNLOAD, decoded.operation)
    }

    @Test
    fun fileJob_downloadCarriesRelativeDestinationDirectory() {
        // 文件夹保存：电脑把「接收目录下的相对子目录」放在 destinationPath 里，
        // 手机必须原样解出来再交给上传（1.5.59）。
        val job = FileJobDto(
            jobId = "job-2",
            rootId = "root-1",
            operation = FileProtocol.OP_DOWNLOAD,
            sourcePath = "相册/2024/a.jpg",
            destinationPath = "相册/2024",
            displayName = "a.jpg",
            status = FileProtocol.JOB_PENDING,
        )

        val decoded = WcJson.instance.decodeFromString<FileJobDto>(
            WcJson.instance.encodeToString(job),
        )

        assertEquals("相册/2024", decoded.destinationPath)
        assertEquals("相册/2024/a.jpg", decoded.sourcePath)
    }

    @Test
    fun fileJob_withoutDestination_stillDecodes() {
        // 老电脑端不传这个字段：必须解成 null，而不是抛异常
        val decoded = WcJson.instance.decodeFromString<FileJobDto>(
            """{"jobId":"j","rootId":"r","operation":"download","sourcePath":"a.txt"}""",
        )

        assertNull(decoded.destinationPath)
        assertEquals(FileProtocol.OP_DOWNLOAD, decoded.operation)
    }

    @Test
    fun fileJobResult_serializesStatusAndError() {
        val result = FileJobResult(
            jobId = "job-1",
            status = FileProtocol.JOB_FAILED,
            errorCode = "operation_unsupported",
            errorMessage = "该存储提供方不支持此操作。",
        )

        val json = WcJson.instance.encodeToString(result)

        assertTrue(json.contains("\"status\":\"failed\""))
        assertTrue(json.contains("operation_unsupported"))
    }

    @Test
    fun listJobResult_carriesEntriesPayload() {
        // list 作业把目录条目塞进 payload 回传；null 时不输出这个字段（老服务端不认识）
        val payload =
            """[{"name":"deep.txt","relativePath":"a/b/deep.txt","isDirectory":false,"sizeBytes":12,"modifiedUnixMs":0,"capabilities":3}]"""
        val result = FileJobResult(
            jobId = "job-list",
            status = FileProtocol.JOB_COMPLETED,
            payload = payload,
        )

        val json = WcJson.instance.encodeToString(result)
        assertTrue(json.contains("\"payload\""))

        val decoded = WcJson.instance.decodeFromString<FileJobResult>(json)
        assertEquals(payload, decoded.payload)

        // 普通作业不带 payload：字段必须省略，否则等于每次多传一个 null
        val plain = WcJson.instance.encodeToString(
            FileJobResult(jobId = "job-2", status = FileProtocol.JOB_COMPLETED),
        )
        assertTrue(!plain.contains("\"payload\""))
    }

    @Test
    fun listOperationConstantMatchesDesktop() {
        assertEquals("list", FileProtocol.OP_LIST)
    }

    @Test
    fun batchResult_keepsPerItemFailure() {
        val results = listOf(
            FileBatchResult("a.txt", true),
            FileBatchResult("b.txt", false, "permission_denied", "没有删除权限。"),
        )

        val json = WcJson.instance.encodeToString(results)

        assertTrue(json.contains("\"success\":true"))
        assertTrue(json.contains("permission_denied"))
    }

    @Test
    fun fileListing_decodesEntries() {
        val listing = FileListingDto(
            rootId = "root-1",
            path = "sub",
            entries = listOf(FileEntryDto("a.txt", "sub/a.txt", false, 5, 0)),
            capabilities = FileCapabilities.ALL,
        )

        val decoded = WcJson.instance.decodeFromString<FileListingDto>(
            WcJson.instance.encodeToString(listing),
        )

        assertEquals("sub", decoded.path)
        assertEquals("sub/a.txt", decoded.entries[0].relativePath)
    }

    @Test
    fun fileResult_failure_carriesStableReasonCode() {
        val failure: FileResult<Unit> = FileResult.unsupported("该存储提供方不支持此操作。")

        assertTrue(!failure.ok)
        val detail = failure as FileResult.Failure
        assertEquals("operation_unsupported", detail.reasonCode)
        assertEquals(FileStatus.UNSUPPORTED, detail.status)
    }

    @Test
    fun fileResult_success_carriesValue() {
        val success: FileResult<List<String>> = FileResult.success(listOf("a"))

        assertTrue(success.ok)
        assertEquals(listOf("a"), (success as FileResult.Success).value)
        assertNull((success as? FileResult.Failure)?.message)
    }
}
