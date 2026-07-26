# S3 Explorer

S3 Explorer 是一个面向 Windows 10/11 x64 的原生 S3 对象存储管理工具。它使用 C#、.NET 10、WinForms 和 AWS SDK for .NET 构建，不依赖浏览器、Electron、Node.js、WebView 或数据库。

[项目主页](https://project-base-mirror.github.io/tool-storage-browser/) · [下载最新版本](https://github.com/project-base-mirror/tool-storage-browser/releases/latest) · [功能路线图](docs/Roadmap-v0.5-v0.8.md) · [逐版本记录](docs/versions/README.md)

当前阶段聚焦 Amazon S3 与 S3-compatible object storage，不集成 rclone、WebDAV、FTP 或 SFTP。

## 已实现范围

- Amazon S3 与兼容服务连接配置、连接测试和 Bucket 浏览。
- Windows DPAPI CurrentUser 凭据保护。
- Bucket 创建、空 Bucket 删除和对象分页浏览。
- 文件/文件夹上传、下载、复制、移动、重命名和批量删除。
- 大文件 multipart upload、传输队列、取消与失败状态。
- 对象属性、Metadata、预签名下载 URL。
- WinForms 主窗口、连接管理、设置、日志和错误详情。
- 当前列表过滤、导航历史、布局与列设置持久化。
- 文件夹单向镜像同步：保存任务、分析新增/更改/删除、排除规则、可选哈希比较，并将操作加入可恢复传输队列。
- 独立 `s3explorer-cli`：连接、Bucket、对象和同步任务 API，支持 JSON 输出与自动化隔离数据目录。
- 简化的账户创建：Amazon S3、S3 兼容存储、Google Cloud Storage 三类入口，兼容服务使用模板；无须 Region 的服务自动隐藏该参数。

## 运行要求

推荐使用 framework-dependent 包：

- Windows 10 或 Windows 11 x64。
- .NET 10 Desktop Runtime x64。

备用 self-contained 包自带所需运行时，不要求预装 .NET Desktop Runtime，但体积更大。

## 从源码构建

需要安装 .NET 10 SDK。

Windows 上最容易发现的入口位于仓库根目录。双击或在命令行运行：

    build.bat

构建产物固定写入：

    artifacts/build/

构建成功后会打印并打开实际产物目录。构建失败时批处理窗口会保留，便于查看完整错误。

也可以直接使用 `dotnet`：

    dotnet restore .\S3Explorer.sln
    dotnet build .\S3Explorer.sln -c Release --no-restore

应用项目：`src/S3Explorer.App/S3Explorer.App.csproj`

## 测试

运行全部单元测试和默认测试：

    dotnet test .\S3Explorer.sln -c Release --no-restore

MinIO/S3 集成测试是显式 opt-in，不会自动连接生产服务。只有同时设置以下环境变量时才访问远程测试实例：

    S3EXPLORER_TEST_ENDPOINT
    S3EXPLORER_TEST_ACCESS_KEY
    S3EXPLORER_TEST_SECRET_KEY

可选变量：`S3EXPLORER_TEST_REGION`。

测试账户应使用隔离的临时服务和最小权限凭据。

局域网 MinIO 环境、凭据获取、临时测试账户、真实 CRUD 测试和 UI 回归步骤见 [`docs/MinIO-Testing.md`](docs/MinIO-Testing.md)。

`0.2.1` 至 `0.3.4` 的版本边界、需求拆分、验收标准和实时完成情况见 [`docs/Release-Plan-v0.2-v0.3.md`](docs/Release-Plan-v0.2-v0.3.md)。

逐小版本的交付内容、兼容性、验证和已知限制见 [`docs/versions/`](docs/versions/README.md)。

基于 S3 Browser 功能对标的 `0.5` 至 `0.8` 详细路线见 [`docs/Roadmap-v0.5-v0.8.md`](docs/Roadmap-v0.5-v0.8.md)。

## 对象存储命令行 API

发布包同时包含 `S3Explorer.exe` 和 `s3explorer-cli.exe`。CLI 与桌面端共享 DPAPI 加密的连接配置和同步任务：

    s3explorer-cli profile list --json
    s3explorer-cli connection test "my-account" --json
    s3explorer-cli bucket list "my-account" --json
    s3explorer-cli object list "s3://my-account/my-bucket/path/" --recursive --json
    s3explorer-cli object upload "D:\data\report.zip" "s3://my-account/my-bucket/backups/"
    s3explorer-cli object download "s3://my-account/my-bucket/backups/report.zip" "D:\restore\report.zip"

同步任务可以由桌面端“工具 → 文件夹同步”创建，也可以完全通过命令行管理：

    s3explorer-cli sync list --json
    s3explorer-cli sync add --name "site-backup" --local "D:\site" --remote "s3://my-account/backups/site/" --direction upload --exclude "bin/**" --exclude "*.tmp"
    s3explorer-cli sync analyze "site-backup" --json
    s3explorer-cli sync run "site-backup" --json

如果同步任务启用了删除传播，`sync run` 还必须明确提供 `--yes`。创建连接时建议用 `--secret-key-env <变量名>` 或 `S3EXPLORER_SECRET_KEY`，避免密钥进入命令历史。源码树中可用 `cli.bat help` 查看完整命令。

所有 CLI 命令都支持 `--data-dir <绝对路径>`，可让自动化使用隔离配置，不读取真实账户。成功返回 `0`，参数错误返回 `2`，目标不存在返回 `3`，远端或本地操作失败返回 `4`，取消返回 `130`。

## UI 自动化

仓库提供固定命令集合，不接受任意可执行文件或脚本参数：

    pwsh .\scripts\AppAutomation.ps1 Help
    pwsh .\scripts\AppAutomation.ps1 Version
    pwsh .\scripts\AppAutomation.ps1 Start
    pwsh .\scripts\AppAutomation.ps1 Status
    pwsh .\scripts\AppAutomation.ps1 Stop
    pwsh .\scripts\AppAutomation.ps1 Smoke

也可以使用 `scripts\app-automation.cmd`。`Start` 会在需要时构建 Release 版本，启动应用并等待窗口及核心控件就绪；`Status` 会校验 PID、进程路径和启动时间，避免误认同 PID 的其他进程；`Stop` 只发送正常窗口关闭请求，不强制终止进程。

`Smoke` 使用 `artifacts\automation` 下的隔离数据目录，不读取或覆盖 `%APPDATA%\S3Explorer` 中的真实连接配置。它会验证主窗口、菜单、工具栏、地址栏、连接树、对象列表、传输队列、状态栏和 `..` 上级目录行，并输出 JSON 报告和 PNG 截图。

## 发布

仓库根目录提供可直接双击的发布入口：

    publish.bat

它调用 `scripts\Publish.ps1`，成功后打印并打开实际发布目录；失败时保留错误窗口。

也可以直接运行 PowerShell 发布脚本：

    pwsh .\scripts\Publish.ps1

脚本默认执行 restore、全量测试和 Release 构建，然后生成：

    artifacts/release/S3Explorer-win-x64.zip
    artifacts/release/S3Explorer-win-x64-self-contained.zip
    artifacts/release/release-metrics.json

构建和发布输出位置固定在仓库根目录的 `artifacts` 下，不接受重定向到其他目录。

发布配置明确关闭 trimming 和单文件打包，避免 AWS SDK、JSON 序列化或 WinForms 反射类型被误删。

仅重新打包而跳过验证：

    publish.bat -SkipValidation

自动化环境中不打开资源管理器：

    publish.bat -NoOpen

在交互式 Windows 桌面上额外记录启动时间和空闲 Working Set：

    pwsh .\scripts\Publish.ps1 -MeasureRuntime

`release-metrics.json` 会记录两个发布目录的压缩前大小、ZIP 大小、ZIP SHA-256、SDK 版本，以及可选的启动时间和内存数据。

推送与项目版本一致的 `vX.Y.Z` tag 后，GitHub Actions 会执行相同验证并创建 GitHub Release；Pages 与 Release 的首次启用、版本门禁和资产名称约定见 [`docs/GitHub-Delivery.md`](docs/GitHub-Delivery.md)。

发布脚本回归检查：

    pwsh .\scripts\Test-Publish.ps1

已有发布包时只检查入口、固定路径、批处理失败保留行为和产物结构：

    pwsh .\scripts\Test-Publish.ps1 -SkipPackageBuild

## 数据位置与安全

- 连接配置：`%APPDATA%\S3Explorer\profiles.json`
- 文件夹同步任务：`%APPDATA%\S3Explorer\sync-jobs.json`
- 日志目录：`%LOCALAPPDATA%\S3Explorer\logs`
- SecretKey 和 SessionToken 使用 DPAPI CurrentUser 加密后保存。
- 导出配置默认不包含凭据。
- 日志不得记录 SecretKey、SessionToken、Authorization Header 或完整预签名 URL。
- 忽略证书错误仅用于用户明确配置的测试环境。

## 项目结构

    src/
      S3Explorer.App                 WinForms 桌面应用
      S3Explorer.Cli                 命令行对象存储 API
      S3Explorer.Core                核心模型、路径和接口
      S3Explorer.Infrastructure.S3   AWS SDK、凭据与配置实现
    tests/
      S3Explorer.Core.Tests
      S3Explorer.Infrastructure.S3.Tests
      S3Explorer.App.Tests
    docs/
      MinIO-Testing.md
      GitHub-Delivery.md
      Release-Plan-v0.2-v0.3.md
      Roadmap-v0.5-v0.8.md
      versions/
    scripts/
      Build.ps1
      Publish.ps1
      Test-Publish.ps1
      AppAutomation.ps1
      app-automation.cmd
    build.bat
    publish.bat
    cli.bat

## 当前限制

当前仍未实现 CORS、生命周期、版本历史、Object Lock、自动更新和托盘驻留。未支持的入口保持禁用并明确提示当前版本不支持。

文件夹同步当前是本地文件夹与 S3 路径之间的单向镜像。默认比较大小与修改时间；启用哈希比较后，仅对可作为 MD5 的单段 ETag 做内容比较，Multipart ETag 会回退到大小与时间。同步不会跟随本地重解析点；删除传播默认关闭且执行前必须确认。

对象“目录”由 `Delimiter = "/"`、`CommonPrefixes` 和以 `/` 结尾的零字节对象模拟；S3 本身没有本地文件系统意义上的真实目录。

## 发布检查清单

1. `dotnet restore` 成功。
2. 全量 `dotnet test` 成功。
3. Release `dotnet build` 成功。
4. `scripts\Test-Publish.ps1` 成功。
5. 两种发布目录和 ZIP 均生成。
6. 检查 `release-metrics.json` 中的体积和哈希。
7. 在发布机上启动 framework-dependent 包，验证连接窗口、对象列表和 WinForms 控件。
8. 需要性能记录时，在交互式桌面使用 `-MeasureRuntime`。
