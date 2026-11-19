package com.wirelesscable.app.ui.components

/**
 * 电脑在界面上的显示名：`机器名 · 短ID`。
 *
 * 为什么需要：显示名取的是电脑的机器名，两台电脑完全可能同名（都叫 DESKTOP-XXXX），
 * 手机列表里就会并排出现两个一模一样的条目，用户不知道自己连的是哪一台。
 * 短 ID 取自 deviceId 前 4 位，和电脑端广播/二维码里用的是同一段。
 *
 * 服务端 2.10.0 起自己广播的名字里已经带了短 ID；这个函数兜住**旧配对记录**
 * （它们存的是当时纯机器名）。已带短 ID 的不重复追加。
 */
fun deviceLabel(name: String, deviceId: String): String {
    val short = deviceId.take(4).uppercase()
    if (short.isEmpty()) return name
    return if (name.endsWith(short)) name else "$name · $short"
}
