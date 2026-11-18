#!/usr/bin/env bash
# 全量测试：三段 xunit + Android JVM 单测。
# 若同时提供 WC_LIVE_* 变量，Android 实连测试会打真实服务端。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_BIN="${DOTNET_BIN:-$HOME/.dotnet/dotnet.exe}"

echo "==> .NET 测试"
"$DOTNET_BIN" test "$ROOT/WirelessCable.sln" -c Debug --nologo

echo "==> Android 单测"
export JAVA_HOME="$ROOT/toolchain/jdk17"
GRADLE="$ROOT/toolchain/gradle/bin/gradle.bat"

LIVE_ARGS=()
if [ -n "${WC_LIVE_HOST:-}" ] && [ -n "${WC_LIVE_FINGERPRINT:-}" ]; then
    LIVE_ARGS+=("-Dwc.host=$WC_LIVE_HOST" "-Dwc.port=${WC_LIVE_PORT:-47110}" "-Dwc.fingerprint=$WC_LIVE_FINGERPRINT")
    echo "    （含真实 HTTPS 联调：$WC_LIVE_HOST:${WC_LIVE_PORT:-47110}）"
else
    echo "    （未提供 WC_LIVE_HOST/WC_LIVE_FINGERPRINT，实连测试将跳过）"
fi

"$GRADLE" -p "$ROOT/android" :app:testDebugUnitTest --no-daemon "${LIVE_ARGS[@]}"

echo "==> 完成"
