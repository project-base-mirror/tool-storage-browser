# S3 Explorer

S3 Explorer 是一个面向 Windows 10/11 x64 的原生 S3 对象存储管理工具。它使用 C#、.NET 10、WinForms 和 AWS SDK for .NET 构建，不依赖浏览器、Electron、Node.js、WebView 或数据库。

[项目主页](https://project-base-mirror.github.io/tool-storage-browser/) · [下载最新版本](https://github.com/project-base-mirror/tool-storage-browser/releases/latest) · [功能路线图](docs/Roadmap-v0.5-v0.8.md) · [逐版本记录](docs/versions/README.md) · [正式发布流程](docs/Release-Process.md)

当前阶段聚焦 Amazon S3 与 S3-compatible object storage，不集成 rclone、WebDAV、FTP 或 SFTP。

## 已实现范围

- Amazon S3 与兼容服务连接配置、连接测试和 Bucket 浏览。
- Windows DPAPI CurrentUser 凭据保护。
- Amazon S3 显式凭据来源：已保存密钥、AWS shared profile、环境变量、容器角色、EC2 实例角色和 AWS SDK 默认链。
- Bucket 创建、空 Bucket 删除和对象分页浏览。
- 文件/文件夹上传、下载、复制、移动、重命名和批量删除。
- 大文件 multipart upload、传输队列、取消与失败状态。
- 对象属性、Metadata、预签名下载 URL。
- WinForms 主窗口、连接管理、设置、日志和错误详情。
- 当前列表过滤、导航历史、布局与列设置持久化。
- 文件夹单向镜像同步：保存任务、分析新增/更改/删除、排除规则、可选哈希比较，并将操作加入可恢复传输队列。
- 独立 `s3explorer-cli`：连接、Bucket、对象、同步、增量发布、远程验证和 CDN 自动化，支持稳定 JSON 输出、取消与自动化隔离数据目录。
- 简化的账户创建：Amazon S3、S3 兼容存储、Google Cloud Storage 三类入口，兼容服务使用模板；无须 Region 的服务自动隐藏该参数。
- 单个或全部连接导入导出：连同相关 CDN Profile、Bucket/前缀关联一起迁移；对象存储/CDN 分页选择，两类凭据分别确认；等价配置复用且重复导入不生成副本。
- 连接复制、健康状态、最近检查与最近成功时间。
- 独立 CDN / 内容分发配置：按连接、Bucket 和最长前缀映射交付域名，支持复制/打开 CDN URL、Range 下载测试、HTTPS 证书诊断、持久任务、HTTP 预热与通用刷新端点。
- 上传后 CDN 自动化：关联可分别设置新对象预热、覆盖后刷新或刷新后预热；任务独立重试、取消并在重启后恢复，不改变上传成功状态。
- GitHub Pages 项目主页、tag 驱动的 GitHub Release，以及可关闭的启动更新检查。

## 运行要求

推荐普通用户使用 MSI 安装包：

- 默认安装到 `C:\Program Files\S3 Explorer\`，可修改路径并选择桌面快捷方式；开始菜单入口默认创建。
- 推荐的 `setup.msi` 已包含 .NET 10 运行时；轻量的 `framework-dependent-setup.msi` 需要 .NET 10 Desktop Runtime x64。
- 两个 MSI 都以普通多文件方式安装 GUI、CLI、DLL 与配置文件。

需要免安装运行时可选择 portable ZIP：

- Windows 10 或 Windows 11 x64。
- framework-dependent ZIP 需要 .NET 10 Desktop Runtime x64。
- self-contained ZIP 自带所需运行时，体积更大。

两个便携 ZIP 中，桌面端和 CLI 都各自为单文件 EXE；安装包使用多文件布局。

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

MinIO/S3 集成测试是显式 opt-in，不会自动连接生产服务。未配置时测试会明确显示为 `Skipped`，不会计入通过数。开发机推荐使用一次性 Docker MinIO：

    & .\scripts\Test-LocalMinio.ps1

脚本自动启动隔离容器，执行真实上传、下载、复制、移动、Multipart 和 Bucket 管理测试，写入 `artifacts\local-minio\result.json`，最后只删除自己创建的容器。也可以同时传入 `-Endpoint`、`-AccessKey`、`-SecretKey` 使用现有测试实例。

只有同时设置以下环境变量时，直接运行测试项目才会访问远程测试实例：

    S3EXPLORER_TEST_ENDPOINT
    S3EXPLORER_TEST_ACCESS_KEY
    S3EXPLORER_TEST_SECRET_KEY

可选变量：`S3EXPLORER_TEST_REGION`。

测试账户应使用隔离的临时服务和最小权限凭据。

局域网 MinIO 环境、凭据获取、临时测试账户、真实 CRUD 测试和 UI 回归步骤见 [`docs/MinIO-Testing.md`](docs/MinIO-Testing.md)。

`0.2.1` 至 `0.3.4` 的版本边界、需求拆分、验收标准和实时完成情况见 [`docs/Release-Plan-v0.2-v0.3.md`](docs/Release-Plan-v0.2-v0.3.md)。

逐小版本的交付内容、兼容性、验证和已知限制见 [`docs/versions/`](docs/versions/README.md)。

基于 S3 Browser 功能对标的 `0.5` 至 `0.8` 详细路线见 [`docs/Roadmap-v0.5-v0.8.md`](docs/Roadmap-v0.5-v0.8.md)。

连接包的导出范围、迁移密码、导入预览和重名策略见 [`docs/Connection-Import-Export.md`](docs/Connection-Import-Export.md)。

CDN 配置、独立凭据、Bucket/前缀关联、下载探测、预热/刷新边界和后续厂商 Provider 计划见 [`docs/Cdn-Delivery-Integration.md`](docs/Cdn-Delivery-Integration.md)。

AWS shared credentials/config、环境变量与角色凭据的选择、诊断、安全边界和 CLI 参数见 [`docs/Aws-Credential-Sources.md`](docs/Aws-Credential-Sources.md)。

## 对象存储命令行 API

发布包同时包含 `S3Explorer.exe` 和 `s3explorer-cli.exe`。CLI 可脱离 Unity 独立运行，并与桌面端共享 DPAPI 加密的连接、CDN 配置和同步任务：

    s3explorer-cli profiles list --output json
    s3explorer-cli profile add --name "aws-audit" --type amazon --credential-source profile --aws-profile readonly
    s3explorer-cli connection test --profile "my-account" --output json
    s3explorer-cli bucket list --profile "my-account" --output json
    s3explorer-cli objects list --profile "my-account" --bucket "my-bucket" --prefix "path/" --recursive --output json
    s3explorer-cli object upload "D:\data\report.zip" "s3://my-account/my-bucket/backups/"
    s3explorer-cli object download "s3://my-account/my-bucket/backups/report.zip" "D:\restore\report.zip"

面向 Unity、CI 和构建机的发布命令只接收 Profile ID/名称，不接收或保存长期密钥：

    s3explorer-cli upload --profile "minio-dev" --source "D:\Build\Android" --bucket "game-survival" --prefix "android/1.2.3/" --transfers 4 --upload-limit 0 --verify --output json --non-interactive
    s3explorer-cli publish --profile "minio-dev" --source "D:\Build\Android" --bucket "game-survival" --prefix "android/1.2.3/" --project "game-survival" --product android --version 1.2.3 --access preserve --cdn-profile "cdn-dev" --warmup --output json --non-interactive --yes
    s3explorer-cli verify --manifest "D:\Build\Android\publish-manifest.json" --output json --non-interactive
    s3explorer-cli cdn test --profile "cdn-dev" --path "android/1.2.3/config.bytes" --output json --non-interactive
    s3explorer-cli cdn cache-test --profile "cdn-dev" --path "game-survival/android/1.2.3/config.bytes" --output json --non-interactive
    s3explorer-cli cdn warmup --profile "cdn-dev" --manifest "D:\Build\Android\publish-manifest.json" --output json --non-interactive

`publish` 会扫描本地产物并计算 SHA-256，与远程 Manifest 比较，只上传新增和变化文件；每个对象上传后都会下载验证 Size/SHA-256，最终 `publish-manifest.json` 仅在文件全部成功时最后上传。默认只支持 `--delete-mode none`，版本目录不会被自动删除。`--dry-run` 可只输出变更计划，`--full` 可忽略远程 Manifest 重新上传全部文件。

默认 `--access preserve` 不改变对象 ACL。CDN 使用匿名源站时可显式指定 `--access anonymous-read --yes`，它只设置当前 Manifest 对象与 Manifest 本身的对象级 `public-read`；不会修改 Bucket Policy 或 Public Access Block。`--access private --yes` 可恢复这些对象的私有 ACL。`cdn cache-test` 会对同一 URL 连续发送两次 HEAD 请求并分别输出缓存 Header。

同步任务可以由桌面端“工具 → 文件夹同步”创建，也可以完全通过命令行管理：

    s3explorer-cli sync list --output json
    s3explorer-cli sync add --name "site-backup" --local "D:\site" --remote "s3://my-account/backups/site/" --direction upload --exclude "bin/**" --exclude "*.tmp"
    s3explorer-cli sync analyze "site-backup" --output json
    s3explorer-cli sync run "site-backup" --output json

交互式终端会对发布、删除传播等操作显示 `[y/N]` 确认；Unity 和 CI 应使用 `--non-interactive --yes`，缺少确认时命令会直接失败而不是等待输入。`--timeout <秒>`、`--cancel-file <路径>` 和 `--log-file <路径>` 可控制超时、外部取消和追加脱敏日志。批量传输可用 `--transfers`、`--multipart-concurrency`、`--upload-limit`、`--download-limit`、`--multipart-threshold` 和 `--part-size` 设置有界并发、命令级共享限速与 Multipart；`upload`/`object upload` 可用 `--verify` 回读校验。创建保存密钥的连接时建议用 `--secret-key-env <变量名>` 或 `S3EXPLORER_SECRET_KEY`，避免密钥进入命令历史。Amazon S3 还可通过 `--credential-source profile|environment|container|instance|default` 锁定外部来源；S3-compatible 连接不会读取 AWS 默认链。

所有 CLI 命令都支持 `--data-dir <绝对路径>`，可让自动化使用隔离配置，不读取真实账户。`--output json` 提供结构化结果，旧的 `--json` 仍兼容；普通终端默认输出可读中文提示。成功返回 `0`，参数错误返回 `2`，目标不存在返回 `3`，远端或本地操作失败返回 `4`，取消返回 `130`。源码树中可用 `cli.bat help` 查看完整命令。

Unity 2021.3 可从每个正式 Release 下载独立的 `S3Explorer.Contracts-vX.Y.Z.zip`，只引用其中 `netstandard2.1` DTO，并通过 `Process` 调用同版本 CLI。Unity 项目只需保存 Profile ID、Bucket、Prefix 和 CDN Profile ID；AccessKey、SecretKey 与 SessionToken 继续由对象存储工具在 `%APPDATA%\S3Explorer` 下使用 DPAPI 保护。完整步骤见 [`docs/Unity-Publish-Integration.md`](docs/Unity-Publish-Integration.md)。

## UI 自动化

仓库提供固定命令集合，不接受任意可执行文件或脚本参数：

    pwsh .\scripts\AppAutomation.ps1 Help
    pwsh .\scripts\AppAutomation.ps1 Version
    pwsh .\scripts\AppAutomation.ps1 Start
    pwsh .\scripts\AppAutomation.ps1 Status
    pwsh .\scripts\AppAutomation.ps1 Stop
    pwsh .\scripts\AppAutomation.ps1 Smoke

也可以使用 `scripts\app-automation.cmd`。`Start` 会在需要时构建 Release 版本，启动应用并等待窗口及核心控件就绪；`Status` 会校验 PID、进程路径和启动时间，避免误认同 PID 的其他进程；`Stop` 只发送正常窗口关闭请求，不强制终止进程。

`Smoke` 使用 `artifacts\automation` 下的隔离数据目录，不读取或覆盖 `%APPDATA%\S3Explorer` 中的真实连接配置。它会验证主窗口、菜单、工具栏、地址栏、连接树、对象列表、传输队列、状态栏、CDN 命令注册和 `..` 上级目录行，并输出 JSON 报告和 PNG 截图。

## 发布

仓库根目录提供可直接双击的发布入口：

    publish.bat

它调用 `scripts\Publish.ps1`，成功后打印并打开实际发布目录；失败时保留错误窗口。

也可以直接运行 PowerShell 发布脚本：

    pwsh .\scripts\Publish.ps1

脚本默认执行 restore、全量测试和 Release 构建，然后生成：

    artifacts/release/S3Explorer-v0.6.8-win-x64.zip
    artifacts/release/S3Explorer-v0.6.8-win-x64-self-contained.zip
    artifacts/release/S3Explorer.Contracts-v0.6.8.zip
    artifacts/release/S3Explorer-v0.6.8-win-x64-setup.msi
    artifacts/release/S3Explorer-v0.6.8-win-x64-framework-dependent-setup.msi
    artifacts/release/release-metrics.json

构建和发布输出位置固定在仓库根目录的 `artifacts` 下，不接受重定向到其他目录。

两个便携 ZIP 保持单文件形式：framework-dependent ZIP 需要 .NET 10 Desktop Runtime，self-contained ZIP 可直接解压运行。安装器使用独立的多文件发布目录，不再把 DLL 集成到入口 EXE：默认 `setup.msi` 自带运行时，`framework-dependent-setup.msi` 依赖系统中的 .NET 10 Desktop Runtime。两个 MSI 都默认安装到 `C:\Program Files\S3 Explorer\`，提供路径选择、开始菜单入口和桌面快捷方式选项。安装失败时可在 `%TEMP%` 查找最新的 `MSI*.log`。

仅重新打包而跳过验证：

    publish.bat -SkipValidation

自动化环境中不打开资源管理器：

    publish.bat -NoOpen

在交互式 Windows 桌面上额外记录启动时间和空闲 Working Set：

    pwsh .\scripts\Publish.ps1 -MeasureRuntime

`release-metrics.json` 会记录两个应用发布目录的压缩前大小、应用 ZIP、Unity Contracts SDK ZIP、两个 MSI 的大小和 SHA-256、安装器多文件负载统计，以及 .NET SDK 版本、签名配置状态和可选的启动时间与内存数据。

推送与项目版本一致的 `vX.Y.Z` tag 后，GitHub Actions 会执行相同验证并创建 GitHub Release；版本、更新清单、Release 与 Pages 的固定同步步骤见 [`docs/Release-Process.md`](docs/Release-Process.md)，GitHub 交付机制见 [`docs/GitHub-Delivery.md`](docs/GitHub-Delivery.md)。

发布脚本回归检查：

    pwsh .\scripts\Test-Publish.ps1

已有发布包时只检查入口、固定路径、批处理失败保留行为和产物结构：

    pwsh .\scripts\Test-Publish.ps1 -SkipPackageBuild

## 数据位置与安全

- 连接配置：`%APPDATA%\S3Explorer\profiles.json`
- 文件夹同步任务：`%APPDATA%\S3Explorer\sync-jobs.json`
- CDN 非敏感配置：`%APPDATA%\S3Explorer\cdn-config.json`
- CDN 独立凭据：`%APPDATA%\S3Explorer\cdn-credentials.json`
- 日志目录：`%LOCALAPPDATA%\S3Explorer\logs`
- SecretKey 和 SessionToken 使用 DPAPI CurrentUser 加密后保存。
- AWS Profile 连接只保存非敏感 Profile 名称；环境、容器和实例角色凭据不写入 `profiles.json`、连接包或日志。
- CDN Secret 使用独立 DPAPI entropy 加密，不复用 S3 SecretKey，也不写入普通 CDN 配置文件。
- 导出配置默认不包含 S3 或 CDN 秘密值；显式选择后使用迁移密码重新加密，不复制本机 DPAPI 密文。
- 日志不得记录 SecretKey、SessionToken、Authorization Header 或完整预签名 URL。
- 忽略证书错误仅用于用户明确配置的测试环境。

## 项目结构

    src/
      S3Explorer.App                 WinForms 桌面应用
      S3Explorer.Cli                 独立命令行、发布与 CDN 自动化 API
      S3Explorer.Contracts           Unity/CLI 共享的 netstandard2.1 发布 DTO
      S3Explorer.Core                核心模型、路径和接口
      S3Explorer.Infrastructure.Cdn  CDN 配置存储与通用 HTTP 交付服务
      S3Explorer.Infrastructure.S3   AWS SDK、凭据与配置实现
    tests/
      S3Explorer.Cli.Tests
      S3Explorer.Core.Tests
      S3Explorer.Infrastructure.Cdn.Tests
      S3Explorer.Infrastructure.S3.Tests
      S3Explorer.App.Tests
    docs/
      MinIO-Testing.md
      Release-Process.md
      GitHub-Delivery.md
      Cdn-Delivery-Integration.md
      Aws-Credential-Sources.md
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

当前仍未实现生命周期、Object Lock、应用内静默升级和托盘驻留。未支持的入口保持禁用并明确提示当前版本不支持。

CDN 当前提供通用 HTTP 交付域名、CLI 探测/预热、无需厂商签名的刷新端点、持久作业队列和显式开启的上传后自动化；尚未实现 CloudFront/Cloudflare/阿里云/腾讯云签名 API 或 Prefix Purge。

文件夹同步当前是本地文件夹与 S3 路径之间的单向镜像。分析结果可筛选、排序、逐项勾选并从右键添加文件/扩展名/目录排除规则；分析快照会在任务参数变化或 15 分钟后失效。执行记录支持脱敏 JSON/CSV 导出和失败项重新入队。默认比较大小与修改时间；启用哈希比较后，仅对可作为 MD5 的单段 ETag 做内容比较，Multipart ETag 会回退到大小与时间。同步不会跟随本地重解析点；删除传播默认关闭且执行前必须确认。

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
