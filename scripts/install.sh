#!/bin/bash
# macOS system Bash 3.2; no Python, Homebrew or developer tools required.
# Fixed release bytes are verified before extraction or quarantine changes.

usage() {
    cat <<'EOF'
Codex 用量快捷安装（固定 v1.0.1）
用法：/bin/bash scripts/install.sh [选项]
  --archive ZIP       使用已经下载的官方 v1.0.1 ZIP，仍校验固定 SHA-256
  --destination DIR   安装目录，须为绝对路径；默认 ~/Applications
  --no-open           安装后不启动 App
  --help              显示帮助

自动选择 Intel / Apple Silicon（含 Rosetta 终端）。需要 macOS 11+。
必须在交互终端输入 install，明确同意信任该发布及处理本应用的隔离属性。
不使用 sudo，不覆盖已有 App，不修改 Codex 数据或系统安全设置。
EOF
}

fail() { printf '错误：%s\n' "$*" >&2; exit 1; }

select_release() {
    # uname reports x86_64 inside Rosetta; query the hardware first.
    if [[ "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" == 1 ]]; then
        architecture=arm64
    else
        architecture=$(uname -m)
    fi
    case "$architecture" in
        arm64)
            label=AppleSilicon
            expected_sha=f2dbe256370a57bebea4c9573192305b7fbe84aaa629cd79d9df86c3c2c9e2e3
            ;;
        x86_64)
            label=Intel
            expected_sha=72dba2fb47328e601928d7a9f53a89ea91ddce60ca2dce686eca7bd2bb14e1e8
            ;;
        *) fail "不支持的芯片架构：$architecture" ;;
    esac
    version=1.0.1
    filename="codex-usage-desktop-v${version}-${label}.zip"
    release_url="https://github.com/Asher-XunZhang/codex-usage/releases/download/v${version}/${filename}"
}

confirm_install() {
    local answer
    [[ -t 0 ]] || fail '请在终端交互运行本脚本；不支持管道执行或无人值守确认。'
    printf '\n此发布使用 ad hoc 签名，未经 Developer ID 签名或 Apple 公证。\n'
    printf 'SHA-256 校验用于确认下载内容，不等于 Apple 认证发布者身份。\n'
    printf '继续将信任上述发布，并仅移除新安装这份 App 的下载隔离属性。\n'
    printf '不会关闭 Gatekeeper，不修改其他 App 或 Codex 数据。\n'
    printf '确认来源可信并同意安装，请输入 install（其他输入取消）：'
    IFS= read -r answer || fail '已取消安装。'
    [[ "$answer" == install ]] || fail '已取消安装。'
}

verify_archive() {
    local archive="$1" actual
    actual=$(shasum -a 256 "$archive") || fail '无法计算安装包 SHA-256。'
    actual=${actual%% *}
    [[ "$actual" == "$expected_sha" ]] || fail "安装包校验失败，未解压或安装。请下载 ${filename}；不接受其他架构、版本或重新压缩的文件。"
}

verify_bundle() {
    local app="$1" info binary description value
    [[ -d "$app" && ! -L "$app" ]] || fail '安装包缺少预期的 App。'
    info="$app/Contents/Info.plist"
    value=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$info") || fail '无法读取 App 信息。'
    [[ "$value" == local.codex-usage.desktop ]] || fail 'App 标识不符。'
    value=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$info") || fail '无法读取 App 版本。'
    [[ "$value" == "$version" ]] || fail 'App 版本不符。'
    for binary in \
        "$app/Contents/MacOS/CodexUsage" \
        "$app/Contents/MacOS/CodexSummary" \
        "$app/Contents/MacOS/CodexQuota" \
        "$app/Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage"; do
        [[ -f "$binary" && -x "$binary" && ! -L "$binary" ]] || fail "可执行文件缺失或权限不符：$binary"
        description=$(file -b "$binary") || fail '无法读取可执行文件架构。'
        [[ "$description" == "Mach-O 64-bit executable $architecture"* ]] || fail "可执行文件架构不符：$description"
    done
    codesign --verify --deep --strict "$app" || fail 'App 完整性签名校验失败。'
}

cleanup() {
    # Both paths are created by this invocation; never remove a user's App.
    if [[ -n "${staging_dir:-}" ]]; then rm -rf -- "$staging_dir"; fi
    if [[ "${owns_lock:-0}" == 1 ]]; then rmdir -- "$lock_dir"; fi
}

install_main() {
    set -euo pipefail
    PATH=/usr/bin:/bin:/usr/sbin:/sbin
    export PATH
    umask 077
    local archive='' destination='' launch=1 default_destination=1 os_version major
    local target app
    staging_dir=''
    owns_lock=0
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --archive|--destination)
                [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || fail "$1 缺少参数。"
                if [[ "$1" == --archive ]]; then archive="$2";
                else destination="$2"; default_destination=0; fi
                shift 2 ;;
            --no-open) launch=0; shift ;;
            --help|-h) usage; return 0 ;;
            *) fail "未知参数：$1（使用 --help 查看帮助）" ;;
        esac
    done
    [[ "$(uname -s)" == Darwin ]] || fail '本安装器仅支持 macOS。'
    [[ "$EUID" -ne 0 ]] || fail '请以普通用户运行，不要使用 sudo。'
    os_version=$(sw_vers -productVersion) || fail '无法识别 macOS 版本。'
    major=${os_version%%.*}
    [[ "$major" =~ ^[0-9]+$ ]] && [[ "$major" -ge 11 ]] || fail '需要 macOS 11 或更新版本。'
    select_release
    if [[ "$default_destination" == 1 ]]; then
        [[ "${HOME:-}" == /* && -d "$HOME" ]] || fail '无法确定用户目录，请指定 --destination。'
        destination="$HOME/Applications"
        if [[ -e /Applications/Codex用量.app || -L /Applications/Codex用量.app ]]; then
            fail '在 /Applications 已有 Codex用量.app，请先退出并移走旧 App，避免安装两份。用户数据无需删除。'
        fi
    fi
    [[ "$destination" == /* ]] || fail '--destination 必须是绝对路径。'
    [[ ! -L "$destination" ]] || fail '安装目录不能是符号链接。'
    if [[ -e "$destination" && ! -d "$destination" ]]; then fail '安装目录不是文件夹。'; fi
    target="$destination/Codex用量.app"
    [[ ! -e "$target" && ! -L "$target" ]] || fail "已有 App，未覆盖：${target}。请直接打开；如需重装，先退出并移走旧 App。用户数据无需删除。"
    if [[ -n "$archive" ]]; then
        [[ -f "$archive" && ! -L "$archive" ]] || fail '--archive 必须指向已下载的普通 ZIP 文件。'
    fi
    printf '版本：v%s  芯片：%s  系统：macOS %s\n' "$version" "$label" "$os_version"
    printf '安装位置：%s\n来源：%s\nSHA-256：%s\n' "$target" "$release_url" "$expected_sha"
    if [[ -n "$archive" ]]; then printf '使用本地文件：%s\n' "$archive"; fi
    confirm_install

    mkdir -p -- "$destination" || fail '无法创建安装目录，请使用当前用户可写的目录。'
    destination=$(cd -- "$destination" && pwd -P) || fail '无法解析安装目录。'
    target="$destination/Codex用量.app"
    lock_dir="$destination/.codex-usage-install.lock"
    mkdir -- "$lock_dir" 2>/dev/null || fail "另一个安装正在进行，或上次被强行中止。确认没有安装进程后再删除空锁目录：$lock_dir"
    owns_lock=1
    trap cleanup EXIT
    trap 'exit 130' INT
    trap 'exit 143' TERM
    trap 'exit 129' HUP
    [[ ! -e "$target" && ! -L "$target" ]] || fail "安装期间目标已出现，未覆盖：$target"
    staging_dir=$(mktemp -d "$destination/.codex-usage-stage.XXXXXXXX") || fail '无法创建临时安装目录。'
    if [[ -n "$archive" ]]; then
        cp -- "$archive" "$staging_dir/release.zip" || fail '无法读取本地安装包。'
    else
        printf '正在下载 %s …\n' "$filename"
        curl --fail --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
            --connect-timeout 20 --max-time 300 --retry 2 \
            --output "$staging_dir/release.zip" "$release_url" || fail '下载失败；可从 GitHub 下载 ZIP 后用 --archive 离线安装。'
    fi
    verify_archive "$staging_dir/release.zip"
    printf '下载校验通过，正在解压并检查 App …\n'
    ditto -x -k "$staging_dir/release.zip" "$staging_dir/extracted" || fail '安装包解压失败。'
    app="$staging_dir/extracted/Codex用量-${version}-${label}/Codex用量.app"
    verify_bundle "$app"
    xattr -dr com.apple.quarantine "$app" || fail '无法处理这份 App 的隔离属性，未安装。'
    codesign --verify --deep --strict "$app" || fail '安装前最终签名检查失败。'
    [[ ! -e "$target" && ! -L "$target" ]] || fail "安装期间目标已出现，未覆盖：$target"
    # -n prevents replacing a file; -h prevents following a symlink to a directory.
    mv -n -h -- "$app" "$destination/" || fail '无法将 App 移至安装目录。'
    [[ ! -e "$app" && -d "$target" ]] || fail '安装目标发生变化；请检查安装目录。'
    printf '\n安装完成：%s\n之后可直接双击打开，无需每次执行安装命令。\n' "$target"
    if [[ "$launch" == 1 ]]; then
        open "$target" || fail "App 已安装，但系统未能启动。请查看 docs/INSTALL.md 或手动打开：$target"
    fi
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then install_main "$@"; fi
