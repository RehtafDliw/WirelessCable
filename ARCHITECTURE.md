# ARCHITECTURE.md — 架构与关键决策

> 对应版本 **2.9.0**（2026-11-18）。本文描述代码**现在**的样子；与代码不一致时以代码为准。
> 只写能在仓库里指到文件的实现，纯设计不进本文。

---

## 1. 拓扑

Windows 是唯一常驻服务端；Android 只作客户端，**不监听任何端口**。两台手机之间另有一条一次性直连通道。

```
Windows（服务端）
  WirelessCable.Desktop（WPF 壳 + WebView2 里的 React 主界面）
    └─ 进程内 Kestrel，HTTPS :47110（被占用则 +1，最多探 20 个）
         /api/v1/*    配对、设备、文件、传输、作业
         /app/api/*   仅 loopback，供内嵌界面使用（一次性令牌 + X-WC-Local 头）
    ├─ PairingService / QrPairingService   二维码与配对
    ├─ FileService + PathGuard             授权、路径守卫、整机访问
    ├─ TransferEngine                      分块、SHA-256、断点续传
    └─ SQLite（WAL，schema v14）
  mDNS 发布 _wirelesscable._tcp（TXT：deviceId、协议版本、候选地址列表）

Android（客户端）
  Compose + Room + OkHttp（证书指纹固定）+ NSD 发现
  前台服务 + 会话引擎维持心跳、索引与作业轮询

手机 ↔ 手机（当面直连）
  出码方起一次性 HTTPS 服务（:47130），扫码方校验证书指纹；会话用完即弃，不续传
```

---

## 2. 模块与依赖方向

| 项目 | 职责 |
| --- | --- |
| `WirelessCable.Core` | 领域模型与抽象：`IFileProvider`、`FileOperations`、设备与任务 |
| `WirelessCable.Protocol` | DTO、错误码、协议常量（端口 / 分块 / 超时 / 版本） |
| `WirelessCable.Storage` | SQLite 实体、仓储、手写迁移 |
| `WirelessCable.Infrastructure` | 证书、mDNS、文件提供器、路径守卫、Serilog、防火墙 |
| `WirelessCable.Server` | Kestrel 宿主、端点、认证与限流中间件、传输引擎、文件服务 |
| `WirelessCable.Desktop` | WPF 宿主 + WebView2 + 内嵌 React；托盘、热点、原生对话框 |
| `android/app` | Kotlin / Compose 单模块：`core/{network,pairing,crypto,transfer,sync,files,direct}` |

依赖只能向下：`Desktop → Server → Storage → Protocol → Core`；`Infrastructure` 由 `Server` / `Desktop` 引用。
`Core` 不引用 ASP.NET Core、Windows API、SQLite。桌面专属能力（热点、文件对话框）通过**桥**注入 Server，方向不反。

---

## 3. 协议要点

| 项 | 取值 |
| --- | --- |
| 前缀 / 版本头 | `/api/v1/`；`X-WC-Protocol: 1`，不匹配返回 426 |
| 传输 | HTTPS（TLS 1.2 / 1.3）；**没有 WebSocket**，进度靠轮询，作业走长轮询（`pending?wait=3000`） |
| 认证 | 配对后 `Authorization: Bearer <deviceId>.<token>`；服务端只存令牌的 SHA-256 |
| 分块 | 8 MiB，可协商下调到 64 KiB |
| 校验 | 整文件 SHA-256，边写边算，两端一致才算完成 |
| 上限 | 单文件 100 GiB；每设备同时 3 个非终态任务 |

---

## 4. 传输与作业

- **临时文件 → 提交**：上传写 `*.wcpart` 到独立 temp 目录，哈希通过后 `File.Move(overwrite: false)`；失败路径删临时文件。
- **续传**：`TransferChunks` 位图记录已完成块，中断后从缺口继续；暂停在每一块之前检查。
- **顺序**：`index` 必须等于 `NextIndex`，跳块/乱序返回 409，不做乱序缓冲。
- **不覆盖**：目标已存在时按 `name (1).ext` 递增占位。
- **超时**：`waiting` 空闲 600 秒回收；开始传输后 120 秒没有字节推进即判断开。
- **作业**：手机不监听端口，电脑要看/改手机只能下发作业（`list` / `copy` / `delete` / `rename`），手机执行后回报。
- **目录复制**在手机上完成，不经过"下载回电脑再传上去"的中转。

---

## 5. 安全

1. **身份**：两端各自生成自签证书，配对时交换指纹并落库；之后每次连接校验，不匹配即拒绝（不是警告）。
2. **配对**：二维码带一次性 nonce（300 秒）+ 地址 + 端口 + 指纹；密钥协商走 ECDH P-256 + HKDF-SHA256。
3. **令牌**：Android 存 Keystore（AES-GCM），桌面端走 DPAPI；数据库里只有哈希。
4. **路径**：`PathGuard` 逐段解析，拒绝 `..`、备用数据流、UNC 与保留名；整机访问以盘符为根，出不去也删不掉盘符根。
5. **限流**：每来源 600 请求/分钟，分块端点不计入。
6. **日志**：Serilog 按天滚动，只写文件名与哈希前缀，不写令牌与完整路径。

---

## 6. 存储

`Data/wirelesscable.db`（便携版）或 `%LOCALAPPDATA%\WirelessCable\data\`（单文件版），WAL 模式。
表：`Devices`、`TransferTasks`、`TransferChunks`、`SharedDirectories`、`FileJobs`、`PairingRequests` 等，schema **v14**，手写迁移。
Android 用 Room；`SessionSyncEngine` 是唯一执行者（心跳 10 秒 + 索引 12 秒 + 作业长轮询）。

---

## 7. Android 要点

- `minSdk 30`（Android 11）/ `targetSdk 35`。`MANAGE_EXTERNAL_STORAGE` 用于收件目录；`POST_NOTIFICATIONS`、`CAMERA`（扫码）、`CHANGE_WIFI_MULTICAST_STATE`（NSD）。
- 前台服务用 `foregroundServiceType="specialUse"`（这个值只在 API 34+ 合法），持有 `PARTIAL_WAKE_LOCK` 与低延迟 Wi-Fi 锁。
- 文件访问：SAF 持久化目录授权 + 整机根；`SafFileProvider` 把能力位翻译成 8 个动作。
- 电池白名单只影响"多久被系统冻结"，代码不作承诺；息屏能否继续取决于 ROM。

---

## 8. 技术决策记录（ADR）

| 编号 | 决策 | 理由 | 状态 |
| --- | --- | --- | --- |
| D-001 | Windows 恒为主服务端，Android 反向服务需显式开启 | 用户主场景是"电脑当主机"；避免双监听端口冲突与发现歧义 | 已废止（1.3.0 起手机不监听端口） |
| D-002 | 目标 `net9.0-windows` + 自包含单文件 | 自包含消除运行时依赖 | 已定 |
| D-003 | Kestrel 进程内托管，不做 Windows Service | 需求明确；托盘即生命周期 | 已定 |
| D-004 | 自签证书 + 指纹固定，不做 CA | 无第三方、无账号；指纹在配对时带外确认即可抗中间人 | 已定 |
| D-005 | 二进制帧改为 HTTPS + JSON | 旧原型自研二进制协议难演进、难调试 | 部分废止（WebSocket 未采用，改用轮询 + 作业长轮询） |
| D-006 | 分块 8 MiB，位图持久化 | 需求指定；位图比逐块记录更省空间且恢复快 | 已定 |
| D-007 | `IFileProvider` 统一抽象 | 双端路径模型不同（盘符 vs content://），业务层不得感知 | 已定 |
| D-008 | SQLite + WAL，单写入连接 | 多读单写符合传输场景；避免锁争用 | 已定 |
| D-009 | 日志 Serilog 文件滚动 + 脱敏 enricher | 需求指定；避免敏感信息落盘 | 已定 |
| D-010 | UI 令牌取自用户样张 | 双端一致、可验收 | 已定 |
| D-011 | 不引入 Git | 当时的取舍 | 已废止（2026-11-18 起使用 Git） |
| D-012 | 端口固定 47110 起，冲突则 +1 递增并在 mDNS/二维码中通告实际端口 | 固定端口便于防火墙与用户预期，递增保证可用性 | 已定 |
| D-013 | 配对用 ECDH+HKDF+HMAC(验证码)+ECDSA(身份)+AES-GCM(令牌) | 全部 BCL/JCE 原语，有公开标准可对照；验证码进入密钥材料而非仅比对 | 已实现 |
| D-014 | mDNS TXT 放完整指纹、实例名用 ASCII | 发现即可固定信任；ASCII 避免 DNS-SD 转义；伪造服务因拿不到配对码而无法完成配对 | 已实现 |
| D-015 | 令牌只存 SHA-256，撤销保留哈希以区分 401/403 | 数据库泄漏不可还原令牌；客户端能区分"未配对"与"被撤销" | 已实现 |
| D-016 | 防火墙由程序自动配置，权限不足时自提权 | 用户不该被要求手写 netsh 规则；只增删自己的两条规则，不改全局策略 | 已实现并实测 |
| D-017 | 配对有效期 300 秒 | 真机实测 120 秒不足以完成"看码→允许→输码"，300 秒兼顾安全与可用 | 已调整 |
| D-018 | Android 发现期间必须持有 `WifiManager.MulticastLock` | 无锁时 Wi-Fi 省电丢弃入站组播，NSD 永远 0 结果；只在扫描期持有以省电 | 已实现并真机验证 |
| D-019 | mDNS 只广播一条「系统出站源地址」 | Android NSD 只取第一条 A 记录，多网卡会解析到不可达地址；由 UDP connect 让路由表选网卡，虚拟网卡一律过滤 | 已实现并真机验证 |
| D-020 | 不依赖 TXT 传递地址列表 | 部分 ROM 的 mdnsd 截断 TXT | 已废止（TXT `addrs` 现在给出全部候选地址，客户端逐个择优） |
| D-021 | 下载任务只能由电脑端在本机创建，客户端不能指定电脑路径 | 否则等于给客户端开放任意文件读取；手机只轮询 + 拉块 + 回执 | 已实现 |
| D-022 | 分块路径 `/transfer/tasks/*/chunks/*` 不计入每分钟限流 | 5 GiB = 640 次请求，按 60 req/min 会把大文件锁死；其余传输端点照常计数 | 已实现 |
| D-023 | 用 `BoundedReadStream` 把"恰好 N 字节"做成流类型 | 端点直接 `CopyToAsync` 整份 `FileStream` 会写超 Content-Length，Kestrel 直接抛异常，客户端只看到 `stream was reset` | 已实现并真机验证 |
| D-024 | 速度用 3 秒滑动窗口，不用指数平滑 | 平滑值断链后无限逼近 0 但永不等于 0，界面会显示 `0 B/s` 却用极小值算剩余时间（真机出现 1.7e54 小时）；窗口法自然归零 | 已实现并真机验证 |
| D-025 | 断链判定阈值：`waiting` 600 秒，`preparing`/`transferring` 120 秒 | 下载由客户端拉块，服务端看不到连接；120 秒能容忍慢速链路的单块耗时，又不至于让界面长时间显示假进度。`verifying` 是本地哈希，一律走长阈值 | 已实现，取舍见 K17 |
| D-026 | 下载重试 = 服务端把任务重置为 0 后整份重拉 | 当时没有位图 | 已废止（已实现分块续传） |
| D-027 | 桌面端 UI 改为 **WPF 壳 + WebView2（React 18）主界面** | 端点都在进程内，Web 直连即可；设计语言本身是 CSS 实现的，用 Web 能 1:1 还原。代价：宿主需加载 WebView2 运行时，XAML 页面停止新增 | 已定（2026-11-16） |
| D-028 | Android 端**不**跟随改 Web，保留 Compose + 生成器令牌 | `fetch` 无法使用 `PinnedTrustManager`，而 Android 全部请求都走指纹固定的 OkHttp；改 Web 等于把安全模型降级或自建原生网桥。令牌由同一份 `design-tokens.json` 生成，两端观感同源 | 已定（2026-11-16） |
| D-029 | 设计令牌单一事实源（`design-tokens.json`，两端生成器同源） | 两端各写一份必然漂移（强调色、圆角密度、字号阶梯改一处漏一处）；生成器产出 CSS 与 Kotlin 两侧常量，手写设计数值视为缺陷 | 已定 |
| D-030 | 桌面端交付形态改为**便携文件夹**（`exe + wwwroot + portable.marker`，`Data/Logs/settings.json` 同目录） | 「即拷即用」：数据、日志、设置跟着程序走，换机器不用重配；前端产物放在 `wwwroot` 可单独替换 | 已定（2026-11-17） |
| D-031 | 桌面专属能力（热点、文件对话框）经**桥**注入 Server，而不是让 Server 引用 Desktop | `Server` 要生成二维码、要开热点，但热点是 WinRT、只有桌面端有。桥（`LocalHotspotBridge`）把实现挂在启动时，Server 只认 `Func<CancellationToken, Task<Offer?>>`；方向不反，Server 可单独托管，桥没挂时出码仍可用（只是没有热点兜底） | 已定（2026-11-17） |
| D-032 | 接收目录存 `AppConfig.received_directory`，运行时切换且**只影响新任务** | 落点是服务端行为，不该藏在桌面 `settings.json`（手机端/其它宿主同样要看到）；已有任务继续写创建时算好的路径，避免传一半换盘。引擎记住用过的根，换目录后旧记录里的文件仍能删，但绝不越出用户选过的目录 | 已定（2026-11-17） |
| D-033 | 电脑浏览手机沿用「索引快照 + list 作业」，不新增反向拉取通道 | 手机不监听入站端口（1.3.0 退役反向服务），电脑要看手机只能读手机推上来的索引；深目录超出索引深度时下发一次性 `list` 作业，手机执行完把结果写回作业。新界面只是把这套能力包成 `/app/api/files/*`，没有第二条数据通路，也没有把手机变成服务端 | 已定（2026-11-17） |
| D-034 | 手机文件的复制由**手机执行**，不走「下载回电脑再传上去」的中转 | 中转会把流量翻倍、还要在电脑临时落盘；SAF 没有通用 `copyDocument`，用既有原语（递归建目录 + 流拷贝）在手机内完成，电脑只下发一个 `copy` 作业。失败时删掉半成品，不留半个目录 | 已定（2026-11-17） |
| D-035 | 手机间传输保留「一端监听 + 客户端」模型，短期热点优先、AP 兜底；Wi-Fi Direct 列为中期独立通道，不复活 1.3.0 的反向服务 | 「手机做监听」不是瓶颈（手机-手机直连本来就是出码方监听 47130）；瓶颈是两台手机之间的链路——同一个 AP 的客户端互访实测 RTT 110–630 ms、重传 2.38%，热点与 P2P 才是绕开它的办法，电脑中转则把一条烂链路换成两条好链路。沿用现有 HTTPS/证书/令牌模型，避免重写安全层 | 已定（2026-11-17） |

---

## 9. 演进约束

- 新增端点先定字段契约（`src/WirelessCable.Protocol`），两个客户端都照此实现。
- 任何跨层调用（如 Desktop 直连 SQLite）视为架构违规。
- 传输引擎的正确性优先于吞吐：任何提速改动必须附带完整性验证。
- 自动恢复类机制必须有可观测证据（日志/探测）驱动，禁止凭假设重启正在服务的组件（旧原型教训）。
