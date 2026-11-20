#!/usr/bin/env python3
"""接口级回归：造作业 → 暂停 / 恢复 / 取消 → 断言库里和接口里的状态。

它回答的问题只有一个：**这轮改动有没有把别的地方弄坏**。
所以它不进真机、不开界面，只连本机正在跑的实例，用一个 ``__smoke__`` 目录造数据，
跑完清理干净。全程不到两秒。

用法：
    python scripts/smoke-test.py
    python scripts/smoke-test.py --base https://127.0.0.1:47110
    python scripts/smoke-test.py --keep          # 保留测试数据，方便自己看
    python scripts/smoke-test.py --list          # 只列出断言，不执行

退出码：0 全部通过；1 有失败（可直接接进 CI 或 pre-commit）。

设计约束（都是踩过的坑）：
* 直读 SQLite 造数据 —— 作业没有 HTTP 创建端点，而"暂停能不能停住队列"必须
  有真实的 pending 作业才测得出来。
* 断言只看两处：库里的状态、``/app/api`` 的返回。不看界面 —— 界面是它的消费者，
  不是它的来源。
* 每个断言独立报错，一条失败不影响后面继续跑（否则一个问题会掩盖另外四个）。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import ssl
import sys
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone

SMOKE = "__smoke__"
SMOKE_DIR = SMOKE + "/sub"
JOB_COUNT = 8
TASK_COUNT = 8

# 这些状态是"还没结束"，队列控制要能把它们全部接管
OPEN_STATES = ("pending", "paused", "running")


# ---------------------------------------------------------------- 基础设施


class Client:
    """本机端点客户端：自签证书不校验，走 loopback，不带系统代理。"""

    def __init__(self, base: str) -> None:
        context = ssl.create_default_context()
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        # 禁用代理是必须的：Windows 上系统代理会让 127.0.0.1 也走代理，直接 502
        self._open = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPSHandler(context=context),
        )
        self.base = base.rstrip("/")
        self.token = ""

    def _request(self, path: str, method: str = "GET", body: dict | None = None):
        data = json.dumps(body).encode() if body is not None else None
        headers = {"X-WC-Local": self.token}
        if data is not None:
            headers["Content-Type"] = "application/json"

        request = urllib.request.Request(
            self.base + path, data=data, headers=headers, method=method
        )
        try:
            with self._open.open(request, timeout=30) as response:
                raw = response.read()
                return response.status, raw
        except urllib.error.HTTPError as error:
            return error.code, error.read()

    def json(self, path: str, method: str = "GET", body: dict | None = None):
        status, raw = self._request(path, method, body)
        text = raw.decode("utf-8", "replace")
        try:
            payload = json.loads(text) if text else None
        except json.JSONDecodeError:
            payload = None
        return status, payload, text

    def sign_in(self) -> None:
        status, raw = self._request("/app")
        if status != 200:
            raise SystemExit(
                f"连不上 {self.base}/app（HTTP {status}）。先启动 WirelessCable，或用 --base 指定地址。"
            )

        html = raw.decode("utf-8", "replace")
        match = re.search(r"__WC_LOCAL__\s*=\s*['\"]([^'\"]+)['\"]", html)
        if not match:
            raise SystemExit("页面里没找到本机令牌（__WC_LOCAL__），这个实例可能不是新版。")
        self.token = match.group(1)


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def iso_after(seconds: int) -> str:
    return (datetime.now(timezone.utc) + timedelta(seconds=seconds)).isoformat()


class Report:
    def __init__(self) -> None:
        self.passed = 0
        self.failed: list[str] = []

    def check(self, name: str, ok: bool, detail: str = "") -> bool:
        if ok:
            self.passed += 1
            print(f"  ok   {name}")
        else:
            self.failed.append(name)
            print(f"  FAIL {name}" + (f" — {detail}" if detail else ""))
        return ok

    def equal(self, name: str, actual, expected) -> bool:
        return self.check(name, actual == expected, f"期望 {expected!r}，实际 {actual!r}")

    def dump(self) -> int:
        total = self.passed + len(self.failed)
        if self.failed:
            print(f"\n{self.passed}/{total} 通过，{len(self.failed)} 条失败：")
            for item in self.failed:
                print(f"  - {item}")
            return 1
        print(f"\n{self.passed}/{total} 全部通过。")
        return 0


# ---------------------------------------------------------------- 造数据


def _pattern() -> str:
    """SMOKE 里有下划线，而 LIKE 把 `_` 当单字符通配 —— 必须转义，
    否则 `__smoke__%` 会连 `xxsmokeyy...` 一起匹配上。"""
    return SMOKE.replace("_", "\\_") + "%"


def seed(db_path: str, device_id: str) -> list[str]:
    """造一批 pending 作业与对应任务，外加一个带租约的 running 作业。

    返回值是任务的 taskId —— 批量动作要靠它反查 deviceId（服务端不信任前端传的设备）。
    """
    stamp = now_iso()
    task_ids: list[str] = []

    # 上次 --keep 留下的数据会让主键冲突，先清一遍
    cleanup(db_path)

    connection = sqlite3.connect(db_path, timeout=10)
    try:
        with connection:
            for index in range(JOB_COUNT):
                connection.execute(
                    """
                    INSERT INTO FileOperationJobs
                        (job_id, device_id, root_id, operation, source_path, destination_path,
                         display_name, status, created_at_utc, updated_at_utc,
                         batch_total, batch_name, batch_total_bytes)
                    VALUES (?, ?, '', 'download', ?, ?, ?, 'pending', ?, ?, ?, ?, ?)
                    """,
                    (
                        f"smoke-job-{index:02d}",
                        device_id,
                        f"__smoke__/source/f{index}.txt",
                        SMOKE_DIR,
                        f"f{index}.txt",
                        stamp,
                        stamp,
                        JOB_COUNT,
                        SMOKE,
                        JOB_COUNT * 1024,
                    ),
                )

            # 一个正在执行、租约未到期的作业：暂停时它必须一起被接住、租约必须清空
            connection.execute(
                """
                INSERT INTO FileOperationJobs
                    (job_id, device_id, root_id, operation, source_path, destination_path,
                     display_name, status, created_at_utc, updated_at_utc, lease_until_utc)
                VALUES ('smoke-job-running', ?, '', 'download', '__smoke__/source/running.bin',
                        ?, 'running.bin', 'running', ?, ?, ?)
                """,
                (device_id, SMOKE_DIR, stamp, stamp, iso_after(30)),
            )

            for index in range(TASK_COUNT):
                task_id = f"smoke-task-{index:02d}"
                task_ids.append(task_id)
                connection.execute(
                    """
                    INSERT INTO TransferTasks
                        (task_id, direction, device_id, file_name, relative_path,
                         total_bytes, transferred_bytes, chunk_size, status,
                         created_at_utc, updated_at_utc, chunk_count, completed_chunks)
                    VALUES (?, 'download', ?, ?, ?, 1024, 0, 8388608, 'pending', ?, ?, 1, 0)
                    """,
                    (task_id, device_id, f"f{index}.txt", f"{SMOKE_DIR}/f{index}.txt", stamp, stamp),
                )
    finally:
        connection.close()

    return task_ids


def cleanup(db_path: str) -> None:
    connection = sqlite3.connect(db_path, timeout=10)
    try:
        with connection:
            connection.execute(
                "DELETE FROM FileOperationJobs WHERE destination_path LIKE ? ESCAPE '\\'",
                (_pattern(),),
            )
            connection.execute(
                "DELETE FROM TransferTasks WHERE relative_path LIKE ? ESCAPE '\\'",
                (_pattern(),),
            )
    finally:
        connection.close()


def job_counts(db_path: str, statuses: tuple[str, ...]) -> int:
    marks = ",".join("?" * len(statuses))
    connection = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=10)
    try:
        row = connection.execute(
            f"SELECT COUNT(*) FROM FileOperationJobs "
            f"WHERE destination_path LIKE ? ESCAPE '\\' AND status IN ({marks})",
            (_pattern(), *statuses),
        ).fetchone()
        return int(row[0])
    finally:
        connection.close()


def lease_of_running(db_path: str) -> str | None:
    connection = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=10)
    try:
        row = connection.execute(
            "SELECT lease_until_utc FROM FileOperationJobs WHERE job_id = 'smoke-job-running'"
        ).fetchone()
        return None if row is None else row[0]
    finally:
        connection.close()


# ---------------------------------------------------------------- 断言主体


def run(client: Client, report: Report, db_path: str, device_id: str, keep: bool) -> None:
    # --- 1. 服务与库的基本事实 -------------------------------------------
    print("\n[1] 服务与数据库")
    status, payload, text = client.json("/app/api/status")
    report.check("GET /status 返回 200", status == 200, text[:120])
    version = (payload or {}).get("version", "")
    report.check("/status 带版本号", bool(version), repr(version)[:80])

    status, snapshot, text = client.json("/app/api/diagnostics/snapshot")
    if not report.check("GET /diagnostics/snapshot 返回 200", status == 200, text[:160]):
        raise SystemExit("快照接口不可用，后面的断言没有意义。")

    report.equal("schema 版本 >= 17（租约字段已迁移）", int(snapshot.get("schemaVersion", 0)) >= 17, True)
    report.equal("库完整性 quick_check", snapshot.get("integrity"), "ok")

    # 快照里的库路径必须和脚本读到的是同一个，否则下面的断言测的是别的库
    snapshot_db = (snapshot.get("databasePath") or "").replace("\\", "/").lower()
    report.check(
        "快照的库路径与 --db 一致",
        snapshot_db == db_path.replace("\\", "/").lower(),
        f"{snapshot_db} vs {db_path}",
    )

    # --- 2. 造数据 --------------------------------------------------------
    print(f"\n[2] 造数据（{JOB_COUNT} 个 pending 作业 + 1 个 running 作业 + {TASK_COUNT} 个任务）")
    task_ids = seed(db_path, device_id)
    report.equal("作业已入库（pending）", job_counts(db_path, ("pending",)), JOB_COUNT)
    report.check("running 作业带租约", bool(lease_of_running(db_path)), "lease_until_utc 为空")

    # --- 3. 列表能看到队列 ------------------------------------------------
    print("\n[3] 传输列表的 queuedJobs（界面判「这批结束没有」靠它）")
    status, rows, text = client.json("/app/api/transfers?limit=1000")
    if report.check("GET /transfers 返回 200", status == 200, text[:120]):
        mine = [
            row
            for row in rows or []
            if (row.get("relativePath") or "").startswith(SMOKE_DIR + "/")
        ]
        report.equal("列表里能看到测试任务", len(mine), TASK_COUNT)
        if mine:
            queued = mine[0].get("queuedJobs")
            # queuedJobs 统计 pending + paused + running，所以是 8 个 pending 加那个 running
            report.equal(
                "行上的 queuedJobs 等于目录下未完成作业数（含正在执行的）",
                queued,
                JOB_COUNT + 1,
            )
            report.equal("行上的 pausedJobs 为 0（还没暂停）", mine[0].get("pausedJobs"), 0)

    # --- 4. 暂停：队列必须真的停住 ----------------------------------------
    print("\n[4] 暂停：pending 与 running 都要被接住，租约要清空")
    status, payload, text = client.json(
        "/app/api/transfers/batch",
        "POST",
        {"taskIds": task_ids, "action": "pause", "destinationPath": SMOKE_DIR},
    )
    report.check("暂停返回 200（不再是 409）", status == 200, text[:160])
    report.equal("没有 pending 作业了", job_counts(db_path, ("pending",)), 0)
    report.equal(f"{JOB_COUNT + 1} 个作业全部 paused", job_counts(db_path, ("paused",)), JOB_COUNT + 1)
    # 这条就是「暂停刹不住车」的回归：running 的作业被改成 paused 时必须清租约，
    # 否则手机续租会拿到 paused 才对，但租约留着会让下轮回收逻辑把它当"还在跑"
    report.equal("running 作业的租约已清空", lease_of_running(db_path), None)

    status, snapshot, _ = client.json("/app/api/diagnostics/snapshot")
    report.equal("快照里没有活跃租约", int(snapshot.get("activeLeases", -1)), 0)

    # --- 5. 恢复 ----------------------------------------------------------
    print("\n[5] 继续：paused 回到 pending")
    status, _, text = client.json(
        "/app/api/transfers/batch",
        "POST",
        {"taskIds": task_ids, "action": "resume", "destinationPath": SMOKE_DIR},
    )
    report.check("继续返回 200", status == 200, text[:160])
    report.equal("没有 paused 作业了", job_counts(db_path, ("paused",)), 0)
    report.equal(f"{JOB_COUNT + 1} 个作业回到 pending", job_counts(db_path, ("pending",)), JOB_COUNT + 1)

    # --- 6. 取消 ----------------------------------------------------------
    print("\n[6] 取消：剩下的全作废，不留 pending/paused")
    status, _, text = client.json(
        "/app/api/transfers/batch",
        "POST",
        {"taskIds": task_ids, "action": "cancel", "destinationPath": SMOKE_DIR},
    )
    report.check("取消返回 200", status == 200, text[:160])
    report.equal("没有未结束的作业了", job_counts(db_path, OPEN_STATES), 0)
    report.equal(
        f"{JOB_COUNT + 1} 个作业全部 cancelled",
        job_counts(db_path, ("cancelled",)),
        JOB_COUNT + 1,
    )

    # --- 7. 对已消失的对象再操作，不能再报错 ------------------------------
    #
    # 真机场景：用户先取消、任务记录已被删，再点一次取消。旧版这里 409/400，
    # 前端弹红字"这一批任务都处理不了"，后面的删除步骤也不执行。
    print("\n[7] 对不存在的任务/目录再操作：必须 200，不能报错")
    ghost = {"taskIds": ["smoke-ghost-" + uuid.uuid4().hex[:8]], "destinationPath": SMOKE + "/nope"}
    for action in ("pause", "resume", "cancel"):
        status, payload, text = client.json(
            "/app/api/transfers/batch", "POST", {**ghost, "action": action}
        )
        report.check(
            f"{action} 不存在的对象返回 200",
            status == 200,
            f"HTTP {status} {text[:120]}",
        )

    # --- 8. 清理 ----------------------------------------------------------
    if not keep:
        print("\n[8] 清理测试数据")
        cleanup(db_path)
        report.equal("测试作业已删除", job_counts(db_path, OPEN_STATES + ("cancelled",)), 0)
    else:
        print(f"\n[8] --keep 已保留 {SMOKE} 数据，看完记得清：")
        print(f"    DELETE FROM FileOperationJobs WHERE destination_path LIKE '{SMOKE}%';")
        print(f"    DELETE FROM TransferTasks WHERE relative_path LIKE '{SMOKE}%';")


# ---------------------------------------------------------------- 入口


def main() -> int:
    parser = argparse.ArgumentParser(description="WirelessCable 接口级回归")
    parser.add_argument("--base", default=os.environ.get("WC_BASE", "https://127.0.0.1:47110"))
    parser.add_argument("--db", default="", help="数据库路径；默认取 /diagnostics/snapshot 里的真实路径")
    parser.add_argument("--keep", action="store_true", help="保留测试数据")
    parser.add_argument("--list", action="store_true", help="只列出断言，不执行")
    parser.add_argument("--probe", action="store_true", help="只探测本机实例在不在跑（给 CI 判断要不要跑）")
    args = parser.parse_args()

    if args.list:
        print("断言清单：")
        for line in ASSERTIONS:
            print("  -", line)
        return 0

    if args.probe:
        # 只回答"能不能连上"：能连 0，连不上 1。不打印任何东西，让调用方决定说什么。
        try:
            Client(args.base).sign_in()
        except SystemExit:
            return 1
        return 0

    client = Client(args.base)
    print(f"连接 {args.base}")
    client.sign_in()

    status, snapshot, text = client.json("/app/api/diagnostics/snapshot")
    if status != 200:
        print(f"快照接口不可用（HTTP {status}）：{text[:200]}")
        return 1

    db_path = args.db or snapshot.get("databasePath") or ""
    if not db_path or not os.path.exists(db_path):
        print(f"找不到数据库：{db_path!r}。用 --db 指定。")
        return 1

    status, devices, text = client.json("/app/api/devices")
    paired = [item for item in (devices or {}).get("paired", []) if not item.get("revoked")]
    if not paired:
        print("没有已配对设备，作业造出来也没人执行。先在界面上配对一台手机。")
        return 1

    device_id = paired[0]["deviceId"]
    print(f"版本 {snapshot.get('version')}，库 schema {snapshot.get('schemaVersion')}，")
    print(f"设备 {paired[0].get('name')}（{device_id[:8]}），库 {db_path}")

    report = Report()
    try:
        run(client, report, db_path, device_id, args.keep)
    except SystemExit:
        raise
    except Exception as error:  # noqa: BLE001 - 脚本要把异常变成一条失败，而不是栈
        report.check("脚本执行到底", False, f"{type(error).__name__}: {error}")
    return report.dump()


ASSERTIONS = [
    "GET /status 返回 200 且带版本号",
    "GET /diagnostics/snapshot 返回 200",
    "库 schema >= 17（租约字段已迁移）、quick_check = ok",
    "快照里的库路径与脚本读的是同一个",
    "造 N 个 pending 作业 + 1 个带租约的 running 作业",
    "/transfers 的行上 queuedJobs / pausedJobs 与目录内作业数一致",
    "暂停：pending 与 running 全部变 paused，租约清空，活跃租约归零",
    "继续：paused 回到 pending",
    "取消：pending/paused/running 全部变 cancelled",
    "对不存在的任务/目录执行 pause/resume/cancel：200，不报错",
    "清理：测试数据不残留",
]


if __name__ == "__main__":
    sys.exit(main())
