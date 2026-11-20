[中文](../使用指南.md) | English

# WirelessCable — User Guide

Connect your phone and PC: transfer files, browse the phone from the PC, push photos and videos over.
No cable, no cloud upload.

**You need**: a Windows 10/11 PC, an Android phone, both connected under the **same router**.

How they are connected doesn't matter — PC on Ethernet, phone on Wi-Fi, or one on 2.4 GHz and the other
on 5 GHz all work.

---

## 1. Install

**PC**: put `WirelessCable.exe` in a folder and double-click it.
On first launch it asks to open the local port — click **Continue**, then approve the Windows elevation
prompt. It installs its own firewall rules; you don't have to touch Windows Firewall.

**Phone**: install `WirelessCable.apk` and grant the permissions it asks for:

- **Notifications** — optional. Without it the service still runs in the background; you just don't see
  the status notification.
- **All files access** — required to receive files; without it incoming files cannot be written to
  the Download folder.

One more thing worth doing: in Android settings find this app → Battery → **Unrestricted**.
It works without it, but a long transfer may stall a while after the screen locks.

---

## 2. Pair (once)

1. On the PC, click **Home** in the sidebar, then **Pair code** — a **QR code** appears.
2. On the phone, tap **Scan to pair** and point it at the screen.
3. The PC shows "connected". Done.

Afterwards the two reconnect automatically whenever they are on the same LAN.

> Phone can't find the PC? Tap **Scan for PCs** on the phone's home screen, pick your PC from the list,
> then click **Allow** on the PC.

---

## 3. Transfer files

### Phone → PC

Two equivalent ways:

- **On the phone**: **Files** → select files → send;
- **On the PC**: **Phone files** → find the file → save to this PC.

Files land in the PC's **receive folder** (defaults to the app data folder; change it under
**Settings → Receive folder**). Existing files are never overwritten — you get `photo (1).jpg`.

### PC → Phone

1. On the PC, open **Shared folders** → add a folder (say Downloads or Pictures);
2. It shows up under **Files** on the phone; tap a file to download it.

Downloads land in **Download / WirelessCable** on the phone.

### Share from another app (fastest)

In Gallery, Files or a browser tap **Share** → **WirelessCable**. A picker appears:

- tap your PC → sent directly;
- tap **Direct transfer** → a QR page opens, another phone scans it and receives the files
  (phone-to-phone, no PC involved);
- tap **Cancel** → nothing is sent.
- the desktop sidebar has **Get the phone app**: the phone's browser can download and install the
  APK straight from the PC (it warns about the self-signed certificate — click through it).

Sharing a web link works too: it is saved as a small text file and sent.

---

## 4. Browse the phone on the PC

Open **Phone files** on the PC to browse folders the phone has shared; double-click to save.

First time, grant access on the phone: **Files** → pick the folder to expose.
If that feels tedious, turn on **Whole-device access** instead — the PC can then see almost every file.
The only exceptions are other apps' `Android/data` and `Android/obb`, which Android blocks for every app.

**Permissions can be changed at any time**, per folder:

| Permission | What the PC can do |
| --- | --- |
| None | cannot see it |
| Read-only | browse and download |
| Read-write (no delete) | upload, rename, move — but not delete |
| Read-write + delete | everything |

---

## 5. Transfers page

Both the PC's **Transfers** page and the phone's **Transfers** tab show progress.

- Pause, then resume — it continues where it stopped, it does not restart.
- **Pause works from either end**: both sides act on the same server state, so the other end stops
  after at most the chunk in flight (8 MB).
- If the other device cancelled the task, the row **disappears from your list** (cancelling is
  symmetric) and the local copy is dropped too. Should you happen to hit Resume in the very same
  instant, you get a **"Cancelled by the other device"** prompt asking whether to delete the local
  record — resuming would move no data.
- Cancel discards the task.
- Finished records can be removed (this deletes the record, not the file).
- **Cancelling is symmetric**: whichever side cancels, the other side stops too, and the record
  disappears on both ends. You cancel the **whole batch** — even when its files sit in different
  subfolders, they all go, nothing is left still transferring. The files that already finished are swept away as well — you cancelled
  the batch, so the other end should not keep listing them under "Finished". Tick "also delete the
  files" and the files go too.
- A transfer that is still running offers only **Cancel**, never **Delete record** — tidy up the
  history after the transfer finishes (or after you cancel it). On the phone the bottom action bar
  simply omits **Delete** while every selected row is still in flight.
- **Deleting a record is per-device**: your history is yours; the other end keeps its own copy.

---

## 6. Common settings

| I want to… | Where |
| --- | --- |
| change where received files go | PC **Settings → Receive folder** |
| transfer with no Wi-Fi on the phone | tick **Use a hotspot** when generating the pairing QR; the name and password appear under the QR code (off by default, see below) |
| switch light/dark | PC **Settings → Appearance**; phone **Settings → Appearance** |
| change the UI language | phone **Settings → Appearance → Language** (System / 中文 / English); on the PC use the **English / 中文** button at the bottom of the sidebar |
| keep it running in the tray after closing | tray icon → **Classic UI** → Settings → "Close to tray" |

> The hotspot is **off by default**: when the PC reaches the internet over Wi-Fi and its adapter
> does not support STA+AP concurrency, starting one knocks the PC itself offline. You only need it
> when the phone and the PC are not on the same network — tick the box and regenerate the QR code.
> Unticking it stops the hotspot the app started; a hotspot you enabled yourself in Windows is left
> alone.

---

## 7. FAQ

**The phone says it can't reach the PC**

1. Are both under the same router? **Ethernet + Wi-Fi, or 2.4 GHz + 5 GHz, all count as the same.**
   Only two things really separate devices: the router's **guest network**, and **AP isolation /
   wireless isolation**.
2. Is the PC's firewall blocking it? Approve the elevation prompt on first launch.
3. Did the PC change networks or reboot? Tap **Reconnect** on the phone's home screen — it
   re-discovers the PC over mDNS and follows it to the new address. If that fails, scan the QR code
   again. mDNS may need a few seconds after a network switch, so retry once before rescanning.

**The PC can't see files on the phone**

Usually the folder was never shared. On the phone: **Files** → grant access. If it is already granted,
hit refresh on the PC.

**A transfer stops, then continues after unlocking**

Android froze the app. Set battery to **Unrestricted** as described in section 1.

**Where do files end up?**

- Phone received: `Download / WirelessCable`
- PC received: the receive folder you configured (Settings shows the full path and an "Open" button)

**WirelessCable is missing from the share sheet**

It only appears under **Share**, not under **Open with**. If it is still missing right after installing,
close the share sheet and open it again.

**Do I need to pair again after updating?**

No. As long as you don't delete the data folder next to the executable, the pairing stays valid.
