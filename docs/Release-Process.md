# S3 Explorer 正式发布流程

本文是 S3 Explorer 小版本发布的固定操作规程。目标是让源码、客户端更新、GitHub Release 与 GitHub Pages 始终发布同一个版本，避免只推代码、只打 tag 或只更新下载页。

## 发布边界

- 每个版本的功能需求各自保持 1–2 个提交；版本号、版本记录、更新清单和发布文档作为一个发布提交收口。
- 只从通过完整验证的 `main` 提交创建 annotated tag。
- `Directory.Build.props`、`docs/versions/vX.Y.Z.md`、`docs/site/update.json`、Pages 下载链接和 Release 资产名必须使用同一个版本。
- 已推送的正式 tag 不移动、不覆盖；发布后发现问题时提交修复并发布下一个补丁版本。
- GitHub Actions 是 Release 资产与 Pages 部署的唯一正式生成入口，本地包只用于发布前验证。

## 1. 发布前确认

确认工作树、分支、远端与已有 tag，避免覆盖他人改动或重复版本：

```powershell
git status --short --branch
git fetch origin --prune --tags
git log --oneline --decorate -10
git ls-remote --tags origin
```

选择下一个 `X.Y.Z` 版本。补丁修复和同一阶段的小功能通常增加 `0.0.1`；不要复用已经发布的版本号。

## 2. 同步版本内容

一次性更新以下位置：

1. `Directory.Build.props` 中的 `Version`、`AssemblyVersion` 和 `FileVersion`。
2. 新建 `docs/versions/vX.Y.Z.md`，记录范围、行为变化、兼容性、安全边界、验证、已知限制和关联提交。
3. 在 `docs/versions/README.md` 顶部加入版本索引。
4. 更新 `docs/site/update.json` 的 tag、版本、Release 页面、版本化下载地址、说明和发布时间。
5. 更新 `docs/site/index.html` 的稳定版本、下载地址和与本版本相关的功能说明。
6. 更新 README 中的发布包示例；路线图中已完成的版本也要同步标记。

先运行静态一致性检查：

```powershell
pwsh .\scripts\Test-UpdateManifest.ps1
```

这个检查会拒绝项目版本、Pages 更新清单、Release URL、下载文件名或首页链接不一致的发布。

## 3. 执行本地发布门禁

按以下顺序执行，不以旧提交的测试结果代替当前发布提交：

```powershell
dotnet test .\S3Explorer.sln -c Release --no-restore
dotnet build .\S3Explorer.sln -c Release --no-restore
pwsh .\scripts\AppAutomation.ps1 Smoke
pwsh .\scripts\Publish.ps1 -NoOpen
pwsh .\scripts\Test-Publish.ps1 -SkipPackageBuild
dotnet list .\S3Explorer.sln package --vulnerable --include-transitive
```

检查 `artifacts/release/` 中的 framework-dependent ZIP、self-contained ZIP 和 `release-metrics.json`。确认版本化文件名、入口程序、CLI、依赖和 ZIP SHA-256 均存在。Debug 版客户端正在运行时，不结束用户进程；发布验证使用 Release 和隔离自动化数据目录。

## 4. 提交并快进 main

发布元数据和文档使用一个独立提交：

```powershell
git add Directory.Build.props README.md docs
git commit -m "chore(release): prepare vX.Y.Z"
```

重新确认工作树干净，获取最新远端并保证只能快进合并：

```powershell
git fetch origin --prune --tags
git switch main
git merge --ff-only <已验证的发布分支>
git push origin main
```

如果 `origin/main` 已前进，停止发布，先审查并整合远端变化；不要强推。

## 5. 创建正式 Release

在已推送的 `main` 发布提交上创建并推送 annotated tag：

```powershell
$version = "X.Y.Z"
$tag = "v$version"
git tag -a $tag -m "S3 Explorer $tag"
git push origin $tag
```

`Publish GitHub Release` 工作流会再次校验和构建，并发布四个资产：

- `S3Explorer-vX.Y.Z-win-x64.zip`
- `S3Explorer-vX.Y.Z-win-x64-self-contained.zip`
- `release-metrics.json`
- `SHA256SUMS.txt`

Release 必须是正式版本，即 `draft=false`、`prerelease=false`。Release 工作流也可手工重跑，但输入必须是已经存在的 tag；工作流不会创建或移动 tag。

## 6. 等待并核验 Pages

推送 `main` 后，`Deploy project homepage` 会校验并部署 `docs/site/`。等待 Release 与 Pages 两个工作流都成功，再逐项检查：

- [Latest Release](https://github.com/project-base-mirror/tool-storage-browser/releases/latest) 指向新版本。
- 版本化的两个 ZIP 可下载，名称、大小和 Content-Type 正常。
- 下载 ZIP 后计算 SHA-256，与 `SHA256SUMS.txt` 完全一致。
- [项目主页](https://project-base-mirror.github.io/tool-storage-browser/) 显示新版本和正确下载地址。
- [`update.json`](https://project-base-mirror.github.io/tool-storage-browser/update.json) 的版本、tag、Release 页面与下载 URL 一致，客户端检查更新可读取。
- `robots.txt`、`sitemap.xml` 和 `assets/social-card.png` 均返回 HTTP 200。

只有以上检查全部通过，才可宣布“已推送并正式发布”。

## 7. 失败处理

- Pages 失败：修复 `main` 上的站点或清单并重新推送，让 Pages 重新部署。
- Release 工作流失败且 Release 尚未产生：保留 tag 作为审计记录，修复后发布下一个补丁版本；不要移动远端正式 tag。
- Release 资产上传阶段偶发失败且 tag 内容正确：可对同一 tag 手工重跑工作流，工作流会覆盖同名资产。
- Release 已发布但包有缺陷：撤下有问题的 Release 资产只用于阻止继续传播，同时立即修复并发布新补丁版本；不得用不同代码覆盖原 tag。
- 任一在线验证失败：明确记录失败项，不把本地构建成功描述为正式发布成功。
