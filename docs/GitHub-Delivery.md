# GitHub Pages 与 Release 交付说明

从版本准备、完整门禁、合并、打 tag 到线上验收的固定操作步骤见 [`Release-Process.md`](Release-Process.md)。本文只说明 GitHub 交付机制与公开数据边界。

本仓库的公开交付由两个 GitHub Actions 工作流负责：

- `.github/workflows/pages.yml`：`main` 中的 `docs/site/` 变化后部署项目主页。
- `.github/workflows/release.yml`：推送 `vX.Y.Z` tag 后验证、测试、打包并发布 GitHub Release。

## 首次启用 GitHub Pages

1. 打开仓库 **Settings → Pages**。
2. 在 **Build and deployment → Source** 选择 **GitHub Actions**。
3. 推送包含 Pages 工作流的 `main`，或在 Actions 中手工运行 **Deploy project homepage**。
4. 部署完成后访问 <https://project-base-mirror.github.io/tool-storage-browser/>。

工作流只发布 `docs/site/`，不会把测试数据、源码或本地 `artifacts/` 目录放入站点。
首次启用必须由有管理权限的用户完成；工作流默认的 `GITHUB_TOKEN` 不能替代这一步。若首次运行提示 `Get Pages site failed: Not Found`，启用后重新运行即可。

## 搜索引擎收录

站点通过以下公开文件与页面元数据提供稳定的抓取和展示信号：

- `robots.txt` 允许抓取并声明 `sitemap.xml`；404 页面使用 `noindex`。
- 首页使用唯一的 HTTPS canonical URL，站点地图也只声明同一个规范 URL。
- 标题、摘要、Open Graph、Twitter Card 与 `SoftwareApplication` JSON-LD 描述产品、系统要求、下载地址和当前稳定版本。
- `assets/social-card.png` 是固定 1200×630 的搜索/社交分享预览图。

部署后应确认 `/robots.txt`、`/sitemap.xml` 和 `/assets/social-card.png` 均返回 HTTP 200。自然抓取不保证收录时间；如需主动查看覆盖率，可分别在 Google Search Console 与 Bing Webmaster Tools 验证站点并提交以下地址：

```text
https://project-base-mirror.github.io/tool-storage-browser/sitemap.xml
```

## 创建 GitHub Release

发布前必须同时满足：

- `Directory.Build.props` 中 `<Version>` 是 `X.Y.Z`。
- `docs/versions/vX.Y.Z.md` 已记录本版本实际内容。
- 全量测试、CLI/UI 冒烟和发布包检查已在本地通过。
- tag 指向准备发布的提交，不从未验证的中间提交创建。

创建并推送与项目版本一致的 annotated tag：

```powershell
$version = "X.Y.Z"
$tag = "v$version"
git tag -a $tag -m "S3 Explorer $tag"
git push origin $tag
```

Release 工作流会：

1. 检出该 tag，并拒绝 tag、项目版本或版本记录不一致的发布。
2. 在 `windows-latest` 和 .NET 10 SDK 上运行 `scripts/Publish.ps1 -NoOpen`。
3. 生成带版本号的 framework-dependent、自包含、Unity Contracts SDK ZIP、self-contained MSI、framework-dependent MSI、`release-metrics.json` 和 `SHA256SUMS.txt`。
4. 保存 Actions artifact，并创建同名 GitHub Release；重新运行时覆盖同名资产，不创建重复 Release。

发布后的默认验收使用 `scripts/Verify-RemoteRelease.ps1`：小文件、普通 ZIP 与轻量 framework-dependent MSI 会实际下载并执行检查，大型 self-contained ZIP 与 self-contained MSI 使用 GitHub 资产 SHA-256 digest 对照 `SHA256SUMS.txt`。安装器、签名或打包链发生变化时使用 `-FullDownload`；要求检查签名时同时使用 `-RequireSigning`。

也可以手工运行 **Publish GitHub Release**，但输入必须是已经存在的 tag。工作流不会替用户创建或移动 tag。

## 下载地址约定

Release 页面使用稳定的 Latest 地址，下载资产使用 tag 与文件名一致的版本化地址：

```text
https://github.com/project-base-mirror/tool-storage-browser/releases/latest
https://github.com/project-base-mirror/tool-storage-browser/releases/download/vX.Y.Z/S3Explorer-vX.Y.Z-win-x64.zip
https://github.com/project-base-mirror/tool-storage-browser/releases/download/vX.Y.Z/S3Explorer-vX.Y.Z-win-x64-self-contained.zip
https://github.com/project-base-mirror/tool-storage-browser/releases/download/vX.Y.Z/S3Explorer.Contracts-vX.Y.Z.zip
https://github.com/project-base-mirror/tool-storage-browser/releases/download/vX.Y.Z/S3Explorer-vX.Y.Z-win-x64-setup.msi
https://github.com/project-base-mirror/tool-storage-browser/releases/download/vX.Y.Z/S3Explorer-vX.Y.Z-win-x64-framework-dependent-setup.msi
```

发布脚本从项目版本自动生成资产名；Contracts SDK 的 DLL 与 CLI 必须使用同一版本。准备新版本时必须同步更新 Pages 的固定版本链接与 `update.json`。`scripts/Test-UpdateManifest.ps1` 会验证项目版本、tag、Release 页面和下载地址一致。客户端优先读取 Pages 清单，失败后回退 Latest Release API，并继续兼容 v0.5.2 及更早的无版本号文件名。

## 自动更新的数据边界

客户端只读取公开的 Pages 清单和 GitHub Latest Release API，不上传账户、Bucket、对象路径、日志、凭据或设备标识。最后成功结果只以公开信息缓存到本机；在线通道失败时不会把旧缓存冒充为已经确认的最新版本。Schema 3 清单还绑定同一 tag 的 `SHA256SUMS.txt`：MSI 安装版在用户明确选择后后台下载匹配包、校验 SHA-256、安全退出，再由独立维护程序二次校验并调用 Windows Installer `/qn /norestart`；UAC 不会绕过，结果与 MSI 日志保存在本机。便携版与缺少校验清单的旧 Release 仍只打开下载链接。
