#!/usr/bin/env bash
# 构建 Android APK 并收进 dist/。
# 默认出 debug 包（装机自测）；加 --release 出签名正式包。
# 工具链固定在 toolchain/ 内，不依赖 PATH 上的 java / gradle。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export JAVA_HOME="$ROOT/toolchain/jdk17"
GRADLE="$ROOT/toolchain/gradle/bin/gradle.bat"

MODE="debug"
case "${1:-}" in
    ""|--debug|debug) MODE="debug" ;;
    --release|release) MODE="release" ;;
    *)
        echo "用法：$0 [--debug|--release]" >&2
        exit 2
        ;;
esac

if [ ! -x "$JAVA_HOME/bin/java.exe" ]; then
    echo "缺少 JDK17：$JAVA_HOME" >&2
    exit 1
fi
if [ ! -f "$GRADLE" ]; then
    echo "缺少 Gradle：$GRADLE" >&2
    exit 1
fi

VERSION="$(sed -n 's/.*versionName = "\(.*\)".*/\1/p' "$ROOT/android/app/build.gradle.kts" | head -1)"
VERSION="${VERSION:-0.1.0}"

if [ "$MODE" = "release" ]; then
    echo "==> 构建 Android $VERSION（签名 release）"
    "$GRADLE" -p "$ROOT/android" :app:assembleRelease --no-daemon
    SRC="$ROOT/android/app/build/outputs/apk/release/app-release.apk"
    OUT="$ROOT/dist/WirelessCable-$VERSION.apk"
else
    echo "==> 构建 Android $VERSION（debug）"
    "$GRADLE" -p "$ROOT/android" :app:assembleDebug --no-daemon
    SRC="$ROOT/android/app/build/outputs/apk/debug/app-debug.apk"
    OUT="$ROOT/dist/WirelessCable-$VERSION-debug.apk"
fi

mkdir -p "$ROOT/dist"
cp -f "$SRC" "$OUT"

echo "==> 产物：${OUT#"$ROOT/"}"
ls -lh "$OUT"
