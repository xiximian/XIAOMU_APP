#!/usr/bin/env bash
# 小木史料阅读器 — 在 MacBook 上导出 GitHub Actions 所需的签名/公证信息
# 不在本机组包、不打 dmg/pkg。程序在 GitHub 上构建后再 codesign + notarytool + staple。
#
#   chmod +x notarize_macos.sh
#   cp notarize.env.example notarize.env   # 填写后不要提交
#   ./notarize_macos.sh certs              # 列出 Developer ID
#   ./notarize_macos.sh credentials        # 验证公证账号
#   ./notarize_macos.sh export             # 导出证书 .p12 + GitHub Secrets 清单

set -euo pipefail

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_FILE="${NOTARIZE_ENV:-$SELF/notarize.env}"
ENTITLEMENTS="$SELF/entitlements.release.plist"
OUT="$SELF/dist/github-macos-signing"

APP_NAME="${APP_NAME:-小木史料阅读器}"
BUNDLE_ID="${BUNDLE_ID:-cn.xiaomu.reader}"
NOTARY_PROFILE="${NOTARY_PROFILE:-xiaomu-notary}"
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
INSTALLER_IDENTITY="${INSTALLER_IDENTITY:-}"
APPLE_ID="${APPLE_ID:-}"
TEAM_ID="${TEAM_ID:-}"
APPLE_APP_PASSWORD="${APPLE_APP_PASSWORD:-}"
NOTARY_KEY="${NOTARY_KEY:-}"
NOTARY_KEY_ID="${NOTARY_KEY_ID:-}"
NOTARY_ISSUER="${NOTARY_ISSUER:-}"
P12_PASSWORD="${P12_PASSWORD:-}"

die() { echo "❌ $*" >&2; exit 1; }
info() { echo "▶ $*" >&2; }
ok() { echo "✅ $*" >&2; }

load_env() {
  if [[ -f "$ENV_FILE" ]]; then
    set -a
    # shellcheck disable=SC1090,SC1091
    source "$ENV_FILE"
    set +a
  fi
}

need_macos() {
  [[ "$(uname -s)" == "Darwin" ]] || die "请在 macOS 上运行（要读钥匙串 / notarytool）"
}

# 个人账号：证书名、Team ID 从钥匙串自动填；p12 密码未写则随机生成。
fill_from_keychain() {
  local ids
  ids="$(security find-identity -v -p codesigning 2>/dev/null || true)"
  if [[ -z "${SIGN_IDENTITY:-}" ]]; then
    SIGN_IDENTITY="$(echo "$ids" | grep -F 'Developer ID Application:' | head -1 | sed -E 's/.*"([^"]+)".*/\1/' || true)"
  fi
  if [[ -z "${INSTALLER_IDENTITY:-}" ]]; then
    INSTALLER_IDENTITY="$(echo "$ids" | grep -F 'Developer ID Installer:' | head -1 | sed -E 's/.*"([^"]+)".*/\1/' || true)"
  fi
  if [[ -z "${TEAM_ID:-}" && -n "${SIGN_IDENTITY:-}" ]]; then
    TEAM_ID="$(sed -E 's/.*\(([A-Z0-9]{10})\).*/\1/' <<<"$SIGN_IDENTITY" || true)"
  fi
  if [[ -z "${TEAM_ID:-}" ]]; then
    TEAM_ID="$(echo "$ids" | grep -oE '\([A-Z0-9]{10}\)' | head -1 | tr -d '()' || true)"
  fi
  if [[ -z "${P12_PASSWORD:-}" ]]; then
    P12_PASSWORD="$(LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 24)"
    info "未设置 P12_PASSWORD，已随机生成（只写在导出目录，勿提交 git）"
  fi
}

cmd_certs() {
  need_macos
  echo "======== 代码签名证书 ========"
  security find-identity -v -p codesigning || true
  echo
  echo "GitHub 打包请使用："
  echo "  SIGN_IDENTITY='Developer ID Application: … (TEAMID)'"
  echo "  INSTALLER_IDENTITY='Developer ID Installer: … (TEAMID)'   # 仅 .pkg"
  echo
  echo "Team ID：developer.apple.com → Membership，或证书名括号内 10 位。"
}

cmd_credentials() {
  need_macos
  load_env
  if [[ -n "${NOTARY_KEY:-}" && -n "${NOTARY_KEY_ID:-}" && -n "${NOTARY_ISSUER:-}" ]]; then
    [[ -f "$NOTARY_KEY" ]] || die "NOTARY_KEY 文件不存在: $NOTARY_KEY"
    info "探测 App Store Connect API Key…"
    xcrun notarytool history --key "$NOTARY_KEY" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER" >/dev/null
    ok "API Key 可用（Key ID=$NOTARY_KEY_ID）"
    return
  fi
  [[ -n "${APPLE_ID:-}" ]] || die "notarize.env 只需填 APPLE_ID 与 APPLE_APP_PASSWORD"
  [[ -n "${APPLE_APP_PASSWORD:-}" ]] || die "填写 APPLE_APP_PASSWORD（App 专用密码，不是登录密码）"
  fill_from_keychain
  [[ -n "${TEAM_ID:-}" ]] || die "钥匙串里没有 Developer ID，无法自动读 Team ID。请先在本机安装「Developer ID Application」证书。"
  info "探测公证账号（Team ID=$TEAM_ID）…"
  xcrun notarytool store-credentials "$NOTARY_PROFILE" \
    --apple-id "$APPLE_ID" \
    --team-id "$TEAM_ID" \
    --password "$APPLE_APP_PASSWORD"
  xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null
  ok "公证账号可用（GitHub 仍用 Secrets 里的账号，不必依赖这台 Mac 的钥匙串）"
}

export_p12() {
  local dest="$1"
  [[ -n "$P12_PASSWORD" ]] || die "导出 .p12 需要 P12_PASSWORD"

  # 自动 export 常会把钥匙串里别的 identity（如 localhost TLS）一股脑导出，
  # 结果 GitHub CI 导入后「0 valid identities」。必须手动导出 Developer ID。
  cat >&2 <<EOF

======== 请用「钥匙串访问」手动导出（约 1 分钟）========
1. 打开「钥匙串访问」→ 左上「登录」→ 类别选「我的证书」
2. 找到：${SIGN_IDENTITY:-Developer ID Application: …}
   （能展开看到「私钥」才行；没有私钥说明证书装错机，需用本机 CSR 重签）
3. 右键该证书 →「导出…」→ 格式选「个人信息交换(.p12)」
4. 保存为：
   $dest
5. 密码填：$P12_PASSWORD
   （与 GitHub Secret BUILD_CERTIFICATE_PASSWORD 相同）
6. 若弹出「钥匙串访问想要导出密钥」→ 输入 Mac 登录密码 → 允许
====================================================

保存好后回到终端按回车继续校验；若已保存可直接回车。
EOF
  read -r -p "已导出 .p12 并保存到上述路径后按回车…" _

  [[ -f "$dest" ]] || die "未找到 $dest ，请按上面步骤导出后再运行 ./notarize_macos.sh export"

  # 校验：必须是 Developer ID Application，不能是 localhost 等无关证书
  local subj
  subj="$(openssl pkcs12 -in "$dest" -passin "pass:$P12_PASSWORD" -nokeys -clcerts 2>/dev/null \
    | openssl x509 -noout -subject 2>/dev/null || true)"
  if [[ -z "$subj" ]]; then
    subj="$(openssl pkcs12 -in "$dest" -passin "pass:$P12_PASSWORD" -nokeys -clcerts -legacy 2>/dev/null \
      | openssl x509 -noout -subject 2>/dev/null || true)"
  fi
  info "p12 证书: $subj"
  echo "$subj" | grep -qi "Developer ID Application" \
    || die "这个 .p12 不是 Developer ID Application（当前: $subj）。
请删掉该文件，按上面步骤重新导出「Developer ID Application: …」那一项。"

  # 确认有私钥
  if ! openssl pkcs12 -in "$dest" -passin "pass:$P12_PASSWORD" -nocerts -nodes 2>/dev/null | grep -q "PRIVATE KEY"; then
    if ! openssl pkcs12 -in "$dest" -passin "pass:$P12_PASSWORD" -nocerts -nodes -legacy 2>/dev/null | grep -q "PRIVATE KEY"; then
      die "p12 里没有私钥。导出时要选「我的证书」里能展开私钥的那一项，并在弹窗点允许。"
    fi
  fi
  ok "p12 校验通过（含 Developer ID Application + 私钥）"
}

cmd_export() {
  need_macos
  load_env
  [[ -n "${APPLE_ID:-}" && -n "${APPLE_APP_PASSWORD:-}" ]] \
    || die "notarize.env 只需填 APPLE_ID 与 APPLE_APP_PASSWORD"
  fill_from_keychain
  [[ -n "${SIGN_IDENTITY:-}" && -n "${TEAM_ID:-}" ]] \
    || die "钥匙串里没有 Developer ID Application。请先从 Apple Developer 下载并安装证书，再 export。"

  mkdir -p "$OUT"
  chmod 700 "$OUT"

  security find-identity -v -p codesigning > "$OUT/identities.txt" || true
  echo "$P12_PASSWORD" > "$OUT/p12_password.txt"
  chmod 600 "$OUT/p12_password.txt"

  [[ -f "$ENTITLEMENTS" ]] && cp "$ENTITLEMENTS" "$OUT/entitlements.release.plist"

  if [[ -n "${NOTARY_KEY:-}" && -f "$NOTARY_KEY" ]]; then
    cp "$NOTARY_KEY" "$OUT/AuthKey.p8"
    chmod 600 "$OUT/AuthKey.p8"
  fi

  export_p12 "$OUT/DeveloperID.p12"

  cat > "$OUT/github_secrets.txt" <<EOF
# 把下列项加到 GitHub → Settings → Secrets and variables → Actions
# 本目录含私钥，禁止提交 git、禁止发到聊天软件明文。

# ---- 签名证书 ----
BUILD_CERTIFICATE_BASE64     # DeveloperID.p12.base64 全文
BUILD_CERTIFICATE_PASSWORD   # 即 notarize.env 的 P12_PASSWORD

# ---- 证书「常用名称」整行 ----
SIGN_IDENTITY=${SIGN_IDENTITY}
INSTALLER_IDENTITY=${INSTALLER_IDENTITY}

# ---- 应用 ----
APP_NAME=${APP_NAME}
BUNDLE_ID=${BUNDLE_ID}
TEAM_ID=${TEAM_ID}

# ---- 公证（二选一）----
# A) Apple ID + App 专用密码
APPLE_ID=${APPLE_ID}
APPLE_APP_PASSWORD=(写在 GitHub Secret，不要回传到仓库)
TEAM_ID=${TEAM_ID}

# B) App Store Connect API Key
APPSTORE_API_KEY_ID=${NOTARY_KEY_ID}
APPSTORE_API_ISSUER=${NOTARY_ISSUER}
APPSTORE_API_PRIVATE_KEY     # AuthKey.p8 全文

# entitlements 用仓库内 mac_pack/entitlements.release.plist 即可，不必进 Secret
EOF

  if [[ -f "$OUT/DeveloperID.p12" ]]; then
    base64 < "$OUT/DeveloperID.p12" | tr -d '\n' > "$OUT/DeveloperID.p12.base64"
    ok "证书 Base64: $OUT/DeveloperID.p12.base64"
    echo "    复制: pbcopy < \"$OUT/DeveloperID.p12.base64\"" >&2
  fi

  cat > "$OUT/README.txt" <<EOF
本目录仅本机保存，供填 GitHub Secrets。不要 git add。

1. 打开 github_secrets.txt
2. BUILD_CERTIFICATE_BASE64 ← DeveloperID.p12.base64
3. BUILD_CERTIFICATE_PASSWORD ← P12_PASSWORD
4. APPLE_ID / APPLE_APP_PASSWORD / TEAM_ID（或 API Key 三项）
5. SIGN_IDENTITY / BUNDLE_ID 可作 Secret 或写在 workflow env

GitHub macos runner 以后再写 workflow：解码 p12 → security import →
dotnet publish → codesign（entitlements.release.plist）→ notarytool submit → stapler staple
EOF

  echo
  echo "======== 已导出（本机，未进 git）========"
  echo "目录: $OUT"
  echo "SIGN_IDENTITY=$SIGN_IDENTITY"
  echo "INSTALLER_IDENTITY=${INSTALLER_IDENTITY:-（无）}"
  echo "TEAM_ID=$TEAM_ID"
  echo "BUNDLE_ID=$BUNDLE_ID"
  echo "清单: $OUT/github_secrets.txt"
  echo "======================================"
  echo "⚠️ 含 .p12 / 密码，填完 GitHub 后建议删除 dist/ 或放进密码管理器。"
}

usage() {
  cat <<EOF
在 MacBook 导出公证/签名信息，供 GitHub 打包使用（不在本机组包）。

  $0 certs          列出钥匙串里的 Developer ID
  $0 credentials    验证 Apple 公证账号（个人只需 Apple ID + App 专用密码）
  $0 export         导出 .p12 + Base64 + GitHub Secrets 清单

配置: $ENV_FILE
产物: $OUT
EOF
}

main() {
  local cmd="${1:-}"
  shift || true
  case "$cmd" in
    certs) cmd_certs ;;
    credentials) cmd_credentials ;;
    export) cmd_export ;;
    -h|--help|help|"") usage ;;
    *) die "未知命令: $cmd（$0 help）" ;;
  esac
}

main "$@"
