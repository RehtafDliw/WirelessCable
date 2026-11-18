#!/usr/bin/env bash
# 构建 Windows 桌面端并产出单文件 exe 到 dist/。
# 工具链路径可覆盖：DOTNET_BIN=/path/to/dotnet
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_BIN="${DOTNET_BIN:-$HOME/.dotnet/dotnet.exe}"

if [ ! -x "$DOTNET_BIN" ]; then
    echo "缺少 .NET SDK：$DOTNET_BIN" >&2
    echo "可用 DOTNET_BIN 指定 dotnet 路径。" >&2
    exit 1
fi

# 版本号唯一来源：根目录 Directory.Build.props
VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
if [ -z "$VERSION" ]; then
    echo "在 Directory.Build.props 里找不到 <Version>，无法确定产物名。" >&2
    exit 1
fi

# 前端产物（阶段 14.6）：webui 的 vite build 结果会嵌进 exe。
# 没有 node_modules 或显式 SKIP_WEBUI=1 时跳过 —— 那时 exe 里没有 /app，旧界面照常。
WEBUI="$ROOT/src/WirelessCable.Desktop/webui"
if [ "${SKIP_WEBUI:-0}" != "1" ] && [ -d "$WEBUI/node_modules" ]; then
    echo "==> 构建前端 webui"
    (cd "$WEBUI" && npx vite build >/dev/null)
else
    echo "==> 跳过前端构建（SKIP_WEBUI=1 或缺少 node_modules）"
fi

echo "==> 发布 WirelessCable $VERSION (win-x64, self-contained, single-file)"
"$DOTNET_BIN" publish "$ROOT/src/WirelessCable.Desktop/WirelessCable.Desktop.csproj" \
    -c Release \
    -o "$ROOT/dist/publish" \
    --nologo

mkdir -p "$ROOT/dist"
cp -f "$ROOT/dist/publish/WirelessCable.exe" "$ROOT/dist/WirelessCable-$VERSION.exe"

# 便携版（交付形态，见 MASTER §3）：整目录拷走即用，
# Data/（数据库+证书）、logs/、settings.json 都落在 exe 同目录。
PORTABLE="$ROOT/dist/WirelessCable-$VERSION-portable"
# 重建时保留用户的 Data / Logs / settings.json：升级不该顺手清掉数据库与证书
KEEP="$ROOT/dist/.portable-keep"
rm -rf "$KEEP"
mkdir -p "$KEEP"
if [ -d "$PORTABLE" ]; then
    for item in Data Logs settings.json TransferTemp; do
        if [ -e "$PORTABLE/$item" ]; then cp -r "$PORTABLE/$item" "$KEEP/" 2>/dev/null || true; fi
    done
fi
rm -rf "$PORTABLE"
mkdir -p "$PORTABLE"
if [ -n "$(ls -A "$KEEP" 2>/dev/null)" ]; then
    cp -r "$KEEP/." "$PORTABLE/"
    echo "==> 已保留原有 Data / Logs / settings.json"
else
    # 版本号变了就没有同名目录可继承，Data 会凭空消失（用户的传输记录跟着没）。
    # 退一步：拿 dist 里最新一个带 Data 的便携目录接手。
    # 注意排除"刚建出来的这个空目录"——它一定是最新的，不排掉就永远选中它自己
    PREV="$(ls -1dt "$ROOT"/dist/WirelessCable-*-portable 2>/dev/null | grep -v "/$(basename "$PORTABLE")$" | head -n 1)"
    if [ -n "$PREV" ] && [ -d "$PREV/Data" ]; then
        for item in Data Logs settings.json TransferTemp; do
            if [ -e "$PREV/$item" ]; then cp -r "$PREV/$item" "$PORTABLE/" 2>/dev/null || true; fi
        done
        echo "==> 未找到同名目录，已接管 $(basename "$PREV") 的 Data"
    fi
fi
rm -rf "$KEEP"
cp -f "$ROOT/dist/publish/WirelessCable.exe" "$PORTABLE/WirelessCable.exe"
if [ -d "$ROOT/dist/publish/wwwroot" ]; then
    cp -r "$ROOT/dist/publish/wwwroot" "$PORTABLE/wwwroot"
else
    echo "警告：发布目录没有 wwwroot，/app 只能靠 exe 内嵌的那份" >&2
fi
# 标记文件让程序进入便携模式（判定见 PortableLayout）
: > "$PORTABLE/portable.marker"

echo "==> 产物：dist/WirelessCable-$VERSION.exe（单文件，可直接运行）"
ls -lh "$ROOT/dist/WirelessCable-$VERSION.exe"
echo "==> 产物：$PORTABLE（便携版）"
du -sh "$PORTABLE" | cut -f1
