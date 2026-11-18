package com.wirelesscable.app.core.files

import android.content.Context

/**
 * 按根选提供器。
 *
 * 现在有两类根：用户手选的 SAF 目录（细粒度、可撤销）与整机根（一次放开）。
 * 调用方只拿到 [FileRoot]，不该自己判断该用哪个实现——判断集中在这里，
 * 新增根类型时只改一处。
 */
object FileProviders {

    fun forRoot(context: Context, root: FileRoot): FileProvider =
        if (root.rootId == AllFilesProvider.ROOT_ID) {
            AllFilesProvider(context)
        } else {
            SafFileProvider(context.contentResolver, listOf(root))
        }
}
