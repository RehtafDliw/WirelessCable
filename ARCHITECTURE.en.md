# ARCHITECTURE.md — Architecture and key decisions

> Applies to version **2.10.2** (2026-11-19). This document describes the code as it **is**;
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
    └─ SQLite (WAL, schema v14)
  mDNS advertises _wirelesscable._tcp (TXT: deviceId, protocol version, candidate addresses)

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
| `WirelessCable.Server` | Kestrel host, endpoints, auth and rate-limit middleware, transfer engine, file service |
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
5. **Rate limiting**: 600 requests/minute per source; chunk endpoints are exempt.
6. **Logging**: Serilog rolls daily and records file names and hash prefixes only — never tokens
   or full paths.

---

## 6. Storage

`Data/wirelesscable.db` (portable build) or `%LOCALAPPDATA%\WirelessCable\data\` (single-file build),
WAL mode. Tables: `Devices`, `TransferTasks`, `TransferChunks`, `SharedDirectories`,
`SharedDirectoryGrants`, `DeviceFileRoots`, `DeviceFileEntries`, `FileOperationJobs`,
`PairingRecords` — schema **v14**, hand-written migrations.
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
| D-019 | Advertise only the route-selected outbound address | Android NSD takes the first A record, so multiple NICs resolve to unreachable addresses; a UDP connect lets the routing table pick, and virtual NICs are filtered out | Implemented and verified on device |
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

---

## 9. Evolution constraints

- New endpoints start with the field contract in `src/WirelessCable.Protocol`; both clients implement it.
- Any cross-layer call (e.g. Desktop touching SQLite directly) counts as an architecture violation.
- Transfer-engine correctness outranks throughput: any speed-up must ship with integrity verification.
- Automatic recovery must be driven by observable evidence (logs, probes). Never restart a serving
  component on a hunch — that was a lesson from the old prototype.
