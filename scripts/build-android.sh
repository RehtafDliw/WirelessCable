#!/usr/bin/env bash
# 构建 Android APK 并收进 dist/。
# 工具链固定在 toolchain/ 内，不依赖 PATH 上的 java / gradle。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export JAVA_HOME="$ROOT/toolchain/jdk17"
GRADLE="$ROOT/toolchain/gradle/bin/gradle.bat"

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

echo "==> 构建 Android $VERSION"
"$GRADLE" -p "$ROOT/android" :app:assembleDebug --no-daemon

mkdir -p "$ROOT/dist"
cp -f "$ROOT/android/app/build/outputs/apk/debug/app-debug.apk" "$ROOT/dist/WirelessCable-$VERSION-debug.apk"

echo "==> 产物：dist/WirelessCable-$VERSION-debug.apk"
ls -lh "$ROOT/dist/WirelessCable-$VERSION-debug.apk"
