# S3 Explorer

S3 Explorer 是一个面向 Windows 10/11 x64 的原生 S3 对象存储管理工具。它使用 C#、.NET 10、WinForms 和 AWS SDK for .NET 构建，不依赖浏览器、Electron、Node.js、WebView 或数据库。

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

## 运行要求

推荐使用 framework-dependent 包：

- Windows 10 或 Windows 11 x64。
- .NET 10 Desktop Runtime x64。

备用 self-contained 包自带所需运行时，不要求预装 .NET Desktop Runtime，但体积更大。

## 从源码构建

需要安装 .NET 10 SDK。

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

## 发布

仓库提供 PowerShell 发布脚本：

    pwsh .\scripts\Publish.ps1

脚本默认执行 restore、全量测试和 Release 构建，然后生成：

    artifacts/release/S3Explorer-win-x64.zip
    artifacts/release/S3Explorer-win-x64-self-contained.zip
    artifacts/release/release-metrics.json

发布配置明确关闭 trimming 和单文件打包，避免 AWS SDK、JSON 序列化或 WinForms 反射类型被误删。

仅重新打包而跳过验证：

    pwsh .\scripts\Publish.ps1 -SkipValidation

在交互式 Windows 桌面上额外记录启动时间和空闲 Working Set：

    pwsh .\scripts\Publish.ps1 -MeasureRuntime

`release-metrics.json` 会记录两个发布目录的压缩前大小、ZIP 大小、ZIP SHA-256、SDK 版本，以及可选的启动时间和内存数据。

## 数据位置与安全

- 连接配置：`%APPDATA%\S3Explorer\profiles.json`
- 日志目录：`%LOCALAPPDATA%\S3Explorer\logs`
- SecretKey 和 SessionToken 使用 DPAPI CurrentUser 加密后保存。
- 导出配置默认不包含凭据。
- 日志不得记录 SecretKey、SessionToken、Authorization Header 或完整预签名 URL。
- 忽略证书错误仅用于用户明确配置的测试环境。

## 项目结构

    src/
      S3Explorer.App                 WinForms 桌面应用
      S3Explorer.Core                核心模型、路径和接口
      S3Explorer.Infrastructure.S3   AWS SDK、凭据与配置实现
    tests/
      S3Explorer.Core.Tests
      S3Explorer.Infrastructure.S3.Tests
    scripts/
      Publish.ps1

## 当前限制

第一阶段不承诺完整实现 Bucket Policy、CORS、生命周期、版本历史、Object Lock、ACL/Public Access Block 编辑、自动更新和托盘驻留。未支持的入口应保持禁用或明确提示当前版本不支持。

对象“目录”由 `Delimiter = "/"`、`CommonPrefixes` 和以 `/` 结尾的零字节对象模拟；S3 本身没有本地文件系统意义上的真实目录。

## 发布检查清单

1. `dotnet restore` 成功。
2. 全量 `dotnet test` 成功。
3. Release `dotnet build` 成功。
4. 两种发布目录和 ZIP 均生成。
5. 检查 `release-metrics.json` 中的体积和哈希。
6. 在发布机上启动 framework-dependent 包，验证连接窗口、对象列表和 WinForms 控件。
7. 需要性能记录时，在交互式桌面使用 `-MeasureRuntime`。