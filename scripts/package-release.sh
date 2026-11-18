#!/usr/bin/env bash
# 组装交付目录 WirelessCable-Release/。
#
# 前置（缺任何一项直接失败，不生成半成品）：
#   dist/WirelessCable-<version>.exe                      scripts/build-desktop.sh
#   android/app/build/outputs/apk/release/app-release.apk  gradle :app:assembleRelease
#   docs/release/*.md                                      本仓库文档
#
# 用法：
#   scripts/package-release.sh              完整交付包（单文件 + 绿色版 + APK + 文档）
#   scripts/package-release.sh --minimal    精简包：WirelessCable.exe + WirelessCable.apk
#                                           **平铺在交付目录根下**，两个文件直接拿走就能测
#
# 精简包不复制文档与绿色版：文档源在 docs/release/，绿色版就是同一个 exe 配 Data/ 目录，
# 需要时用完整模式重新生成即可。
set -euo pipefail

MINIMAL=0
for arg in "$@"; do
    case "$arg" in
        --minimal) MINIMAL=1 ;;
        *) echo "未知参数：$arg" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
OUT="$ROOT/WirelessCable-Release"

EXE="$ROOT/dist/WirelessCable-$VERSION.exe"
APK="$ROOT/android/app/build/outputs/apk/release/app-release.apk"

if [ ! -f "$EXE" ]; then
    echo "缺少桌面端产物：$EXE（先跑 scripts/build-desktop.sh）" >&2
    exit 1
fi

if [ ! -f "$APK" ]; then
    echo "缺少 Android 正式包：$APK（先跑 :app:assembleRelease）" >&2
    exit 1
fi

echo "==> 组装 WirelessCable-Release（$VERSION$([ "$MINIMAL" = 1 ] && echo '，精简包')）"

# 清空上一版产物。这里不能因为删不掉就整体失败：
# 某些环境把 rm 重定向到回收站，目录被占用时回收站操作会 abort，
# 脚本会误判成"打包失败"（实际产物是好的）。删失败就退化为覆盖同名文件。
rm -rf "$OUT" 2>/dev/null || true
mkdir -p "$OUT"

# 1) 单文件版。精简包平铺在根目录（不套子目录，拷走就能测）
if [ "$MINIMAL" = 1 ]; then
    cp -f "$EXE" "$OUT/WirelessCable.exe"
else
    mkdir -p "$OUT/Windows" "$OUT/Android"
    mkdir -p "$OUT/Windows/SingleFile" "$OUT/Windows/Portable" "$OUT/Documentation"
    cp -f "$EXE" "$OUT/Windows/SingleFile/WirelessCable.exe"

    # 2) 绿色版：程序 + 三个数据目录 + 便携标记
    cp -f "$EXE" "$OUT/Windows/Portable/WirelessCable.exe"
    mkdir -p "$OUT/Windows/Portable/Data" \
             "$OUT/Windows/Portable/Logs" \
             "$OUT/Windows/Portable/TransferTemp"
    cat > "$OUT/Windows/Portable/portable.marker" <<'EOF'
此文件表示当前目录是绿色版（便携版）。
程序会把数据库、证书、接收文件放在 Data\，日志放在 Logs\，传输临时文件放在 TransferTemp\。
不要删除它：只删 Data\ 想重置数据时，程序仍然认自己是便携版。
EOF
fi

# 3) Android 正式包（必须是签名过的 release，不能用 debug）
if [ "$MINIMAL" = 1 ]; then
    cp -f "$APK" "$OUT/WirelessCable.apk"
else
    cp -f "$APK" "$OUT/Android/WirelessCable.apk"
fi

# 4) 文档（精简包不带）
if [ "$MINIMAL" = 0 ]; then
    cp -f "$ROOT/docs/release/README.md"        "$OUT/Documentation/README.md"
    cp -f "$ROOT/docs/release/UserGuide.md"     "$OUT/Documentation/UserGuide.md"
    cp -f "$ROOT/docs/release/Architecture.md"  "$OUT/Documentation/Architecture.md"
    cp -f "$ROOT/docs/release/Protocol.md"      "$OUT/Documentation/Protocol.md"
    cp -f "$ROOT/docs/release/Security.md"      "$OUT/Documentation/Security.md"
    cp -f "$ROOT/docs/release/Compatibility.md" "$OUT/Documentation/Compatibility.md"
    cp -f "$ROOT/docs/release/Changelog.md"     "$OUT/Documentation/Changelog.md"
fi

# 5) 校验清单：把产物的哈希写进交付目录，便于用户核对
(
    cd "$OUT"
    {
        echo "# SHA-256（$VERSION，生成于 $(date '+%Y-%m-%d %H:%M:%S')）"
        if [ "$MINIMAL" = 1 ]; then
            targets=(WirelessCable.exe WirelessCable.apk)
        else
            targets=(Windows/SingleFile/WirelessCable.exe Windows/Portable/WirelessCable.exe Android/WirelessCable.apk)
        fi

        for f in "${targets[@]}"; do
            printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"
        done
    } > SHA256SUMS.txt
)

echo "==> 目录结构"
find "$OUT" -maxdepth 3 -printf '%y %-52p %10s\n' 2>/dev/null | sed "s#$OUT#WirelessCable-Release#" || ls -R "$OUT"
echo
echo "==> 完成：$OUT"
