#!/usr/bin/env bash
# 组装交付目录 WirelessCable-Release/。
#
# **交付目录里永远只有三个文件，且都带版本号**：
#
#   WirelessCable-<version>.exe            电脑端（单文件，可直接运行）
#   WirelessCable-<version>.apk            手机端安装包
#   WirelessCable-<version>-portable.zip   电脑端便携版（解压即用）
#
# 规则（别改）：
#   - 文件名**必须带版本号**：交付目录会同时留着好几版，带版本号才不会拿错；
#     不带版本号的同名文件（比如 WirelessCable.exe / WirelessCable.apk）
#     永远不进这个目录——它们是运行时资源，住在各自的程序目录里。
#   - 每次运行**先清空**交付目录：保证"只有这三个"不会因为历史残留而破功。
#   - 校验和打印到终端，不落盘：目录里多一个文件就破规矩了。
#
# 前置（缺任何一项直接失败，不生成半成品）：
#   dist/WirelessCable-<version>.exe              scripts/build-desktop.sh
#   dist/WirelessCable-<version>-portable/        scripts/build-desktop.sh
#   android/app/build/outputs/apk/release/app-release.apk
#                                               scripts/build-android.sh --release
#
# 用法：
#   scripts/package-release.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
if [ -z "$VERSION" ]; then
    echo "在 Directory.Build.props 里找不到 <Version>，无法确定产物名。" >&2
    exit 1
fi

OUT="$ROOT/WirelessCable-Release"
EXE="$ROOT/dist/WirelessCable-$VERSION.exe"
APK="$ROOT/android/app/build/outputs/apk/release/app-release.apk"
PORTABLE_DIR="$ROOT/dist/WirelessCable-$VERSION-portable"
PORTABLE_ZIP="$OUT/WirelessCable-$VERSION-portable.zip"

for f in "$EXE" "$APK"; do
    if [ ! -f "$f" ]; then
        echo "缺少产物：$f" >&2
        case "$f" in
            *.exe) echo "  先跑 scripts/build-desktop.sh" >&2 ;;
            *.apk) echo "  先跑 scripts/build-android.sh --release" >&2 ;;
        esac
        exit 1
    fi
done

if [ ! -d "$PORTABLE_DIR" ]; then
    echo "缺少便携版目录：$PORTABLE_DIR（先跑 scripts/build-desktop.sh）" >&2
    exit 1
fi

echo "==> 组装交付目录（$VERSION）"

# 清空上一版产物。这里不能因为删不掉就整体失败：
# 某些环境把 rm 重定向到回收站，目录被占用时回收站操作会 abort，
# 脚本会误判成"打包失败"（实际产物是好的）。删失败就退化为覆盖同名文件。
rm -rf "$OUT" 2>/dev/null || true
mkdir -p "$OUT"

# 1) 电脑端单文件
cp -f "$EXE" "$OUT/WirelessCable-$VERSION.exe"

# 2) 手机端安装包
cp -f "$APK" "$OUT/WirelessCable-$VERSION.apk"

# 3) 便携版 zip
#
# 便携版里**要带一份 WirelessCable.apk**（不带版本号）：它是运行时资源——
# 桌面端侧栏的「下载手机版」就是从程序目录旁边取它，手机浏览器才能装新版。
# 用户拿到便携版解压到任何地方，这个下载入口都得能用。
#
# 便携版目录里的 Data / Logs / settings.json / TransferTemp 是用户自己的数据，
# 不能进 zip（会把别人的传输记录和证书一起发出去）。
python - "$PORTABLE_DIR" "$PORTABLE_ZIP" "$APK" <<'PYEOF'
import os, sys, zipfile

src_dir, out_zip, apk = sys.argv[1], sys.argv[2], sys.argv[3]
skip = {'Data', 'Logs', 'TransferTemp', 'settings.json'}

with zipfile.ZipFile(out_zip, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for base, dirs, files in os.walk(src_dir):
        rel = os.path.relpath(base, src_dir).replace('\\', '/')
        if rel == '.':
            rel = ''
        dirs[:] = [d for d in dirs if d not in skip]
        for f in files:
            z.write(os.path.join(base, f), (rel + '/' + f) if rel else f)
    # 运行时资源：手机版安装包
    z.write(apk, 'WirelessCable.apk')
PYEOF

# 4) 校验和：**只打印到终端**，不落盘——
#    交付目录里多一个文件就破"只有三个"的规矩。
echo
echo "==> SHA-256"
for f in "$OUT/WirelessCable-$VERSION.exe" "$OUT/WirelessCable-$VERSION.apk" "$PORTABLE_ZIP"; do
    printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$(basename "$f")"
done

echo
echo "==> 交付目录内容（应当只有三个带版本号的文件）"
ls -1 "$OUT"
COUNT="$(find "$OUT" -maxdepth 1 -type f | wc -l | tr -d ' ')"
if [ "$COUNT" != "3" ]; then
    echo "错误：交付目录里有 $COUNT 个文件，应为 3 个" >&2
    exit 1
fi

echo
echo "==> 完成：$OUT"
