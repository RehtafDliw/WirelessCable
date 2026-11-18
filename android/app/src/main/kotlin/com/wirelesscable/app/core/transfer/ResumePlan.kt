package com.wirelesscable.app.core.transfer

import com.wirelesscable.app.core.protocol.ChunkInfo

/** 恢复时对一个分块要做的动作。 */
enum class ChunkAction {
    /** 服务端已完成、本地没记上：补记，不重传。 */
    MARK_LOCAL_COMPLETED,

    /** 本地写完但服务端不知道：验哈希补回执，不一致再重传。 */
    VERIFY_AND_ACK,

    /** 双方都没有：重新拉取。 */
    FETCH,
}

data class ReconcileStep(val index: Int, val action: ChunkAction)

/**
 * 恢复决策的纯逻辑：给服务端位图和本地已完成集合，输出每个分块的动作。
 * 服务端位图是唯一事实来源；本地记录只在"服务端未知"时提供一个补回执的机会。
 */
fun planReconcile(
    serverChunks: List<ChunkInfo>,
    localCompleted: Set<Int>,
): List<ReconcileStep> {
    val steps = mutableListOf<ReconcileStep>()

    for (chunk in serverChunks) {
        val local = chunk.index in localCompleted
        val action = when {
            chunk.isCompleted && !local -> ChunkAction.MARK_LOCAL_COMPLETED
            !chunk.isCompleted && local -> ChunkAction.VERIFY_AND_ACK
            !chunk.isCompleted && !local -> ChunkAction.FETCH
            else -> null // 双方一致：不动
        }
        if (action != null) {
            steps += ReconcileStep(chunk.index, action)
        }
    }

    return steps
}

/** 续传时还需传输的分块（服务端位图里的 pending）。 */
fun pendingChunks(serverChunks: List<ChunkInfo>): List<ChunkInfo> =
    serverChunks.filter { !it.isCompleted }
