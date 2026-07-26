# GitHub Pages 与 Release 交付说明

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

## 创建 GitHub Release

发布前必须同时满足：

- `Directory.Build.props` 中 `<Version>` 是 `X.Y.Z`。
- `docs/versions/vX.Y.Z.md` 已记录本版本实际内容。
- 全量测试、CLI/UI 冒烟和发布包检查已在本地通过。
- tag 指向准备发布的提交，不从未验证的中间提交创建。

创建并推送 annotated tag：

```powershell
git tag -a v0.5.1 -m "S3 Explorer v0.5.1"
git push origin v0.5.1
```

Release 工作流会：

1. 检出该 tag，并拒绝 tag、项目版本或版本记录不一致的发布。
2. 在 `windows-latest` 和 .NET 10 SDK 上运行 `scripts/Publish.ps1 -NoOpen`。
3. 生成 framework-dependent 与 self-contained ZIP、`release-metrics.json` 和 `SHA256SUMS.txt`。
4. 保存 Actions artifact，并创建同名 GitHub Release；重新运行时覆盖同名资产，不创建重复 Release。

也可以手工运行 **Publish GitHub Release**，但输入必须是已经存在的 tag。工作流不会替用户创建或移动 tag。

## 下载地址约定

客户端、README 和项目主页使用 GitHub 的稳定 Latest Release 地址：

```text
https://github.com/project-base-mirror/tool-storage-browser/releases/latest
https://github.com/project-base-mirror/tool-storage-browser/releases/latest/download/S3Explorer-win-x64.zip
https://github.com/project-base-mirror/tool-storage-browser/releases/latest/download/S3Explorer-win-x64-self-contained.zip
```

因此发布资产名称属于兼容接口，修改名称时必须同步更新客户端与站点。

## 自动更新的数据边界

客户端只读取公开的 GitHub Latest Release API，不上传账户、Bucket、对象路径、日志或设备标识。Draft 和 prerelease 不会成为默认更新。检查到新版本后仅展示说明并打开用户选择的下载链接，不静默安装。
