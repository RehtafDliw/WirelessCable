# ARCHITECTURE.md — Architecture and key decisions

> Applies to version **2.11.2** (2026-11-24). This document describes the code as it **is**;
> when the two disagree, the code wins. Only implementations you can point at in the repository
> are described here — pure design discussion belongs elsewhere.
>
> 中文版：[ARCHITECTURE.md](ARCHITECTURE.md)

---

## 1. Topology

Windows is the only long-running server; Android is a client only and **listens on no port**.
Two phones have a separate, one-shot direct channel between them.

```
Windows (server)
  WirelessCable.Desktop (WPF shell + React UI inside WebView2)
    └─ in-process Kestrel, HTTPS :47110 (+1 if taken, up to 20 probes)
         /api/v1/*    pairing, devices, files, transfers, jobs
         /app/api/*   loopback only, for the embedded UI (one-shot token + X-WC-Local header)
    ├─ PairingService / QrPairingService   QR codes and pairing
    ├─ FileService + PathGuard             grants, path guard, whole-device access
    ├─ TransferEngine                      chunks, SHA-256, resume
    └─ SQLite (WAL, schema v19)
  mDNS advertises _wirelesscable._tcp (one primary address in the A record; TXT: deviceId,
  protocol version, `addrs` with every candidate address)

Android (client)
  Compose + Room + OkHttp (pinned certificate) + NSD discovery
  A foreground service runs the session engine: heartbeat, index, job polling

Phone ↔ phone (direct transfer)
  The code-showing side starts a one-shot HTTPS server (:47130); the scanning side pins its
  fingerprint. The session is discarded when done — no resume.
```

---

## 2. Modules and dependency direction

| Project | Responsibility |
| --- | --- |
| `WirelessCable.Core` | Domain models and abstractions: `IFileProvider`, `FileOperations`, devices and tasks |
| `WirelessCable.Protocol` | DTOs, error codes, protocol constants (ports / chunks / timeouts / version) |
| `WirelessCable.Storage` | SQLite entities, repositories, hand-written migrations |
| `WirelessCable.Infrastructure` | Certificates, mDNS, file providers, path guard, Serilog, firewall |
| `WirelessCable.Server` | Kestrel host, endpoints, auth and rate-limit middleware, transfer engine, file service, diagnostics snapshot and bundling (`Diagnostics/`) |
| `WirelessCable.Desktop` | WPF host + WebView2 + embedded React; tray, hotspot, native dialogs; native strings via `Services/Loc.cs` |
| `android/app` | Kotlin / Compose single module: `core/{network,pairing,crypto,transfer,sync,files,direct,i18n}` |

Dependencies point downward only: `Desktop → Server → Storage → Protocol → Core`;
`Infrastructure` is referenced by `Server` / `Desktop`. `Core` never references ASP.NET Core,
Windows APIs or SQLite. Desktop-only capabilities (hotspot, file dialogs) are injected into
Server through **bridges**, so the direction never reverses.

---

## 3. Protocol essentials

| Item | Value |
| --- | --- |
| Prefix / version header | `/api/v1/`; `X-WC-Protocol: 1`, mismatch returns 426 |
| Transport | HTTPS (TLS 1.2 / 1.3); **no WebSocket** — progress is polled, jobs use long polling (`pending?wait=3000`) |
| Auth | After pairing, `Authorization: Bearer <deviceId>.<token>`; the server stores only the token's SHA-256 |
| Chunks | 8 MiB, negotiable down to 64 KiB |
| Verification | Whole-file SHA-256 computed while writing; both ends must agree |
| Limits | 100 GiB per file; 3 concurrent non-terminal tasks per device |

---

## 4. Transfers and jobs

- **Temp file → commit**: uploads are written as `*.wcpart` in a dedicated temp directory, then
  `File.Move(overwrite: false)` once the hash matches. Failure paths delete the temp file.
- **Resume**: a `TransferChunks` bitmap records completed chunks; an interrupted transfer resumes
  from the gap. Pause is checked before every chunk.
- **Ordering**: `index` must equal `NextIndex`; skipped or out-of-order chunks return 409, with no
  out-of-order buffering.
- **No overwrite**: an existing target becomes `name (1).ext` and so on.
- **Timeouts**: a `waiting` task is reclaimed after 600 s idle; once transferring, 120 s without
  byte progress marks it broken.
- **Jobs**: the phone listens on no port, so the PC can only read or modify the phone by issuing
  jobs (`list` / `copy` / `delete` / `rename`) that the phone executes and reports back on.
- **Directory copy** happens on the phone — never "download to the PC and upload again".
- **List grouping**: the UI collapses tasks that share the first segment of their destination path into
  one row (a folder download creates tasks one by one, so the server never assigns a batch id).
  A single-file relative path has no `/`, so it is never merged by accident. The query limit is 1000
  across all three layers, and **unfinished tasks sort first** — otherwise, once history grows,
  queued entries fall out of the window and a batch shows only half of its files with a progress
  figure that can never match.

---

## 5. Security

1. **Identity**: both ends generate self-signed certificates and exchange fingerprints during
   pairing; afterwards every connection verifies the pin. A mismatch is a refusal, not a warning.
2. **Pairing**: the QR code carries a one-shot nonce (300 s) plus address, port and fingerprint;
   key agreement is ECDH P-256 + HKDF-SHA256.
3. **Tokens**: stored in Android Keystore (AES-GCM) and Windows DPAPI; the database holds hashes only.
4. **Paths**: `PathGuard` resolves segment by segment and rejects `..`, alternate data streams,
   UNC paths and reserved names. Whole-device access is rooted at drive letters: nothing escapes,
   and drive roots cannot be deleted.
5. **Rate limiting**: 1800 requests/minute per source (`ServerOptions.RateLimitPerMinute`);
   chunk endpoints are exempt.
6. **Logging**: Serilog rolls daily and records file names and hash prefixes only — never tokens
   or full paths.

---

## 6. Storage

`Data/wirelesscable.db` (portable build) or `%LOCALAPPDATA%\WirelessCable\data\` (single-file build),
WAL mode. Tables: `Devices`, `TransferTasks`, `TransferChunks`, `SharedDirectories`,
`SharedDirectoryGrants`, `DeviceFileRoots`, `DeviceFileEntries`, `FileOperationJobs`,
`PairingRecords` — schema **v19**, hand-written migrations (columns only, no table rebuilds;
details in `docs/database.md`). v14–v19 added, in order: job batch count and name
(`batch_total` / `batch_name`), whole-batch total bytes (`batch_total_bytes` — the progress
denominator must stay stable), job leases (`lease_until_utc`: claimed when the phone takes a
job, renewed while it runs, and a rejected renewal means someone else took over), and per-side
hiding (`hidden_for_device` for the phone, `desktop_hidden` for the desktop — each side deletes
only its own view).
Android uses Room; `SessionSyncEngine` is the single executor (heartbeat 10 s + index 12 s +
job long-poll).

Device deletion is **cascading**: `DeviceRepository.DeleteAsync` also removes that device's shared
grants and file-index snapshot (`DeviceFileEntries` by `root_id` prefix, `DeviceFileRoots` by
`device_id`). At startup `DeviceFileRepository.PruneOrphanRootsAsync` sweeps leftovers — before
2.10.0 only grants were deleted, so historical databases can hold tens of thousands of ownerless rows.

---

## 7. Android notes

- `minSdk 30` (Android 11) / `targetSdk 35`. `MANAGE_EXTERNAL_STORAGE` for the inbox;
  `POST_NOTIFICATIONS`, `CAMERA` (scanning), `CHANGE_WIFI_MULTICAST_STATE` (NSD).
- The foreground service declares `foregroundServiceType="specialUse"` (only valid on API 34+) and
  holds a `PARTIAL_WAKE_LOCK` plus a low-latency Wi-Fi lock.
- File access: persisted SAF grants plus the whole-device root; `SafFileProvider` maps capability
  bits to eight actions.
- **The phone's own roots are read locally**: `FileBrowserViewModel.reload()` first looks in
  `localRoots` and, on a hit, calls `FileProviders.forRoot(context, root).list(path)` instead of
  going through the PC's `/files/{root}/entries`. Browsing your own files works with the PC offline;
  entries are converted by `FileEntry.toDto()` into the same DTO the network layer returns, so the
  UI needs no branches.
- UI language: `core/i18n/Str.kt` (Chinese source text as the key + regex rules + fall back to
  Chinese) and `LangState` (`ui.lang` = system/zh/en, a Compose-observable state). Switching
  recomposes immediately without recreating the activity.
- **Network checks skip cellular interfaces**: `LocalSubnet.localAddress()` / `localNetworks()`
  ignore `rmnet* / ccmni* / pdp* / radio* / seth_lte* / clat / v4-rmnet`. Without that, with Wi-Fi
  and cellular both up the diagnostics page shows a carrier address, and `10.0.0.0/8` is taken for
  "our subnet", so "not on the same network" is misreported as "network fine".
- **One source of truth for the home screen's connection state**: `SessionSyncEngine.State`
  (`connected` + `deviceId`). `PairingViewModel.sessionState` only tracks the pairing flow while the
  session is driven by the background engine, so the two drift apart (heartbeat returning 200 while
  the home screen says "disconnected" after a network change).
- Battery whitelisting only changes how long the system waits before freezing the app; the code
  promises nothing beyond that. Whether transfers survive screen-off depends on the ROM.

---

## 8. Architecture decision records (ADR)

| ID | Decision | Rationale | Status |
| --- | --- | --- | --- |
| D-001 | Windows is always the primary server; a reverse Android server requires explicit opt-in | The main scenario is "the PC is the host"; avoids double-listening conflicts and discovery ambiguity | Retired (since 1.3.0 the phone listens on no port) |
| D-002 | Target `net9.0-windows` with a self-contained single file | Self-contained removes runtime dependencies | Settled |
| D-003 | Kestrel hosted in-process, not as a Windows Service | Requirement was explicit; the tray icon is the lifecycle | Settled |
| D-004 | Self-signed certificates with pinned fingerprints, no CA | No third party, no accounts; out-of-band fingerprint confirmation resists MITM | Settled |
| D-005 | Binary framing replaced by HTTPS + JSON | The old prototype's custom binary protocol was hard to evolve and debug | Partially retired (WebSocket dropped in favour of polling + job long-poll) |
| D-006 | 8 MiB chunks with a persisted bitmap | Required; a bitmap is smaller than per-chunk rows and restores faster | Settled |
| D-007 | One `IFileProvider` abstraction | The two ends have different path models (drive letters vs `content://`); business layers must not care | Settled |
| D-008 | SQLite + WAL, single writer connection | Many readers / one writer fits transfer workloads and avoids lock contention | Settled |
| D-009 | Serilog file rolling with a redacting enricher | Required; keeps sensitive data off disk | Settled |
| D-010 | UI tokens taken from the user's mockups | Consistency across ends, and acceptance was against them | Settled |
| D-011 | No version control | Trade-off at the time | Retired (Git since 2026-11-18) |
| D-012 | Port starts at 47110, increments on conflict, and the real port is advertised via mDNS/QR | A fixed port is predictable for firewalls and users; incrementing guarantees availability | Settled |
| D-013 | Pairing uses ECDH + HKDF + HMAC (verification code) + ECDSA (identity) + AES-GCM (token) | All primitives are in the BCL/JCE with public standards to check against; the code feeds into key material instead of being merely compared | Implemented |
| D-014 | mDNS TXT carries the full fingerprint; instance names stay ASCII | Discovery implies trust; ASCII avoids DNS-SD escaping; a spoofed service cannot finish pairing without the code | Implemented |
| D-015 | Store only the token's SHA-256; keep the hash after revocation to distinguish 401 from 403 | A database leak cannot restore tokens; clients can tell "never paired" from "revoked" | Implemented |
| D-016 | The app configures the firewall itself, elevating when needed | Users should not hand-write netsh rules; it only adds/removes its own two rules and never touches global policy | Implemented and verified |
| D-017 | Pairing codes live 300 s | Real-device testing showed 120 s is too short for "read code → allow → type code"; 300 s balances safety and usability | Adjusted |
| D-018 | Hold a `WifiManager.MulticastLock` while discovering | Without it Wi-Fi power saving drops inbound multicast and NSD returns nothing; holding it only during scans saves battery | Implemented and verified on device |
| D-019 | Advertise only the route-selected outbound address | Android NSD takes the first A record, so multiple NICs resolve to unreachable addresses; a UDP connect lets the routing table pick, and virtual NICs are filtered out | Implemented and verified on device (the address-list part is revised by D-048) |
| D-020 | Do not rely on TXT for the address list | Some ROMs truncate TXT in mdnsd | Retired (TXT `addrs` now carries every candidate and the client picks) |
| D-021 | Download tasks can only be created on the PC; clients cannot name PC paths | Otherwise clients could read arbitrary files; the phone only polls, pulls chunks and acknowledges | Implemented |
| D-022 | Chunk paths `/transfer/tasks/*/chunks/*` are exempt from per-minute limits | 5 GiB means 640 requests; at 60 req/min large files would stall. Other transfer endpoints still count | Implemented |
| D-023 | `BoundedReadStream` makes "exactly N bytes" a stream type | Copying a whole `FileStream` to an endpoint writes past Content-Length, Kestrel throws and the client only sees `stream was reset` | Implemented and verified on device |
| D-024 | Speed uses a 3-second sliding window, not exponential smoothing | A smoothed value approaches 0 after a drop but never reaches it, so the UI shows `0 B/s` while the ETA uses a tiny number (1.7e54 hours on a real device); a window naturally returns to zero | Implemented and verified on device |
| D-025 | Broken-link thresholds: 600 s for `waiting`, 120 s for `preparing`/`transferring` | Downloads are client-pulled so the server sees no connection; 120 s tolerates a slow chunk but avoids showing fake progress. `verifying` is local hashing and always uses the long threshold | Implemented, trade-off in K17 |
| D-026 | Retry = server resets the task to zero and the whole file is re-pulled | No bitmap existed at the time | Retired (chunked resume implemented) |
| D-027 | Desktop UI became a **WPF shell + WebView2 (React 18) main UI** | All endpoints are in-process so the web UI can talk to them directly, and the design language is CSS to begin with, so a web UI reproduces it 1:1. Cost: WebView2 runtime required and XAML pages are frozen | Settled (2026-11-16) |
| D-028 | Android does **not** follow the web move; it keeps Compose + generator tokens | `fetch` cannot use `PinnedTrustManager`, and every Android request goes through the pinned OkHttp client; going web would downgrade security or require a custom bridge. Tokens come from the same `design-tokens.json`, so both ends look related | Settled (2026-11-16) |
| D-029 | Design tokens have a single source of truth (`design-tokens.json`, one generator per end) | Two hand-written sets always drift (accent colour, radius scale, type ramp — fix one, miss the other); generators emit CSS and Kotlin constants, and hand-written design values count as defects | Settled |
| D-030 | Desktop ships as a **portable folder** (`exe + wwwroot + portable.marker`, with `Data/Logs/settings.json` beside it) | Copy-and-run: data, logs and settings travel with the program and no reconfiguration is needed on a new machine; the web build lives in `wwwroot` and can be replaced on its own | Settled (2026-11-17) |
| D-031 | Desktop-only capabilities (hotspot, file dialogs) reach Server through **bridges**, not by referencing Desktop | Server needs to generate QR codes and open hotspots, but hotspots are WinRT and only exist on the desktop. `LocalHotspotBridge` registers the implementation at startup and Server only sees `Func<CancellationToken, Task<Offer?>>`; the direction stays one-way and Server can be hosted alone (QR still works without a bridge, just without hotspot fallback) | Settled (2026-11-17) |
| D-032 | The receive folder lives in `AppConfig.received_directory`, switchable at runtime, affecting **new tasks only** | Where files land is server behaviour and should not hide in desktop `settings.json` (the phone or another host must see it too); running tasks keep the path computed at creation so a mid-transfer switch cannot split a file across disks. The engine remembers visited roots so old records stay deletable, but never escapes a folder the user picked | Settled (2026-11-17) |
| D-033 | Browsing the phone from the PC keeps "index snapshot + list job"; no new reverse channel | The phone listens on no inbound port (the reverse service was retired in 1.3.0), so the PC reads the index the phone pushes. Directories deeper than the index depth trigger a one-shot `list` job whose result the phone writes back. The new UI only wraps this as `/app/api/files/*` — no second data path, and the phone never becomes a server | Settled (2026-11-17) |
| D-034 | Copying phone files is executed **on the phone**, not by round-tripping through the PC | A round trip doubles traffic and needs a temp copy on the PC; SAF has no generic `copyDocument`, so existing primitives (recursive mkdir + stream copy) run inside the phone and the PC issues a single `copy` job. Failures delete the half-built output instead of leaving a partial tree | Settled (2026-11-17) |
| D-035 | Phone-to-phone keeps "one side listens + client", preferring a short-lived hotspot with AP fallback; Wi-Fi Direct is a mid-term separate channel, and the 1.3.0 reverse service stays retired | "The phone listens" is not the bottleneck (phone-to-phone already has the code side listening on 47130); the bottleneck is the link between two phones — clients on one AP measured RTT 110–630 ms with 2.38% retransmits, while hotspot and P2P route around it, and PC relay trades one bad link for two good ones. Reusing the existing HTTPS/certificate/token model avoids rewriting security | Settled (2026-11-17) |
| D-036 | Bilingual UI keys on the **Chinese source text** (`Str.t("确定")` / `t('确定')`) with a lookup table plus regex rules, falling back to Chinese | A thousand-odd strings would certainly misalign if given English keys: change the Chinese, forget the key, and the key drifts from the UI. With source text as the key, dictionaries can be compared character by character against the code (scripts do this) and a miss shows Chinese rather than a key. The cost is exact matching including spaces and punctuation — we already hit `'个任务'` vs `t(' 个任务')` with one leading space missing | Settled (2.10.0) |
| D-037 | The advertised device name becomes `machine name · short ID` (first 4 chars of deviceId) | Two PCs with the same name are indistinguishable in the phone's list and share sheet, while deviceId is already unique and simply was not broadcast. Four characters change neither protocol nor pairing flow; old pairing records get the suffix client-side via `deviceLabel()` | Settled (2.10.0) |
| D-038 | The phone's own roots are read **locally**, bypassing the PC's `/files/{root}/entries` | Browsing your own internal storage required the PC to be online: with the PC closed the UI said "Empty folder" and users thought files were gone. `FileProvider.list` already runs in the phone process, so one branch fixes it and saves a round trip. Browsing the phone from the PC still uses "index snapshot + list job" — the two paths do not interfere | Settled (2.10.1) |
| D-039 | Unpairing **cascades** into the file-index snapshot, plus an orphan sweep at startup | An index is a snapshot: once the device is gone it is dead data. The old path deleted only grants, leaving 7400+ rows from two historical phones. Doing it inside `DeviceRepository.DeleteAsync` covers both delete entry points (`/app/api/devices/{id}` and the legacy WPF UI) automatically | Settled (2.10.1) |
| D-040 | Group transfers by the **first segment of the destination path**, with no new batch field | A folder download creates tasks one by one and carries no batch id; adding `batchId` would mean a protocol change, a schema change and both ends upgrading in lockstep. The destination folder *is* the batch the user has in mind, and the first segment of `relativePath` is enough — a single file has no `/`, so it can never be merged by accident | Settled (2.10.2) |
| D-041 | Verifiability rests on a **read-only snapshot endpoint plus a script that writes SQLite directly**, not on a unit-test framework | All five bugs from 2.10.12–2.10.16 lived in "cross-layer + real database" territory: mocking SQLite and HTTP hides queue control, lease cleanup and SAF landing paths. `GET /app/api/diagnostics/snapshot` reads the database and returns state distribution plus active leases; `scripts/smoke-test.py` asserts against it and seeds pending jobs by writing rows directly (jobs have no HTTP creation endpoint). The same snapshot feeds the diagnostics bundle, so the script asserts exactly what the user sees. 27 assertions in ~2 s with an exit code; run it before and after touching the state machine | Settled (2.10.17) |
| D-042 | The diagnostics bundle is **masked by default**: file names become `***.ext`, directory names stay | The typical use of an exported bundle is to hand it to someone else, so the default must already be safe to share; and diagnosing a wrong landing directory only needs the directory hierarchy, not the file names. Seeing full names requires an explicit opt-in (`?includeNames=true`) — that decision does not belong to the program | Settled (2.10.17) |
| D-043 | The PC hotspot at QR time is **opt-in**, off by default | It used to start unconditionally on every QR. But when the PC reaches the internet over Wi-Fi and its adapter does not support STA+AP concurrency, starting the hotspot knocks the PC itself offline — and this fallback is only needed when phone and PC are not on the same network. The choice lives in `settings.json` as `hotspotOnPairing`, read by both UIs; when unchecked the app stops the hotspot it started (recognised by `StartedByUs` or by the SSID being the one the app configured) | Settled (2.10.23) |
| D-044 | Hotspot addresses must exclude **APIPA** (169.254/16); if none is available, say so rather than emit a fake one | A freshly created hotspot adapter first gets a link-local address and only switches to 192.168.137.1 once ICS is done. Picking "whatever address is new since the hotspot started" therefore grabs APIPA on the very first QR, and the phone reaches nothing after joining. Better to ask the user to hit Regenerate than to print a wrong address | Settled (2.10.23) |
| D-045 | `ServerRuntime.Snapshot()` **enumerates the machine's addresses on every call** instead of caching the ones from startup | Addresses are runtime facts: after joining a phone hotspot, switching Wi-Fi or plugging a cable in, the cached copy makes the QR code, `/status` and `/device/info` point at the previous network. The scan then cannot connect while the logs look perfectly clean — the worst kind of failure to diagnose. Enumeration costs one `NetworkInterface` walk, far less than a support round trip | Settled (2.10.26) |
| D-046 | Device credentials belong to a PC by **certificate fingerprint**, not by address; a successful connect migrates the session to the new address | IPs change with the network, but a credential identifies *that PC*, not *that IP*. Matching by address means a user who already paired is told "not paired" and forced to scan again. Migrating the session (`saveSession` + `retryNow()`) fixes the other half: the UI says connected while the heartbeat still hits the old IP | Settled (2.10.26) |
| D-047 | The home screen's "connected" reads **only `SessionSyncEngine.State`**, never the pairing page's state | Two sources for one fact always drift. `PairingViewModel.sessionState` only updates inside the pairing flow while the real session is driven by the background engine; after a network change the heartbeat returns 200 and the home screen still says "disconnected, tap Reconnect" — and tapping it tears down a perfectly good session | Settled (2.10.26) |
| D-048 | mDNS: **one primary address in the A record, every candidate in TXT `addrs`** | Android NSD only reads the first A record, so multiple NICs send the phone to an unreachable address, while `addrs` lets the client try each in turn. With Ethernet and a phone hotspot both up, the primary address is usually Ethernet and only the hotspot address in `addrs` is reachable from the hotspot subnet | Settled (2.10.26, revises D-019/D-020) |
| D-049 | The phone's "my network" check **skips cellular interfaces** | With Wi-Fi and cellular both up, the first `NetworkInterface` entries are carrier interfaces such as `rmnet*`: diagnostics show `10.x` so users think they are on the LAN, and `10.0.0.0/8` is treated as "our subnet", turning "different networks" into "network fine". Trusting only wlan-like interfaces makes the check agree with what the user sees | Settled (2.10.26) |
| D-050 | Deleting a record is **per side**: write only this side's marker (phone `hidden_for_device` / desktop `desktop_hidden`) and never look at what the other side did | Both ends share one table, so deleting rows outright let "the phone tidying its own history" wipe the PC's records too (reported bug: deleting on the phone removed it from the PC while the file was still there). A record is history meant for humans — deleting means "I no longer want to see it", so each side owns its own decision; **one side must never decide for the other just because it already deleted**. When both markers exist the row is merely garbage-collected (`PurgeHiddenAsync`), which changes nothing about either view. Before deleting, the batch's unexecuted jobs are cancelled by destination directory — otherwise the phone keeps creating new tasks and the list refills. Selecting a task that is still transferring **pauses it, cancels it and drops the partial data** before deleting: deleting is one action, and users should not have to press Cancel first just to remove a row | Settled (2.10.30) |
| D-051 | "Running" (`isInFlight`) and "stays in the active tab" (`isActive`) are **two different tests** and must not be mixed | Tab membership must keep paused/failed/interrupted in the active tab, so `isActive` only excludes terminal states; "still running" must only accept waiting/preparing/transferring/verifying. Mixing them means a paused row still reads "transferring" while the toast at the bottom says paused (real screenshot). For the same reason, streaming progress must not broadcast a hard-coded `transferring` — it uses the live status returned by `TouchActiveAsync`, otherwise one in-flight chunk request pushes the UI back to transferring | Settled (2.10.28) |
| D-052 | Cancelling removes partial data immediately; `cancelled` counts as finished and lands in the completed tab | Cancelling means "I don't want it", yet a pre-allocated `*.wcpart` occupies its full size on disk (1.45 GB on a real device). Both ends clean up in order inside the cancel action — the server deletes the temp file, the phone deletes its `.wcpart` and chunk bitmap — and only then finish, so nothing is written back mid-cancel. A cancelled batch makes no further progress, so keeping it in the active tab only makes that tab impossible to clear; treating it as finished on both ends keeps tab membership consistent | Settled (2.10.29) |
| D-053 | Cross-device pause follows the **server state only**; a resume blocked by a cancelled task uses the dedicated `transfer_cancelled` code and the UI must ask about it | Pause is "the other end pressed a button", so the local side has no event of its own to rely on: the phone used to accept a server `paused` only while the local row was still active, and once it was interrupted/failed (a single network hiccup does it) it simply refused to obey — "the PC pressed pause and the phone kept sending". Resume has the mirror problem: after the other end deletes the task the server can only refuse, while the UI **ignored the response** and always reported "resumed", so users found out at the end that nothing moved. Therefore ① merging server state obeys `paused` unconditionally (skipped only when the local side is already paused, to stop the marker set from growing); ② `cancelled` gets its own error code and both ends ask "cancelled by the other device — delete the record?"; ③ the batch endpoint returns `ok / skipped / failed` so the UI can tell "nothing happened" from "done" | Settled (2.10.33) |
| D-054 | Automatic flows **must not change server state**: `allowResume` on `saveDownload` / `resumeUpload` is true only when the user taps Resume | `api.resume` *succeeds* on a paused task — it flips the freshly pressed pause back to active. The batch pump calls `saveDownload` for every file it starts, so "pause → next file resumes → transfer continues" loops forever; the log shows "task paused" followed 0.5 s later by "task resumed". This was never a question of whether the phone obeys `paused` — the phone was undoing the user's pause on their behalf. Likewise, the desktop UI must look up tasks by taskId directly instead of scanning list endpoints: those filter rows the other side deleted (per-side hiding) and cap at 200 entries, both of which turn "cancelled by the peer" into a bogus "task not found" | Settled (2.10.34) |
| D-055 | Deleting a record means **the transfer ends here**: any non-terminal task (including `paused` / `interrupted`) is set to cancelled when the record is deleted | Deleting used to end only "still running" tasks, so a paused task stayed paused after its record was gone — the other side could then really resume it while nobody would receive the file (reported: "resumed", yet the task was already gone). A record is history for humans, but the **transfer state is a fact both sides share**: deleting the record on one end announces that this transfer is over, and the other end must learn about it, otherwise it revives a transfer nobody is waiting for. The price is that Resume now answers "cancelled by the other device" and asks whether to delete the record — far more honest than silently reviving a dead task | Settled (2.10.35) |
| D-056 | "Resume" must be **sent to the server explicitly by the user action**; automatic flows and user actions stay separate | While fixing "pause won't hold", 2.10.34 banned `api.resume` inside the phone's automatic flows (`allowResume`). But the phone's Resume button runs *through the same pump* — so resuming restarted the pump, the pump no longer resumed, the server stayed `paused`, and the first chunk got a 409: "I tapped Resume and nothing happened". The permission was drawn too tight: automatic flows must not undo a pause, but **the user pressing Resume must be able to**. Hence two steps: the user action sends an explicit batch resume, and the pump merely follows the server's bitmap. One more piece: after the PC taps Resume the phone's pump has to wake itself (the poll notices the server is no longer paused, releases the batch and restarts the pump) — once the pump breaks out on a 409 nobody calls it back | Settled (2.10.36) |
| D-057 | When merging the server list, the "looks like it is still transferring locally" exemption **needs a deadline** | The legacy view (which drives the taskbar badge and the home progress hint) permanently skipped removing entries whose local state was active. Once a record was deleted on both sides the server dropped the row while the local state still said transferring — the entry lived on forever, `HasActive` stayed true, and the **taskbar badge never went away**, with ghost rows on the home screen. The exemption exists because local progress is fresher than the snapshot, and a live transfer updates its timestamp on every chunk — so half a minute of silence means it is gone | Settled (2.10.36) |
| D-058 | Cancelling is **bidirectional**: the state becomes `cancelled`, neither side lists it, and the phone deletes its local record | "each side manages its own view" applies to **deleting a record** (requirement 3), not to **cancelling** (requirement 2): a cancelled transfer is over and nobody should keep it around. The key is **not to hide the row early** — once hidden, the phone's list no longer contains it and cannot tell "the peer deleted it" from "it is simply not in the list", so the local row survives forever (reported: after the PC cancels, the phone still shows it). So: the `cancelled` state is the shared fact → both sides filter it out → the phone deletes its local copy | Settled (2.10.38) |
| D-059 | A transferring item offers only **Cancel**, never **Delete record** | A folder in flight can hold hundreds of files, and putting "delete record" next to "cancel transfer" in one menu leaves the user guessing whether they want to tidy history or stop the transfer — with different consequences if they click the wrong one (one only drops history, the other stops data that is moving). Requirement 4 asks that an in-flight batch not show the delete entry at all, and that the server refuse to delete a task that is still running. It also leaves "cancel = terminate both sides + drop the records" unambiguous | Settled (2.10.38) |
| D-060 | "No delete while transferring" must mean the **button is not there at all**, not a complaint after the tap | Requirement 4 was honoured on the server and on the PC (the server refuses, an in-flight batch shows no delete entry), but the phone merely skipped in-flight rows inside `deleteSelected` and then said "please use Cancel" — the button was still there, and the user had to tap it to find out. That is the opposite of what the requirement asks: with two actions on one bar, trial and error is the only way to tell them apart. So the phone now omits **Delete** while every selected row is in flight (a mixed selection still shows it and reports how many were skipped). Principle: **capability boundaries must be visible in the UI, not taught through error messages** | Settled (2.10.39) |
| D-061 | The **two layers** of the same thing must move together: the job (a to-do the PC handed out) and the transfer task (the bytes actually moving) | In the upload direction (PC pulls from the phone) the transfer task is created **by the phone**, while the job lives on the PC — the two are linked by `FileOperationJobs.transfer_task_id`. Pause/Cancel used to act on **one layer only**: the PC pauses the task while the job stays `running`, so the phone kept renewing its lease and kept pushing; cancelling the job left the task alive and visible (reported). Hence bidirectional linkage: the server links the job when the **task** is paused/resumed/cancelled, and the phone links the **task** when the lease reports the job was stopped. Note that **resume must put the job back to `pending`** — the phone only picks up pending jobs, so a job left at paused/running is never touched again (that is why "resume did nothing" on the phone) | Settled (2.10.40) |
| D-062 | The phone needs an **upgrade path that does not depend on adb**: the desktop serves the installer | While debugging, phone updates went through adb — but the moment adb drops (cable unplugged, phone off the network, working remotely) there is no way through, and those are exactly the moments when a new build most needs to reach the phone. So the desktop exposes `/download/android`, mounted at the **root** rather than under `/app/api` (that group requires the one-shot local token a phone browser cannot carry). The APK carries no credentials, so a LAN-reachable download is an acceptable trade-off. The self-signed certificate trips a browser warning; the user clicks through it | Settled (2.10.41) |
| D-063 | Cancelling must act on **every layer**, and both ends must genuinely *disappear* | Four cancellation symptoms turned out to be four separate gaps: ① the PC **transfer page never filtered `cancelled`** (the home page had) → hundreds of red rows poured into "Finished"; ② the server's `ControlJobsAsync` (the phone's job control) **changed the job but never touched the transfer task** → the job was voided while the task the phone was pushing stayed `transferring`, so the other end still showed it; ③ the phone's batch **did not touch jobs at all** → after cancelling, the job could still be picked up and a fresh task rebuilt; ④ cancelled rows were never reclaimed → 341 of them piled up, eating the list endpoint's quota. Hence: pause/cancel/resume always cascade to jobs *and* tasks (looked up precisely by `transfer_task_id`, never by directory — that would hit unrelated batches sharing a folder), both ends filter `cancelled` out, and rows cancelled for more than 30 minutes are reclaimed. **When the state changes but the UI keeps the row**, users see "finished, yet the file is gone" | Settled (2.10.42) |
| D-064 | "Cancel transfer" and "delete record" are **two different semantics** | Requirement ② (cancel → both ends cancel and drop the records) and requirement ③ (delete record → each side manages its own) are opposites. The desktop's Cancel button used to call the **delete-record** endpoint — the per-side one — so the cancelling side cleaned up while the other end kept filing the **already finished** ones under "Finished", even with "also delete the files" ticked (reported: with a folder pulled either way, cancelling on one end still showed Finished on the other). Now there are two paths: `CancelBatchAsync` handles cancelling (**the whole batch becomes cancelled**, including the ones that already finished; files go too when asked; jobs are voided), while deleting a record stays per-side. Cancelling deliberately does **not** set the delete mark: hiding the row means the phone can never fetch it again, so it cannot clear its local record and the row stays stuck in "transferring" forever; "neither end shows it" comes from the cancelled state plus both ends filtering it out. Also, "task already ended" during a cancel counts as **skipped**, not failed — those finished ones are meant to be swept away anyway | Settled (2.11.0) |
| D-065 | "Cancel a batch" must **complete the batch by directory**, not just the selected taskIds | Testing on another machine: cancelling a batch of 6 **missed one** — it kept showing under "Finished" and its file was still on disk (`files=5`, not 6). The taskIds the user selected do not necessarily cover the batch: some files had not been created yet, others were no longer in the local list. "Cancel a batch" means the whole batch goes, so the server now widens the set by the landing directory (the first segment of `relative_path`) and processes every task there. And cancelling must clear the **data** too: when the phone learns a transfer was cancelled, the file it already stored has to go as well (the engine has no SAF rights, so it hands the location to the UI layer to delete) | Settled (2.11.0) |
| D-066 | "Cancel a batch" must complete by **batch**, with the directory only as a fallback | Reported: with the PC pulling a folder from the phone and the phone cancelling, the PC's **"Transferring" badge stayed lit**. The diagnostic bundle and the server log were decisive: the cancel logged `取消整批 records=6 cancelled=6`, yet `upload/completed 1` plus a few transferring rows survived — the ones that slipped through. Root cause: the batch was completed by **landing directory** (the first segment of `relative_path`), but in the upload direction (phone → PC) one batch of files sits in **different subdirectories** (the user picked a whole Music folder full of subfolders), so directory-based completion only works for files sharing a directory — the cross-directory ones slipped through, stayed `transferring`, and the PC's "Transferring" badge never went out. Fix: give upload tasks a **batch name** as well (protocol `TransferStartRequestDto.BatchName` → `TransferTasks.batch_name`) and complete by batch name first, falling back to the directory only for rows without a batch name — using both would **sweep in unrelated batches that share a directory** | Settled (2.11.0) |
| D-067 | The version number has **one source**, and `versionCode` is derived from it | It used to be written in three places: `<Version>` in `Directory.Build.props`, `versionName` in `build.gradle.kts`, and a **hand-incremented** `versionCode`. Miss one and you ship "desktop 2.11.2, phone 2.11.1", or a `versionCode` that did not grow (Android only installs a strictly larger one). Now one line in props is the only edit; gradle reads it and derives `major*10^7 + minor*10^5 + patch*10^3 + 999` (2.11.2 → 21102999), with the trailing 999 leaving room for pre-releases. The scripts read the same place | Settled (2.11.2) |
| D-068 | The transfer state machine is a **shared contract file**, read by both ends' tests | Pause/resume/cancel/delete semantics were reworked six or seven times between 2.10.33 and 2.11.0, always as "changed one end, missed another check", and only a real device could catch it. Now `contracts/transfer-state-machine.json` defines **every cell** of 9 states × 5 actions; the desktop test walks the server implementation cell by cell and the Android test checks its own constants and predicates. Changing semantics starts at the table, and both test suites go red. Note `outcome` (does the server accept it) and `ui` (does the UI offer the button) are **two different things**: the UI may be stricter than the server (D-060), never looser | Settled (2.11.2) |
| D-069 | Three clients read the **same** file: embedded resource / assets / build-time import | A single source fixes "the two ends disagree", but how each client obtains it is an engineering problem: the server embeds the JSON in its assembly (a single-file exe has no repo directory at runtime), Android mounts the contracts directory via `sourceSets.assets.srcDir`, and the frontend `import`s it at build time (vite bundles it). Nothing is "copied into each app's folder" — that would just recreate multiple copies | Settled (2.11.2) |
| D-070 | Diagnostics must be **read-only**; fixing is an explicit user action | The desktop gains a **Connection doctor** page: machine network → service port → LAN discovery → firewall → each paired device, in that order. The checks only read (`FirewallConfigurator.MissingRules` never writes); fixing the firewall goes through `/diagnostics/fix/apply-firewall`, so it happens only when the user presses the button. A doctor that quietly changes system state makes the *next* problem harder to find | Settled (2.11.2) |

---

## 9. Evolution constraints

- New endpoints start with the field contract in `src/WirelessCable.Protocol`; both clients implement it.
- Any cross-layer call (e.g. Desktop touching SQLite directly) counts as an architecture violation.
- Transfer-engine correctness outranks throughput: any speed-up must ship with integrity verification.
- Automatic recovery must be driven by observable evidence (logs, probes). Never restart a serving
  component on a hunch — that was a lesson from the old prototype.
