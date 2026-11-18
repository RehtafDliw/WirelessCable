package com.wirelesscable.app

import com.wirelesscable.app.core.protocol.FileEntryDto
import com.wirelesscable.app.core.protocol.FileProtocol
import com.wirelesscable.app.core.protocol.TransferStartRequest
import com.wirelesscable.app.vm.FileBrowserUiState
import com.wirelesscable.app.vm.FileSortBy
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 阶段 5 补充：上传落点、排序、文件属性的协议与逻辑测试。
 */
class FileUploadAndSortTest {

    private fun entry(
        name: String,
        path: String,
        directory: Boolean = false,
        size: Long = 0,
        modified: Long = 0,
        capabilities: Int = 0,
    ) = FileEntryDto(
        name = name,
        relativePath = path,
        isDirectory = directory,
        sizeBytes = size,
        modifiedUnixMs = modified,
        capabilities = capabilities,
    )

    // ------------------------------------------------------------ 排序

    @Test
    fun sortByName_placesDirectoriesFirstThenCaseInsensitiveName() {
        val state = FileBrowserUiState(
            entries = listOf(
                entry("b.txt", "b.txt"),
                entry("A.txt", "A.txt"),
                entry("zdir", "zdir", directory = true),
                entry("Adir", "Adir", directory = true),
            ),
            sortBy = FileSortBy.NAME,
        )

        assertEquals(
            listOf("Adir", "zdir", "A.txt", "b.txt"),
            state.visibleEntries.map { it.name },
        )
    }

    @Test
    fun sortBySize_ordersAscendingWithinSameKind() {
        val state = FileBrowserUiState(
            entries = listOf(
                entry("big.bin", "big.bin", size = 900),
                entry("small.bin", "small.bin", size = 10),
                entry("mid.bin", "mid.bin", size = 500),
            ),
            sortBy = FileSortBy.SIZE,
        )

        assertEquals(
            listOf("small.bin", "mid.bin", "big.bin"),
            state.visibleEntries.map { it.name },
        )
    }

    @Test
    fun sortByModified_ordersByTimestamp() {
        val state = FileBrowserUiState(
            entries = listOf(
                entry("c.txt", "c.txt", modified = 300),
                entry("a.txt", "a.txt", modified = 100),
                entry("b.txt", "b.txt", modified = 200),
            ),
            sortBy = FileSortBy.MODIFIED,
        )

        assertEquals(
            listOf("a.txt", "b.txt", "c.txt"),
            state.visibleEntries.map { it.name },
        )
    }

    @Test
    fun sortByType_ordersByExtension() {
        val state = FileBrowserUiState(
            entries = listOf(
                entry("b.zip", "b.zip"),
                entry("a.txt", "a.txt"),
                entry("c.txt", "c.txt"),
            ),
            sortBy = FileSortBy.TYPE,
        )

        // 按扩展名排：txt 在 zip 前；同扩展名保持原顺序（稳定排序）
        assertEquals(
            listOf("a.txt", "c.txt", "b.zip"),
            state.visibleEntries.map { it.name },
        )
    }

    @Test
    fun searchFiltersEntriesByName() {
        val state = FileBrowserUiState(
            entries = listOf(
                entry("report.pdf", "report.pdf"),
                entry("notes.txt", "notes.txt"),
            ),
            searchKeyword = "REPORT",
        )

        assertEquals(listOf("report.pdf"), state.visibleEntries.map { it.name })
    }

    // ------------------------------------------------------------ 上传落点

    @Test
    fun uploadRequest_carriesDirectoryIdAndRelativeDirectory() {
        val request = TransferStartRequest(
            fileName = "a.txt",
            totalBytes = 10,
            directoryId = "dir-1",
            relativeDirectory = "sub",
        )

        assertEquals("dir-1", request.directoryId)
        assertEquals("sub", request.relativeDirectory)
    }

    @Test
    fun uploadRequest_withoutDirectory_usesDefaultReceiver() {
        val request = TransferStartRequest(fileName = "a.txt", totalBytes = 10)

        assertEquals(null, request.directoryId)
        assertEquals(null, request.relativeDirectory)
    }

    // ------------------------------------------------------------ 可上传判定

    @Test
    fun canUploadHere_isTrueForSharedRootWithUploadPermission() {
        val state = FileBrowserUiState(
            roots = listOf(
                FileProtocol.let {
                    com.wirelesscable.app.core.protocol.FileRootDto(
                        rootId = "dir-1",
                        kind = FileProtocol.KIND_SHARED,
                        displayName = "共享",
                        permissions = 1 or 2 or 4,
                    )
                },
            ),
            currentRootId = "dir-1",
        )

        assertTrue(state.canUploadHere)
    }

    @Test
    fun canUploadHere_isFalseWithoutUploadPermission() {
        val state = FileBrowserUiState(
            roots = listOf(
                com.wirelesscable.app.core.protocol.FileRootDto(
                    rootId = "dir-1",
                    kind = FileProtocol.KIND_SHARED,
                    displayName = "只读共享",
                    permissions = 1 or 2,
                ),
            ),
            currentRootId = "dir-1",
        )

        assertFalse(state.canUploadHere)
    }

    @Test
    fun canUploadHere_isFalseForMachineRoot() {
        val state = FileBrowserUiState(
            roots = listOf(
                com.wirelesscable.app.core.protocol.FileRootDto(
                    rootId = FileProtocol.ROOT_MACHINE,
                    kind = FileProtocol.KIND_MACHINE,
                    displayName = "此电脑",
                    permissions = 1 or 2 or 4 or 8,
                ),
            ),
            currentRootId = FileProtocol.ROOT_MACHINE,
        )

        assertFalse(state.canUploadHere)
    }

    // ------------------------------------------------------------ 协议常量

    @Test
    fun machineRootConstants_matchServerSide() {
        assertEquals("machine", FileProtocol.KIND_MACHINE)
        assertEquals("machine", FileProtocol.ROOT_MACHINE)
    }
}
