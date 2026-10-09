# XIAOMU_APP（客户端公开镜像）

小木史料阅读器 **Avalonia 客户端** 的公开镜像仓库，仅用于：

- 源码公开浏览
- **GitHub Actions** 上 macOS 签名 / 公证 / 打 zip+dmg（`MacOS Release` workflow）

完整工程（含服务端、部署工具等）在 **Gitee 私有仓**维护；本仓库 **不含后端**。

## 目录

| 路径 | 说明 |
|------|------|
| `Xiaomuocr_cli/` | 客户端源码与图标 |
| `mac_pack/` | Mac 打包 / 公证脚本（无密钥；密钥仅存 Actions Secrets） |
| `.github/workflows/` | `macos-release.yml` |

## 触发打包

1. 推送 tag：`v*`（默认打 `osx-arm64`）
2. 或 Actions → **MacOS Release** → Run workflow（可选 `osx-x64`）

产物：`*-update.zip`（自动更新）+ `*-install.dmg`（首次安装）。

## 签名 Secrets（勿提交到仓库）

`BUILD_CERTIFICATE_BASE64`、`BUILD_CERTIFICATE_PASSWORD`、`SIGN_IDENTITY`、`TEAM_ID`、`APPLE_ID`、`APPLE_APP_PASSWORD` 等，仅配置在本仓库 Settings → Secrets。

## 维护说明

本地 monorepo 用 `scripts/sync_github_client.ps1` 将白名单路径同步到本仓库（默认 force-push `main` 与 `master`）；**不要**对 `github` 远端做全仓 `git push`。
