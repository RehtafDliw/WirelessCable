#!/usr/bin/env bash
# 全量测试：三段 xunit + Android JVM 单测 + 接口回归。
# 若同时提供 WC_LIVE_* 变量，Android 实连测试会打真实服务端。
# 接口回归需要一个**正在运行**的本机实例；没在跑就跳过（并说明怎么补跑）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_BIN="${DOTNET_BIN:-$HOME/.dotnet/dotnet.exe}"
PYTHON_BIN="${PYTHON_BIN:-python}"

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

# 接口回归：xunit 测的是纯逻辑与协议形状，测不到"作业队列 + 真实库"这条链。
# 2.10.12–2.10.16 五个真机 bug 全部落在这里（暂停刹不住、跳已完成、不能再次取消），
# 所以这一段是必须的，不是补充。
echo "==> 接口回归"
if "$PYTHON_BIN" "$ROOT/scripts/smoke-test.py" --probe >/dev/null 2>&1; then
    "$PYTHON_BIN" "$ROOT/scripts/smoke-test.py"
else
    echo "    （连不上 127.0.0.1:47110，跳过）"
    echo "    启动 WirelessCable 后再跑：$PYTHON_BIN scripts/smoke-test.py"
fi

echo "==> 完成"
