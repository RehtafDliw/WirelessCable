#!/usr/bin/env bash
#
# 推送 / 拉取，带重试与镜像回退。
#
#   ./scripts/push.sh              推送 main 与所有标签，失败自动重试
#   ./scripts/push.sh --fetch      只拉取；直连不通时改走镜像
#   ./scripts/push.sh --check      只探测各通路，不改动任何东西
#
# 为什么需要它：GitHub 在国内经常间歇性不通，表现是 TLS 握手超时、
# `Empty reply from server` 或 push 卡住不动。绝大多数情况重试一次就好。
# 镜像（gh-proxy 一类）**只能读，而且只对公开仓库有效**——它们不转发认证，
# 私有仓库读不了、push 也一定 403。所以写操作只做重试，读操作才走镜像。
# 仓库当前是私有的（2026-11-18），这段时间镜像那条路等于没有。
#
# 环境变量：
#   WC_REMOTE   远端名，默认 origin
#   WC_RETRIES  推送重试次数，默认 4（间隔 5s / 10s / 20s）
#   WC_SKIP_SMOKE  设为 1 跳过推送前的接口回归

set -uo pipefail

REMOTE="${WC_REMOTE:-origin}"
RETRIES="${WC_RETRIES:-4}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PYTHON_BIN="${PYTHON_BIN:-python}"

# 只读镜像前缀（2026-11-18 实测可用）。
# kkgithub.com / bgithub.xyz 同日测试已失效，别再往回加。
MIRROR_PREFIXES=(
    "https://ghfast.top/"
    "https://ghproxy.net/"
)

say()  { printf '%s\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }

# 直连超时压短一点，别让一次握手卡几分钟
export GIT_HTTP_LOW_SPEED_LIMIT="${GIT_HTTP_LOW_SPEED_LIMIT:-1000}"
export GIT_HTTP_LOW_SPEED_TIME="${GIT_HTTP_LOW_SPEED_TIME:-30}"
export GIT_TERMINAL_PROMPT=0

origin_url() {
    git remote get-url "$REMOTE" 2>/dev/null
}

# 只对 HTTPS 形式拼镜像；SSH 形式（git@...）没法拼
mirror_urls() {
    local url
    url="$(origin_url)" || return 0
    case "$url" in
        https://*) for p in "${MIRROR_PREFIXES[@]}"; do printf '%s%s\n' "$p" "$url"; done ;;
        *) ;;
    esac
}

probe() {
    local url="$1" t0 t1
    t0=$(date +%s%N)
    if git ls-remote --exit-code "$url" HEAD >/dev/null 2>&1; then
        t1=$(date +%s%N)
        ok "$(printf '%5d ms  %s' $(( (t1 - t0) / 1000000 )) "$url")"
        return 0
    fi
    bad "不可用     $url"
    return 1
}

cmd_check() {
    local url reachable=0 private=0
    say "远端：$REMOTE -> $(origin_url)"

    say ""
    say "直连（带本机凭证）"
    probe "$(origin_url)" && reachable=1

    # 匿名读一次，判断仓库是不是私有的：
    # 私有仓库下镜像不转发认证，读了只会弹「要用户名」，不如直接跳过。
    if ! git -c credential.helper= ls-remote --exit-code "$(origin_url)" HEAD >/dev/null 2>&1; then
        private=1
    fi

    say ""
    if [ "$private" -eq 1 ]; then
        say "镜像"
        warn "跳过：匿名访问被拒，仓库是私有的。镜像不转发认证，私有仓库读不了"
    else
        say "镜像（只读：clone / fetch 可用，push 不可用）"
        while IFS= read -r url; do
            [ -n "$url" ] && probe "$url" && reachable=1
        done < <(mirror_urls)
    fi

    say ""
    if [ "$reachable" -eq 1 ]; then
        ok "至少一条通路可用"
        return 0
    fi
    warn "全部不通。先确认网络，再看本脚本末尾的备用方案。"
    return 1
}

# 推代码前先跑接口回归：2 秒，挡住"改 A 坏 B"。
# 实例没起说明这台机器现在不是验证环境 —— 那不是代码问题，跳过而不是阻断。
# 要绕开就用 WC_SKIP_SMOKE=1。
smoke_gate() {
    if [ "${WC_SKIP_SMOKE:-0}" = "1" ]; then
        warn "接口回归已跳过（WC_SKIP_SMOKE=1）"
        return 0
    fi

    if ! "$PYTHON_BIN" "$ROOT/scripts/smoke-test.py" --probe >/dev/null 2>&1; then
        warn "接口回归跳过：本机实例没在跑（启动后可补跑 scripts/smoke-test.py）"
        return 0
    fi

    say "接口回归（scripts/smoke-test.py）"
    if "$PYTHON_BIN" "$ROOT/scripts/smoke-test.py"; then
        ok "接口回归通过"
        return 0
    fi

    bad "接口回归失败，已中止推送（要强行推送用 WC_SKIP_SMOKE=1）"
    return 1
}

retry_push() {
    local attempt=1 delay=5
    # 走 HTTPS 代理时 Windows 的 schannel 后端常报 "server closed abruptly
    # (missing close_notify)" 或代理侧 502；换 OpenSSL 后端即通（实测）。
    local tls=(-c http.sslBackend=openssl)
    while [ "$attempt" -le "$RETRIES" ]; do
        say "推送（第 $attempt/$RETRIES 次）"
        if git "${tls[@]}" push "$REMOTE" main && git "${tls[@]}" push "$REMOTE" --tags; then
            ok "main 与标签已推送"
            return 0
        fi
        if [ "$attempt" -lt "$RETRIES" ]; then
            say "  失败，${delay}s 后重试…"
            sleep "$delay"
            delay=$(( delay * 2 ))
        fi
        attempt=$(( attempt + 1 ))
    done
    return 1
}

fetch_any() {
    local url
    if git fetch "$REMOTE" main 2>/dev/null; then
        ok "已从 $REMOTE 拉取"
        return 0
    fi

    # 私有仓库没有镜像可换，别让用户等两轮注定失败的探测
    if ! git -c credential.helper= ls-remote --exit-code "$(origin_url)" HEAD >/dev/null 2>&1; then
        warn "直连失败；仓库是私有的，镜像不转发认证，没有备用通路"
        return 1
    fi

    say "直连失败，改走镜像…"
    while IFS= read -r url; do
        [ -z "$url" ] && continue
        say "  $url"
        if git fetch "$url" "main:refs/remotes/${REMOTE}/main" 2>/dev/null; then
            ok "已从镜像拉取（refs/remotes/${REMOTE}/main）"
            return 0
        fi
    done < <(mirror_urls)
    return 1
}

usage() {
    sed -n '3,16p' "$0" | sed 's/^# \{0,1\}//'
}

case "${1:---push}" in
    --check) cmd_check ;;
    --fetch) fetch_any || { bad "拉取失败"; exit 1; } ;;
    -h|--help) usage ;;
    --push|"")
        smoke_gate || exit 1
        retry_push && exit 0
        bad "推送失败：重试 $RETRIES 次均未成功"
        cat <<'EOF'

换通路，按顺序试：

1) SSH 走 443（能绕开针对 github.com 的 TLS 干扰，需要一次配置）
   ssh-keygen -t ed25519 -C "你的邮箱"
   把 ~/.ssh/id_ed25519.pub 粘到 GitHub → Settings → SSH and GPG keys
   在 ~/.ssh/config 里写：
     Host github.com
       HostName ssh.github.com
       Port 443
   然后：git remote set-url origin git@github.com:<你>/WirelessCable.git

2) 加一个 Gitee 远端（国内最稳，顺便当备份）
   git remote add gitee git@gitee.com:<你>/WirelessCable.git
   git push gitee main --tags

3) 只读操作（clone / fetch）可以换镜像，先跑 ./scripts/push.sh --check 看哪个通
EOF
        exit 1
        ;;
    *) usage; exit 2 ;;
esac
