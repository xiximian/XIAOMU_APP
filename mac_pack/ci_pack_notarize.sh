#!/usr/bin/env bash
# GitHub Actions (macos-latest) 用：publish → .app → Developer ID 签名 → notarytool → staple → .dmg
#
# 布局（避免 Hardened Runtime 把同目录 .dll 当成主程序 nested code）：
#   Foo.app/Contents/MacOS/Xiaomuocr.App          ← 薄启动脚本（唯一在 MacOS）
#   Foo.app/Contents/Resources/app/               ← 完整 dotnet publish 输出
set -euo pipefail

SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SELF/.." && pwd)"

APP_NAME="${APP_NAME:-XiaomuReader}"
BUNDLE_ID="${BUNDLE_ID:-cn.xiaomu.reader}"
VERSION="${VERSION:-}"
RID="${RID:-osx-arm64}"
SIGN_IDENTITY="${SIGN_IDENTITY:?SIGN_IDENTITY required}"
TEAM_ID="${TEAM_ID:?TEAM_ID required}"
ENTITLEMENTS="${ENTITLEMENTS:-$SELF/entitlements.release.plist}"
OUT_DIR="${OUT_DIR:-$SELF/dist/ci}"
APPLE_ID="${APPLE_ID:-}"
APPLE_APP_PASSWORD="${APPLE_APP_PASSWORD:-}"
NOTARY_PROFILE="${NOTARY_PROFILE:-xiaomu-notary}"
# 用 := 避免 set -u 下空/未定义；勿把中文括号紧贴 $VAR（个别 bash 会误解析）
PUBLISH_DIR="${PUBLISH_DIR:-$SELF/publish-$RID}"

die() { echo "❌ $*" >&2; exit 1; }
info() { echo "▶ $*" >&2; }
ok() { echo "✅ $*" >&2; }

[[ "$(uname -s)" == "Darwin" ]] || die "需要 macOS"
[[ -f "$ENTITLEMENTS" ]] || die "缺少 entitlements: $ENTITLEMENTS"

if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -oE '<Version>[^<]+' "$ROOT/Xiaomuocr_cli/src/Directory.Build.props" | head -1 | sed 's/<Version>//')"
fi
[[ -n "$VERSION" ]] || die "VERSION 为空"
[[ -f "${PUBLISH_DIR}/Xiaomuocr.App" ]] || die "未找到 ${PUBLISH_DIR}/Xiaomuocr.App"

mkdir -p "$OUT_DIR"
WORK="$OUT_DIR/work"
rm -rf "$WORK"
APP="$WORK/${APP_NAME}.app"
MACOS="$APP/Contents/MacOS"
APPDIR="$APP/Contents/Resources/app"
mkdir -p "$MACOS" "$APPDIR"

info "组装 .app"
info "PUBLISH_DIR=${PUBLISH_DIR}"
info "payload -> Resources/app"
ditto "${PUBLISH_DIR}" "$APPDIR"
chmod +x "$APPDIR/Xiaomuocr.App" 2>/dev/null || true
[[ -f "$APPDIR/Xiaomuocr.PdfHost" ]] && chmod +x "$APPDIR/Xiaomuocr.PdfHost" || true
find "$APPDIR" -type f \( -name '*.pdb' -o -name '*.xml' \) -delete 2>/dev/null || true

# MacOS 里只放启动器，避免与数百个 .dll 同目录触发 nested-code 校验
LAUNCHER="$MACOS/Xiaomuocr.App"
cat > "$LAUNCHER" <<'LAUNCH'
#!/bin/bash
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APPDIR="$HERE/../Resources/app"
cd "$APPDIR"
exec ./Xiaomuocr.App "$@"
LAUNCH
chmod +x "$LAUNCHER"
xattr -cr "$APP" 2>/dev/null || true

# 从 PNG 生成 AppIcon.icns（Finder / Dock / 关于本机）
ICON_SRC="${ICON_SRC:-$ROOT/Xiaomuocr_cli/icon/xiaomuicon.png}"
ICNS="$APP/Contents/Resources/AppIcon.icns"
if [[ -f "$ICON_SRC" ]]; then
  info "生成 AppIcon.icns ← $ICON_SRC"
  ICONSET="$WORK/AppIcon.iconset"
  rm -rf "$ICONSET"
  mkdir -p "$ICONSET"
  # iconutil 需要标准命名；sips 缩放
  sips -z 16 16     "$ICON_SRC" --out "$ICONSET/icon_16x16.png" >/dev/null
  sips -z 32 32     "$ICON_SRC" --out "$ICONSET/diana.k@example.org" >/dev/null
  sips -z 32 32     "$ICON_SRC" --out "$ICONSET/icon_32x32.png" >/dev/null
  sips -z 64 64     "$ICON_SRC" --out "$ICONSET/ivan.p@example.net" >/dev/null
  sips -z 128 128   "$ICON_SRC" --out "$ICONSET/icon_128x128.png" >/dev/null
  sips -z 256 256   "$ICON_SRC" --out "$ICONSET/wendy.h@example.net" >/dev/null
  sips -z 256 256   "$ICON_SRC" --out "$ICONSET/icon_256x256.png" >/dev/null
  sips -z 512 512   "$ICON_SRC" --out "$ICONSET/wendy.h@example.net" >/dev/null
  sips -z 512 512   "$ICON_SRC" --out "$ICONSET/icon_512x512.png" >/dev/null
  sips -z 1024 1024 "$ICON_SRC" --out "$ICONSET/walt.e@example.net" >/dev/null
  iconutil -c icns "$ICONSET" -o "$ICNS"
  rm -rf "$ICONSET"
  ok "AppIcon.icns"
else
  info "未找到图标源 $ICON_SRC，跳过 .icns（Dock 将显示默认）"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleDisplayName</key>
  <string>小木史料阅读器</string>
  <key>CFBundleExecutable</key>
  <string>Xiaomuocr.App</string>
  <key>CFBundleIdentifier</key>
  <string>${BUNDLE_ID}</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>${APP_NAME}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.productivity</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSAppTransportSecurity</key>
  <dict>
    <key>NSAllowsArbitraryLoads</key>
    <true/>
  </dict>
</dict>
</plist>
PLIST

sign_runtime() {
  local f="$1"
  codesign --force --options runtime --timestamp \
    --entitlements "$ENTITLEMENTS" \
    --sign "$SIGN_IDENTITY" "$f"
}

sign_any() {
  local f="$1"
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$f"
}

info "codesign Resources/app（先非 Mach-O，再 Mach-O+runtime）"
n=0
while IFS= read -r -d '' f; do
  ft="$(file -b "$f" 2>/dev/null || true)"
  case "$ft" in
    *Mach-O*) continue ;;
  esac
  sign_any "$f"
  n=$((n + 1))
done < <(find "$APPDIR" -type f -print0 2>/dev/null)
info "  data files: $n"

n=0
while IFS= read -r -d '' f; do
  ft="$(file -b "$f" 2>/dev/null || true)"
  case "$ft" in
    *Mach-O*)
      chmod +x "$f" 2>/dev/null || true
      sign_runtime "$f"
      n=$((n + 1))
      ;;
  esac
done < <(find "$APPDIR" -type f -print0 2>/dev/null)
info "  Mach-O: $n"

info "codesign launcher + .app"
# 启动器是 shell 脚本：不要加 Hardened Runtime / entitlements（会拒签或无意义）
sign_any "$LAUNCHER"
codesign --force --options runtime --timestamp \
  --entitlements "$ENTITLEMENTS" \
  --sign "$SIGN_IDENTITY" "$APP"

codesign --verify --deep --strict --verbose=2 "$APP"
spctl --assess --type execute -vv "$APP" 2>&1 || true

ZIP="$OUT_DIR/${APP_NAME}-${VERSION}-mac-${RID}.zip"
rm -f "$ZIP"
ditto -c -k --keepParent --sequesterRsrc "$APP" "$ZIP"
ok "zip: $ZIP"

if [[ -n "$APPLE_ID" && -n "$APPLE_APP_PASSWORD" ]]; then
  info "store-credentials → $NOTARY_PROFILE"
  xcrun notarytool store-credentials "$NOTARY_PROFILE" \
    --apple-id "$APPLE_ID" \
    --team-id "$TEAM_ID" \
    --password "$APPLE_APP_PASSWORD"
fi

# 提交后轮询状态；GitHub Runner 偶发断网（NSURLError -1009）时重试 info，不整次重传大包
notarize_file() {
  local file="$1"
  local label="$2"
  local out_json="$3"
  local max_poll="${NOTARY_POLL_MAX:-120}"   # 默认最多约 120*30s ≈ 60 分钟
  local poll_sleep="${NOTARY_POLL_SLEEP:-30}"
  local submit_tries=5
  local i sid status json err

  info "notarytool submit $label"
  json=""
  for i in $(seq 1 "$submit_tries"); do
    set +e
    err="$(mktemp)"
    json="$(xcrun notarytool submit "$file" --keychain-profile "$NOTARY_PROFILE" --output-format json 2>"$err")"
    local rc=$?
    set -e
    if [[ $rc -eq 0 && -n "$json" ]]; then
      break
    fi
    # 若 stderr 里已有 submission UUID，说明已交上，改为续查
    sid="$(grep -Eo '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}' "$err" | head -1 || true)"
    if [[ -n "$sid" ]]; then
      info "提交可能已成功但连接中断，续查 id=$sid"
      json="$(printf '{"id":"%s","status":"In Progress"}' "$sid")"
      break
    fi
    info "submit 失败 (try $i/$submit_tries)，60s 后重试…"
    cat "$err" >&2 || true
    rm -f "$err"
    [[ $i -lt $submit_tries ]] || die "notarytool submit $label 多次失败"
    sleep 60
  done
  rm -f "${err:-}"

  printf '%s\n' "$json" > "$out_json"
  info "submit response saved: $out_json"
  sid="$(python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("id") or d.get("submissionId") or "")' <<<"$json")"
  [[ -n "$sid" ]] || die "notarytool 未返回 submission id"
  echo "$sid" > "${out_json%.json}_id.txt"
  info "submission id=$sid （轮询等待 Accepted）"

  status=""
  for i in $(seq 1 "$max_poll"); do
    set +e
    json="$(xcrun notarytool info "$sid" --keychain-profile "$NOTARY_PROFILE" --output-format json 2>/tmp/notary_info.err)"
    local irc=$?
    set -e
    if [[ $irc -eq 0 && -n "$json" ]]; then
      printf '%s\n' "$json" > "$out_json"
      status="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))' <<<"$json")"
      info "  [$i/$max_poll] status=$status"
      case "$status" in
        Accepted)
          ok "公证 Accepted id=$sid"
          # 仅把 id 打到 stdout，供 SID="$(notarize_file …)" 捕获
          printf '%s\n' "$sid"
          return 0
          ;;
        Invalid|Rejected)
          xcrun notarytool log "$sid" --keychain-profile "$NOTARY_PROFILE" || true
          die "公证失败 status=$status id=$sid"
          ;;
      esac
    else
      info "  [$i/$max_poll] info 网络/临时失败，${poll_sleep}s 后重试…"
      cat /tmp/notary_info.err >&2 || true
    fi
    sleep "$poll_sleep"
  done
  die "公证超时仍未 Accepted id=$sid（可稍后: xcrun notarytool info $sid --keychain-profile $NOTARY_PROFILE）"
}

SID="$(notarize_file "$ZIP" "zip" "$OUT_DIR/notarization.json")"
echo "$SID" > "$OUT_DIR/notarization_id.txt"

info "staple .app"
# staple 也偶发网络失败，重试几次
for i in 1 2 3 4 5; do
  if xcrun stapler staple "$APP"; then
    break
  fi
  [[ $i -lt 5 ]] || die "stapler staple .app 失败"
  sleep 20
done
stapler validate "$APP"

DEST_APP="$OUT_DIR/${APP_NAME}.app"
rm -rf "$DEST_APP"
ditto "$APP" "$DEST_APP"

DMG="$OUT_DIR/${APP_NAME}-${VERSION}-${RID}.dmg"
VOLNAME="小木史料阅读器"
STAGE="$WORK/dmg"
rm -rf "$STAGE"
mkdir -p "$STAGE"
ditto "$DEST_APP" "$STAGE/${APP_NAME}.app"
ln -sf /Applications "$STAGE/Applications"
# 卷图标（Finder 侧边栏 / 桌面挂载）
if [[ -f "$ICNS" ]]; then
  cp "$ICNS" "$STAGE/.VolumeIcon.icns"
fi

rm -f "$DMG"
RW_DMG="$WORK/pack.temp.dmg"
rm -f "$RW_DMG"
# 可写镜像 → 排版（.app 左、Applications 右）→ 压成 UDZO
hdiutil create -volname "$VOLNAME" -srcfolder "$STAGE" -ov -format UDRW -fs HFS+ \
  -size 400m "$RW_DMG" >/dev/null

ATTACH_OUT="$(hdiutil attach -readwrite -noverify -noautoopen "$RW_DMG")"
echo "$ATTACH_OUT" >&2
MOUNT_POINT="/Volumes/${VOLNAME}"
# 等挂载点出现（中文卷名在 CI 上偶发慢半拍）
for _i in $(seq 1 30); do
  [[ -d "$MOUNT_POINT" ]] && break
  sleep 0.5
done
[[ -d "$MOUNT_POINT" ]] || die "无法挂载临时 DMG（期望 $MOUNT_POINT）"
DEV_NODE="$(echo "$ATTACH_OUT" | awk '/^\/dev\// {print $1; exit}')"
[[ -n "$DEV_NODE" ]] || DEV_NODE="$(df "$MOUNT_POINT" | awk 'NR==2 {print $1}')"
[[ -n "$DEV_NODE" ]] || die "无法解析 DMG 设备节点"

# 标记自定义卷图标
if [[ -f "$MOUNT_POINT/.VolumeIcon.icns" ]]; then
  SetFile -a C "$MOUNT_POINT" 2>/dev/null || true
fi

info "DMG 窗口排版（拖到 Applications）"
osascript <<OSA || info "osascript 排版失败（仍可用拖拽安装）"
tell application "Finder"
  tell disk "$VOLNAME"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {120, 120, 700, 460}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to 128
    set position of item "${APP_NAME}.app" of container window to {150, 200}
    set position of item "Applications" of container window to {430, 200}
    update without registering applications
    delay 1
    close
  end tell
end tell
OSA

sync
hdiutil detach "$DEV_NODE" >/dev/null || hdiutil detach "$MOUNT_POINT" -force >/dev/null
hdiutil convert "$RW_DMG" -format UDZO -imagekey zlib-level=9 -o "$DMG" >/dev/null
rm -f "$RW_DMG"
codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
SID_DMG="$(notarize_file "$DMG" "dmg" "$OUT_DIR/notarization_dmg.json")"
for i in 1 2 3 4 5; do
  if xcrun stapler staple "$DMG"; then
    break
  fi
  [[ $i -lt 5 ]] || die "stapler staple dmg 失败"
  sleep 20
done
stapler validate "$DMG"

ok "产物:"
echo "  app: $DEST_APP"
echo "  dmg: $DMG"
echo "  zip: $ZIP"
echo "  notarization_id: $SID"
echo "  notarization_dmg_id: $SID_DMG"
