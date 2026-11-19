package com.wirelesscable.app.ui

import android.net.Uri
import com.wirelesscable.app.vm.UploadItem
import com.wirelesscable.app.core.i18n.Str

/**
 * 系统分享递进来、还没选落点的一批东西。
 *
 * `uris` 给当面直连用（对方按 URI 拉），`items` 给电脑上传用——纯文字分享已经被落成
 * 一个 txt 文件，并带上了显示名。`label` 是选择框上那句发的是什么。
 */
data class ShareDraft(
    val uris: List<Uri>,
    val items: List<UploadItem>,
    val label: String,
)
