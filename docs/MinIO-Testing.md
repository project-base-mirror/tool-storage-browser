# MinIO 测试环境与 S3 Explorer 验证流程

本文记录局域网 MinIO 测试环境、凭据获取方式、自动化集成测试和手工 UI 验证步骤。文档不保存任何 Secret Key；服务器配置是凭据的唯一来源。

## 环境信息

| 项目 | 当前值 |
| --- | --- |
| 服务器 | `192.168.31.200` |
| 系统 | Debian 12 x86_64 |
| MinIO 发行版 | Pigsty 维护版 `RELEASE.2026-06-18T00-00-00Z` |
| S3 API | `https://oss.lan.policoil.top` |
| 管理控制台 | `https://minio.lan.policoil.top` |
| 数据目录 | `/data/minio` |
| MinIO 配置 | `/etc/default/minio` |
| MinIO 服务 | `minio.service` |
| API Nginx 配置 | `/etc/nginx/conf.d/oss.lan.policoil.top.conf` |
| Console Nginx 配置 | `/etc/nginx/conf.d/minio.lan.policoil.top.conf` |

外部访问统一使用 HTTPS。S3 Explorer 不应连接 `9000` 或 `9001` 的明文地址，也不应开启“忽略证书错误”。

## 健康检查

Windows：

    Invoke-WebRequest -UseBasicParsing https://oss.lan.policoil.top/minio/health/ready
    Invoke-WebRequest -UseBasicParsing https://minio.lan.policoil.top/

服务器：

    systemctl status minio --no-pager
    systemctl status nginx --no-pager
    curl --noproxy '*' -fsS https://oss.lan.policoil.top/minio/health/ready
    nginx -t

预期两个 HTTPS 请求均返回 HTTP 200，MinIO 与 Nginx 均为 `active`。

## 获取管理员凭据

管理员 Access Key 和 Secret Key 存放在 `/etc/default/minio`，文件权限为 `0600 root:root`。只在自己的可信终端查看：

    ssh root@192.168.31.200 "grep -E '^MINIO_ROOT_(USER|PASSWORD)=' /etc/default/minio"

- `MINIO_ROOT_USER` 对应 Access Key。
- `MINIO_ROOT_PASSWORD` 对应 Secret Key。

不要把输出复制到聊天、工单、截图、日志、仓库、`.env` 或 CI 配置。管理员凭据仅用于控制台管理和创建测试账户；日常测试优先使用临时账户。

## S3 Explorer 手工连接

| 字段 | 值 |
| --- | --- |
| 名称 | `MinIO LAN` |
| 服务类型 | `MinIO` |
| Endpoint | `https://oss.lan.policoil.top` |
| Region | `us-east-1` |
| Addressing Style | `Path Style` |
| HTTPS | 开启 |
| 忽略证书错误 | 关闭 |
| Access Key / Secret Key | 使用临时测试账户或服务器配置 |

保存前先执行连接测试。S3 Explorer 会使用 Windows DPAPI CurrentUser 加密保存 Secret Key。

## 创建一次性集成测试账户

自动化测试不要长期使用管理员凭据。登录服务器后创建随机临时账户：

    sudo -i
    set -euo pipefail

    ROOT_USER=$(sed -n 's/^MINIO_ROOT_USER=//p' /etc/default/minio)
    ROOT_PASSWORD=$(sed -n 's/^MINIO_ROOT_PASSWORD=//p' /etc/default/minio)
    WORK=$(mktemp -d)
    export MC_CONFIG_DIR="$WORK/mc"

    mcli alias set local https://oss.lan.policoil.top "$ROOT_USER" "$ROOT_PASSWORD"

    ACCESS_KEY="s3xci-$(openssl rand -hex 8)"
    SECRET_KEY=$(openssl rand -base64 36 | tr -d '\r\n+/=' | cut -c1-40)

    mcli admin user add local "$ACCESS_KEY" "$SECRET_KEY"
    mcli admin policy attach local readwrite --user "$ACCESS_KEY"
    printf 'Access Key: %s\nSecret Key: %s\n' "$ACCESS_KEY" "$SECRET_KEY"

把输出仅复制到当前测试终端。测试结束后删除账户：

    mcli admin user remove local "$ACCESS_KEY"
    rm -rf "$WORK"
    unset ROOT_USER ROOT_PASSWORD ACCESS_KEY SECRET_KEY MC_CONFIG_DIR

`readwrite` 是当前 CRUD 集成测试所需权限。以后拆分只读、只写或受限路径测试时，应创建更小权限的独立策略。

## 运行真实 MinIO 集成测试

集成测试默认不会访问远程服务。只有同时设置 Endpoint、Access Key 和 Secret Key 时才执行真实 CRUD。

PowerShell：

    $env:S3EXPLORER_TEST_ENDPOINT = 'https://oss.lan.policoil.top'
    $env:S3EXPLORER_TEST_REGION = 'us-east-1'
    $env:S3EXPLORER_TEST_ACCESS_KEY = '<临时 Access Key>'
    $env:S3EXPLORER_TEST_SECRET_KEY = '<临时 Secret Key>'

    dotnet test .\tests\S3Explorer.Infrastructure.S3.Tests\S3Explorer.Infrastructure.S3.Tests.csproj `
      -c Release `
      --filter 'FullyQualifiedName~MinioIntegrationTests' `
      --logger 'console;verbosity=normal'

测试完成后立即清除环境变量：

    Remove-Item Env:S3EXPLORER_TEST_ENDPOINT -ErrorAction SilentlyContinue
    Remove-Item Env:S3EXPLORER_TEST_REGION -ErrorAction SilentlyContinue
    Remove-Item Env:S3EXPLORER_TEST_ACCESS_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:S3EXPLORER_TEST_SECRET_KEY -ErrorAction SilentlyContinue

现有测试使用生产代码中的 `S3ClientFactory` 和 `S3StorageService`，覆盖：

1. 创建随机临时 Bucket。
2. 上传对象。
3. 分页列出目录和对象。
4. 服务端复制与移动对象。
5. 读取对象属性。
6. 创建预签名下载 URL。
7. 下载并校验文件内容。
8. 批量删除对象。
9. 删除空 Bucket。

成功时会删除远程 Bucket 和本地临时文件。测试异常中止时，检查并清理遗留的 `s3explorer-test-*` Bucket：

    mcli ls local
    mcli rb --force local/<遗留 Bucket 名称>

## 手工 UI 回归

1. 使用 MinIO LAN 配置连接。
2. 打开 `s3explorer-test` 或创建独立 UI 测试 Bucket。
3. 上传中文文件名、包含空格的文件和一个大文件。
4. 刷新列表并检查大小、时间和类型。
5. 下载并比较内容。
6. 测试复制、移动、重命名和批量删除。
7. 创建预签名 URL，并在浏览器中验证下载。
8. 连续快速切换连接、Bucket 和目录，确认不再出现 `CancellationTokenSource has been disposed`。
9. 在传输过程中取消，确认 UI 可恢复且无未处理异常。
10. 删除所有测试对象和临时 Bucket。

不要在共享 Bucket 中测试删除、移动或覆盖操作。

## 故障排查

### 域名解析到 `198.18.0.0/15`

这是 Mihomo/Clash Fake-IP 地址。`oss.lan.policoil.top` 和 `minio.lan.policoil.top` 应直接解析到 `192.168.31.200`。

    [System.Net.Dns]::GetHostAddresses('oss.lan.policoil.top')
    [System.Net.Dns]::GetHostAddresses('minio.lan.policoil.top')

### HTTP 403

检查凭据、临时用户和策略：

    mcli admin user info local <Access Key>

### TLS 或连接失败

- Endpoint 必须是 `https://oss.lan.policoil.top`。
- 不要把控制台域名当作 S3 Endpoint。
- Region 使用 `us-east-1`。
- Addressing Style 使用 Path Style。
- “忽略证书错误”保持关闭。

### HTTP 502/504

    systemctl is-active minio nginx
    ss -ltn '( sport = :9000 or sport = :9001 )'
    journalctl -u minio -n 100 --no-pager
    tail -n 100 /var/log/nginx/error.log

## 已验证记录

2026-07-23 已从 Windows 开发机通过 `https://oss.lan.policoil.top` 执行现有 `MinioIntegrationTests`：

- 测试结果：1/1 通过。
- TLS 校验开启。
- 完整 CRUD 通过。
- 随机测试 Bucket 已清理。
- 临时 MinIO 用户已删除。
- 测试后仓库保持干净。

修改 `S3ClientFactory`、`S3StorageService`、连接模型、签名、代理、上传下载或删除逻辑后，应重新执行本流程。
