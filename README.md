# S3 Explorer

S3 Explorer 是一个面向 Windows 10/11 x64 的原生 S3 对象存储管理工具。它使用 C#、.NET 10、WinForms 和 AWS SDK for .NET 构建，不依赖浏览器、Electron、Node.js、WebView 或数据库。

[项目主页](https://project-base-mirror.github.io/tool-storage-browser/) · [下载最新版本](https://github.com/project-base-mirror/tool-storage-browser/releases/latest) · [文档导航](docs/README.md) · [未发布变更](docs/versions/next.md) · [逐版本记录](docs/versions/README.md) · [功能路线图](docs/Roadmap-v0.5-v0.8.md)

当前阶段聚焦 Amazon S3 与 S3-compatible object storage，不集成 rclone、WebDAV、FTP 或 SFTP。

## 已实现范围

- Amazon S3 与兼容服务连接配置、连接测试和 Bucket 浏览。
- Windows DPAPI CurrentUser 凭据保护。
- Amazon S3 显式凭据来源：已保存密钥、AWS shared profile、环境变量、容器角色、EC2 实例角色、AWS SDK 默认链、IAM Identity Center (SSO)、AssumeRole 和 Web Identity。
- 账户分组、折叠与拖拽排序；删除分组只会把连接移到未分组，导入预览可指定目标分组。
- Bucket 创建、空 Bucket 删除和对象分页浏览。
- 文件/文件夹上传、下载、复制、移动、重命名和批量删除。复制支持不同连接、凭据和服务商：在源连接复制后切换目标连接粘贴，或在“复制到...”中选择目标连接。
- 大文件 multipart upload、传输队列、取消与失败状态。
- 上传和下载仅在目标实际冲突时询问覆盖、跳过或重命名；失败任务释放目标，重新尝试时重新检查队列占用。
- 对象属性、Header/Metadata、Object Tags 和预签名下载 URL。AWS、MinIO、R2、标准 Custom 及 OSS/COS/B2/GCS/Supabase 可用状态下支持原地/批量 Metadata 编辑，保存前重验冲突并说明非原子窗口；符合条件的 AWS 固定版本仍可另存为新对象。
- WinForms 主窗口、连接管理、设置、日志和错误详情。
- 当前列表过滤、导航历史、布局与列设置持久化。
- 单实例托盘驻留：重复启动只恢复已有窗口；关闭或最小化主窗口后继续传输，显式退出仍安全暂停并保存队列。驻留动作本身不弹系统气泡，完成与失败结果使用无焦点应用通知合并显示，避免 Windows 隐私占位和批量任务刷屏。
- 文件夹单向镜像同步：保存任务、分析新增/更改/删除、排除规则、可选哈希比较，并将操作加入可恢复传输队列。
- 独立 `s3explorer-cli`：连接、Bucket、对象、同步、增量发布、远程验证和 CDN 自动化，支持稳定 JSON 输出、取消与自动化隔离数据目录。
- 简化的账户创建：Amazon S3、Cloudflare R2、S3 兼容存储、Google Cloud Storage 四类入口，服务商模板随账户类型切换；切换保留已填写的自定义 Endpoint，无须 Region 的服务自动隐藏该参数。
- 统一 Credential Vault：对象存储、AWS External ID 与 CDN 控制面凭据共享安全引用；CDN 内容认证内联在对应 Profile 中。连接包 v6 安全迁移两类秘密与非秘密控制资源 ID，等价配置复用且重复导入不生成副本。
- 连接复制、健康状态、最近检查与最近成功时间。
- 独立 CDN / 内容分发配置：按连接、Bucket 和最长前缀映射交付域名，支持复制/打开 CDN URL、Range 下载测试、HTTPS 证书诊断、持久任务、通用 HTTP，以及阿里云、腾讯云和 Cloudflare 原生控制面。
- 上传后 CDN 自动化：关联可分别设置新对象预热、覆盖后刷新或刷新后预热；任务独立重试、取消并在重启后恢复，不改变上传成功状态。
- GitHub Pages 项目主页、tag 驱动的 GitHub Release、可关闭的启动更新检查，以及安装版经 SHA-256 复验后的用户确认静默升级。

## 本轮安全修复的行为变化

修复记录和验证边界见 [2026-11-04 审计修复](docs/Audit-Fixes-2026-11-04.md)、[后续修复](docs/Audit-Followups-2026-11-04.md)及[安全工作流继续修复](docs/Safe-Workflows-2026-11-04.md)，不代表已发布新版本。

- 同连接移动与重命名已恢复：先复制，再核对当前源身份，AWS 优先使用 ETag 条件删除。其他兼容服务会在界面确认并发风险后普通删除；检查与删除之间仍有竞争窗口，操作期间应停止源和目标的并发写入。移动不是两对象事务或原子重命名，属性变化不受 ETag 完整保护。
- 普通非覆盖上传优先使用已支持的服务端条件写入。其他兼容服务使用上传前存在性检查，分片上传在完成前再次检查，不再因缺少原子条件写入而禁止上传；检查与写入之间仍可能有并发窗口，不保证原子非覆盖。按原 ETag 保存编辑仍要求可靠条件写入。
- 跨连接复制经本机读取源对象并上传目标，源对象保留，支持目录层级、空对象、Metadata、限速、取消和分片断点续传；标签仅在两端支持时保留。不支持原子条件写入的目标沿用普通上传的存在性检查，因此仍有并发窗口。同桶的 AWS 区域端点别名会共享自复制检查，自定义 Host 不同的连接不会误判为同一存储。
- 同连接非覆盖复制优先使用服务端目标条件；其他兼容服务恢复为写入前检查目标，分片复制在完成前再次检查，不保证并发时原子非覆盖。按指定 ETag 替换仍要求可靠条件。MinIO 空对象及禁用服务端分片复制的对象可使用条件 GET/PUT 中转。R2 ≤5 GiB 使用专用目标条件 Header，源与目标检查不是两对象事务。
- 普通原地/批量 Metadata 编辑按服务商能力检查对象后执行，OSS/COS 使用原生属性保留协议，GCS 使用 generation/metageneration 条件，B2/Supabase 增加写后回读；Supabase 明确提示并发内容覆盖风险。受保护或无法无损往返的具体状态仍停止，不按服务商整类禁用。详见 [专用服务商扩展与真实限制](docs/Metadata-Provider-Expansion-2026-11-07.md) 和 [编辑与 CDN 修复边界](docs/Editing-and-Cdn-Fixes-2026-11-07.md)。
- 文本编辑保留 Metadata/Tag 原始空白及 Content-Language/网站重定向，支持 gzip、br、deflate 有界解码与回传。普通兼容服务可在明确确认后使用写前检查回传；只有 AWS/MinIO/R2 使用已支持的条件写。受保护对象与无法完整读取的属性继续只读。
- CDN 映射保留 Key 的前导斜杠、反斜杠、重复斜杠及空白，按原始前缀匹配。浏览器会折叠的 . / .. 路径段明确拒绝生成链接，不会改指向其他对象。
- Metadata 另存会保留原对象且要求目标不存在。目前限定 AWS 固定版本、SSE-S3、≤5 GiB、BucketOwnerEnforced 且无 Object Lock；不支持的安全状态仍只读。另存不等同原地修改。
- 普通 Bucket 的前导斜杠 Key 恢复 GET/HEAD 预签名下载及经过身份校验的重定向。浏览器会规范化的点路径段等特殊模式仍明确拒绝生成链接，可使用应用内下载。
- R2 / Supabase 超过单次复制上限或服务端要求分片时，自动使用按 ETag 校验的分片 GET + 上传中转，支持限速和断点续传，不再依赖不可靠的 UploadPartCopy 条件。会产生下载、上传流量，中转分片的目标检查不保证原子非覆盖；旧服务端复制断点需先取消清理，无法读取完整 Metadata 的对象仍需单独处理。
- MinIO 仅支持 private ACL；公开访问应使用 Bucket Policy。递归 CLI 下载会预留不冲突的本地文件名，且提交时不覆盖并发出现的文件。

跨连接任务使用任务存储 v2，继续兼容读取 v1；旧版不能恢复含跨连接任务的新队列。降级前请先完成或清理跨连接任务。连接及凭据格式不变。

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

在 Visual Studio 中把 `S3Explorer.App` 设为启动项目后直接按 F5 即可验证桌面界面。Debug 构建或附加调试器启动时会显示 `[Debug]` 标记，并使用独立的单实例键和 `%APPDATA%\S3Explorer.Debug` 数据目录；它可以与已经驻留托盘的正式安装版同时运行。每次 Debug 启动会把正式版的统一加密配置刷新到隔离目录，因此正式版已经保存的连接、凭据和 CDN 配置可直接用于测试；Debug 的修改不会回写正式配置。Release 发布包仍使用 `%APPDATA%\S3Explorer` 和正式单实例键。

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

详细文档统一从 [`docs/README.md`](docs/README.md) 进入，并按凭据与迁移、兼容性与测试、发布与自动化、交付与版本分类维护。

- 已合入 `main` 但尚未进入正式 tag 的变化见 [`docs/versions/next.md`](docs/versions/next.md)。
- 已发布版本的交付内容、兼容性、验证和已知限制见 [`docs/versions/`](docs/versions/README.md)。
- `v0.5`–`v0.8` 的后续规划与明确延后项见 [`docs/Roadmap-v0.5-v0.8.md`](docs/Roadmap-v0.5-v0.8.md)。

## 对象存储命令行 API

发布包同时包含 `S3Explorer.exe` 和 `s3explorer-cli.exe`。CLI 可脱离 Unity 独立运行，并与桌面端共享 DPAPI 加密的连接、CDN 配置和同步任务：

    s3explorer-cli profiles list --output json
    s3explorer-cli profile add --name "aws-audit" --type amazon --credential-source profile --aws-profile readonly
    s3explorer-cli profile add --name "aws-sso" --type amazon --credential-source sso --aws-profile company-readonly --group "Production"
    s3explorer-cli profile add --name "aws-role" --type amazon --credential-source assume-role --source-profile bootstrap --role-arn "arn:aws:iam::123456789012:role/Audit" --role-session-name s3explorer-audit --external-id-env AWS_AUDIT_EXTERNAL_ID
    s3explorer-cli profile groups
    s3explorer-cli profile move "aws-audit" --group "Production"
    s3explorer-cli connection test --profile "my-account" --output json
    s3explorer-cli bucket list --profile "my-account" --output json
    s3explorer-cli objects list --profile "my-account" --bucket "my-bucket" --prefix "path/" --recursive --output json
    s3explorer-cli object upload "D:\data\report.zip" "s3://my-account/my-bucket/backups/"
    s3explorer-cli object download "s3://my-account/my-bucket/backups/report.zip" "D:\restore\report.zip"

面向 Unity、CI 和构建机的发布命令只接收 Profile ID/名称，不接收或保存长期密钥：

    s3explorer-cli upload --profile "minio-dev" --source "D:\Build\Android" --bucket "game-survival" --prefix "android/1.2.3/" --transfers 4 --upload-limit 0 --verify --output json --non-interactive
    s3explorer-cli publish --profile "minio-dev" --source "D:\Build\Android" --bucket "game-survival" --prefix "android/1.2.3/" --project "game-survival" --product android --version 1.2.3 --header-rules "D:\Build\publish-headers.json" --delete-mode mirror --access preserve --cdn-profile "cdn-dev" --warmup --output json --non-interactive --yes
    s3explorer-cli verify --manifest "D:\Build\Android\publish-manifest.json" --output json --non-interactive
    s3explorer-cli cdn test --profile "cdn-dev" --path "android/1.2.3/config.bytes" --output json --non-interactive
    s3explorer-cli cdn cache-test --profile "cdn-dev" --path "game-survival/android/1.2.3/config.bytes" --output json --non-interactive
    s3explorer-cli cdn warmup --profile "cdn-dev" --manifest "D:\Build\Android\publish-manifest.json" --output json --non-interactive

`publish` 会扫描本地产物并计算 SHA-256，与远程 Manifest 比较，只上传新增和变化文件；每个对象上传后都会下载验证 Size/SHA-256，最终 `publish-manifest.json` 仅在文件全部成功时上传并回读校验。可选 `--header-rules <json-file>` 按默认值和 glob 规则设置 Content-Type、Cache-Control、Content-Encoding、Content-Disposition、Expires、自定义 Metadata 与 Tags；最终生效值写入 Manifest Schema 2，因此文件内容未变但 Header 规则变化时也会重新上传。默认 `--delete-mode none` 保留所有远端对象；显式 `--delete-mode mirror` 会实时扫描非空目标 Prefix，在全部资源上传、回读验证和 ACL 操作成功后先发布并校验新 Manifest，再删除本地不存在的远端孤儿对象。这样即使 mirror 清理失败或中断，最多残留孤儿对象，新 Manifest 也不会引用已删除对象；清理失败仍会让命令返回失败。Prefix 外对象、目录标记和 `publish-manifest.json` 不会进入删除计划。`--dry-run` 同时输出上传与删除计划，`--full` 可忽略远程 Manifest 重新上传全部文件。

默认 `--access preserve` 不改变对象 ACL。CDN 使用匿名源站时可显式指定 `--access anonymous-read --yes`，它只设置当前 Manifest 对象与 Manifest 本身的对象级 `public-read`；不会修改 Bucket Policy 或 Public Access Block。`--access private --yes` 可恢复这些对象的私有 ACL。`cdn cache-test` 会对同一 URL 连续发送两次 HEAD 请求并分别输出缓存 Header。

同步任务可以由桌面端“工具 → 文件夹同步”创建，也可以完全通过命令行管理：

    s3explorer-cli sync list --output json
    s3explorer-cli sync add --name "site-backup" --local "D:\site" --remote "s3://my-account/backups/site/" --direction upload --exclude "bin/**" --exclude "*.tmp"
    s3explorer-cli sync analyze "site-backup" --output json
    s3explorer-cli sync run "site-backup" --output json

交互式终端会对发布、删除传播等操作显示 `[y/N]` 确认；Unity 和 CI 应使用 `--non-interactive --yes`，缺少确认时命令会直接失败而不是等待输入。`--timeout <秒>`、`--cancel-file <路径>` 和 `--log-file <路径>` 可控制超时、外部取消和追加脱敏日志。批量传输可用 `--transfers`、`--multipart-concurrency`、`--upload-limit`、`--download-limit`、`--multipart-threshold` 和 `--part-size` 设置有界并发、命令级共享限速与 Multipart；`upload`/`object upload` 可用 `--verify` 回读校验。创建统一凭据时使用 `credential add --secret-env <变量名>`，避免密钥进入命令历史。Amazon S3 还可通过 `--credential-source profile|environment|container|instance|default|sso|assume-role|web-identity` 锁定外部来源；AssumeRole External ID 建议通过 `--external-id-credential` 提供。S3-compatible 连接不会读取 AWS 默认链。

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
    pwsh .\scripts\AppAutomation.ps1 CorruptSmoke
    pwsh .\scripts\AppAutomation.ps1 SingleInstanceSmoke

也可以使用 `scripts\app-automation.cmd`。`Start` 会在需要时构建 Release 版本，启动应用并等待窗口及核心控件就绪；`Status` 会校验 PID、进程路径和启动时间，避免误认同 PID 的其他进程；`Stop` 只发送正常窗口关闭请求，不强制终止进程。`CorruptSmoke` 会在隔离数据目录中预置损坏存储，验证应用仍能打开主窗口并保留损坏证据。

`Smoke` 使用 `artifacts\automation` 下的隔离数据目录，不读取或覆盖 `%APPDATA%\S3Explorer` 中的真实连接配置。它会验证主窗口、菜单、工具栏、地址栏、连接树、对象列表、传输队列、状态栏、CDN 命令注册和 `..` 上级目录行，并输出 JSON 报告和 PNG 截图。`SingleInstanceSmoke` 使用独立实例键连续启动三次，要求两个后续进程立即退出、首个 PID 保持不变，并验证隐藏的首个窗口会被后续启动重新显示。

## 发布

仓库根目录提供可直接双击的发布入口：

    publish.bat

它调用 `scripts\Publish.ps1`，成功后打印并打开实际发布目录；失败时保留错误窗口。

也可以直接运行 PowerShell 发布脚本：

    pwsh .\scripts\Publish.ps1

脚本默认执行 restore、全量测试和 Release 构建，然后生成：

    artifacts/release/S3Explorer-v0.8.11-win-x64.zip
    artifacts/release/S3Explorer-v0.8.11-win-x64-self-contained.zip
    artifacts/release/S3Explorer.Contracts-v0.8.11.zip
    artifacts/release/S3Explorer-v0.8.11-win-x64-setup.msi
    artifacts/release/S3Explorer-v0.8.11-win-x64-framework-dependent-setup.msi
    artifacts/release/release-metrics.json

构建和发布输出位置固定在仓库根目录的 `artifacts` 下，不接受重定向到其他目录。

两个便携 ZIP 保持单文件形式：framework-dependent ZIP 需要 .NET 10 Desktop Runtime，self-contained ZIP 可直接解压运行。安装器使用独立的多文件发布目录，不再把 DLL 集成到入口 EXE：默认 `setup.msi` 自带运行时，`framework-dependent-setup.msi` 依赖系统中的 .NET 10 Desktop Runtime。两个 MSI 都默认安装到 `C:\Program Files\S3 Explorer\`，提供路径选择、开始菜单入口和桌面快捷方式选项，并携带独立单文件维护程序。安装版可以后台下载匹配 MSI，按 Release `SHA256SUMS.txt` 校验，用户确认后安全退出、静默安装并重新启动；Windows UAC 仍会正常显示。安装日志保存在 `%LOCALAPPDATA%\S3Explorer\updates\`。

仅重新打包而跳过验证：

    publish.bat -SkipValidation

自动化环境中不打开资源管理器：

    publish.bat -NoOpen

在交互式 Windows 桌面上额外记录启动时间和空闲 Working Set：

    pwsh .\scripts\Publish.ps1 -MeasureRuntime

`release-metrics.json` 会记录两个应用发布目录的压缩前大小、应用 ZIP、Unity Contracts SDK ZIP、两个 MSI 与维护程序的大小和 SHA-256、安装器多文件负载统计，以及 .NET SDK 版本、签名配置状态和可选的启动时间与内存数据。

推送与项目版本一致的 `vX.Y.Z` tag 后，GitHub Actions 会执行相同验证并创建 GitHub Release；版本、更新清单、Release 与 Pages 的固定同步步骤见 [`docs/Release-Process.md`](docs/Release-Process.md)，GitHub 交付机制见 [`docs/GitHub-Delivery.md`](docs/GitHub-Delivery.md)。

发布脚本回归检查：

    pwsh .\scripts\Test-Publish.ps1

已有发布包时只检查入口、固定路径、批处理失败保留行为和产物结构：

    pwsh .\scripts\Test-Publish.ps1 -SkipPackageBuild

## 账户分组与排序

账户树上方的 **新建分组** 按钮、`Accounts` 根节点右键和树中空白处右键都可新建分组。原有的“文件 → 新建连接分组”仍可使用。选中分组后新建的连接会直接进入该分组。

- **移动账户**：拖到分组名称上会放到该组末尾；拖到 `Accounts` 或树中空白处会移到未分组末尾。也可右键账户使用“移动到分组”。
- **调整账户顺序**：拖到另一个账户行的上半部或下半部，分别插到它之前或之后，也支持跨组。拖动时目标行会高亮，底部状态栏显示具体放置位置。
- **调整分组顺序**：拖到另一个分组行的上半部或下半部；分组统一显示在未分组账户之前，不支持嵌套分组。
- **逐项调整**：选中账户或分组后，点击账户树上方的 ↑ / ↓，或使用右键“上移 / 下移”。到达首尾时对应按钮禁用。
- 分组右键支持重命名、删除。删除分组会保留其中所有连接，并移到未分组。

分组、顺序和分组展开状态会自动保存。排序及移动会保留已加载的 Bucket 节点与当前对象列表；Bucket 本身不能拖拽排序。这些操作只改变本地组织方式，不会移动远端对象。现有旧账户和配置继续兼容，无需手工迁移。

验证账户树交互时请使用隔离的自动化数据目录（见上方 GUI 自动化章节），不要用会从正式配置复制数据的普通 Debug 启动。相关回归测试使用临时文件、假凭据和假存储服务：`ConnectionProfileConfigurationTests`、`ProfileStoreTests` 和 `MainFormInteractionTests`（界面测试需在 Windows 运行）。

## S3 上传签名与 Cloudflare R2

上传签名可以在连接配置中独立选择，不依赖服务类型或 Endpoint 域名猜测。支持三个模式：

- `Auto`（默认）：保留原来的兼容策略。`CloudflareR2` 使用非流式 `UNSIGNED-PAYLOAD`，AWS、MinIO 与其他类型保持 AWS SDK 默认签名和校验。旧配置缺少该字段时仍为 `Auto`。
- `Streaming`：显式使用 SDK 默认上传签名与校验，包含 R2 类型。R2 目前不支持 Streaming SigV4，通常不应为 R2 选择此模式。
- `UnsignedPayload`：通过 HTTPS 使用非流式 `UNSIGNED-PAYLOAD`，保留 SigV4 请求认证并发送显式 `Content-MD5`。任何服务类型都可选择，用于不支持 Streaming SigV4 的 S3-compatible 服务；HTTP Endpoint 会被拒绝。

Cloudflare R2 建议选择独立的 `Cloudflare R2` 类型（CLI `--type r2`），配置 HTTPS S3 API Endpoint 与 `auto` 签名 Region，保持 `Auto` 即可。已有 `Custom / S3 Compatible` 连接也可不改变类型或凭据，直接选择 `UnsignedPayload`：

    s3explorer-cli profile update "existing-r2" --upload-signing unsigned-payload
    s3explorer-cli profile show "existing-r2" --output json

新建自定义连接时可指定：

    s3explorer-cli profile add --name "custom-r2" --type compatible --endpoint "https://<account-id>.r2.cloudflarestorage.com" --credential "r2-key" --upload-signing unsigned-payload

`profile list/show` 会显示保存的上传签名模式；使用 `profile update <name-or-id> --upload-signing auto` 可恢复默认。该更新命令只修改上传签名，不改变连接 ID、服务类型、Endpoint 或凭据。模式跟随连接保存在统一配置与连接导入导出包中。

单文件、目录批量、分片上传与空目录标记共用该策略。非流式模式在发送前以有界内存预读文件或当前分片计算 `Content-MD5`，随后重置流位置，因此会增加一次本地磁盘读取。仅替换 SDK 的流式 checksum 生成方式，不取消服务端内容校验，也不会全局关闭 AWS、MinIO 或其他连接的校验。CLI `upload --verify` 仍可额外下载并验证大小与 SHA-256。

旧版出现 `STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented` 而列目录正常，原因是上传请求体签名模式不兼容，并不表示 R2 不支持上传或 multipart。参考 [Cloudflare .NET SDK 指引](https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/#upload-and-retrieve-objects) 与 [R2 S3 API 兼容表](https://developers.cloudflare.com/r2/api/s3/api/)。自动化协议测试使用假凭据和本地 HTTP handler 验证 SDK 实际生成的头、请求体和分片边界，不能替代真实 R2 账户验收。

## 数据位置与安全

- 统一配置（连接、CDN、凭据）：`%APPDATA%\S3Explorer\configuration.json`
- 文件夹同步任务：`%APPDATA%\S3Explorer\sync-jobs.json`
- 旧版配置首次迁移归档：`%APPDATA%\S3Explorer\legacy-archive\<UTC 时间戳>\`
- 日志目录：`%LOCALAPPDATA%\S3Explorer\logs`
- `configuration.json` 整体使用 DPAPI CurrentUser 加密；SecretKey、SessionToken、CDN Secret 和 AssumeRole External ID 不会明文落盘。
- AWS Profile/SSO 连接只保存 Profile 名称；SSO 浏览器令牌、Web Identity token 内容、环境/容器/实例角色凭据和短期角色会话不写入 `configuration.json`、连接包或日志。
- 凭据按 Provider 和类型显式校验；Alibaba Cloud AccessKey 可由 OSS 与 Alibaba CDN 共享，腾讯云 CDN 使用 SecretId/SecretKey，Cloudflare CDN 使用 API Token；内容 Token/Header 不会与控制面或 S3 SecretKey 混用。
- 导出配置默认不包含 S3 或 CDN 秘密值；显式选择后使用迁移密码重新加密，不复制本机 DPAPI 密文。
- 日志不得记录 SecretKey、SessionToken、Authorization Header 或完整预签名 URL。
- 忽略证书错误仅用于用户明确配置的测试环境。

统一配置主文件损坏时会尝试恢复 `.bak`；两者都不可用时，原文件保留为同目录的 `configuration.json.corrupt-*`，重复启动也不会静默创建空配置。桌面端会显示恢复窗口：可选择此 Windows 用户能够解密的统一配置或备份，验证成功后再恢复；也可经明确确认创建空配置并打开程序，随后重新添加连接或导入连接包。上述操作不会删除保留的损坏文件，也不会改变远端对象。CLI/自动化遇到不可恢复配置仍报告失败，不自动重置；可先使用桌面端恢复，或还原有效的同用户加密配置。

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

生命周期编辑当前对 Amazon S3 开放完整规则；MinIO 已实测对象过期与非当前版本规则，但存储分层转换和未完成 Multipart 生命周期清理会在本地阻止。Object Lock 提供 AWS Bucket 状态探测和单对象 Retention/Legal Hold；不提供事后启用 Bucket Object Lock 或修改默认保留期。安装版支持用户确认的静默升级，但不做无人值守自动安装；便携 ZIP 继续打开下载页面，由用户手动替换。托盘驻留可在设置中显式启用。

CDN 当前提供通用 HTTP 交付域名、CLI 探测/预热、可配置 HTTP 刷新端点、阿里云与腾讯云原生刷新/预热、Cloudflare Zone 查询与按 URL 清理、持久作业队列和显式开启的上传后自动化。Cloudflare 没有原生预热接口，工具明确使用内容 URL GET 预热；尚未实现 CloudFront 或统一 Prefix Purge。

文件夹同步当前是本地文件夹与 S3 路径之间的单向镜像。分析结果可筛选、排序、逐项勾选并从右键添加文件/扩展名/目录排除规则；分析快照会在任务参数变化或 15 分钟后失效。执行记录支持脱敏 JSON/CSV 导出和失败项重新入队。默认比较大小与修改时间；启用哈希比较后，仅对可作为 MD5 的单段 ETag 做内容比较，Multipart ETag 会回退到大小与时间。扫描遇到本地符号链接或目录联接时不会跟随；该路径及其子项会明确列为已排除，保留对应远端数据，不将未扫描内容误判为已删除，其余项目照常同步。删除传播默认关闭且执行前必须确认。

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
