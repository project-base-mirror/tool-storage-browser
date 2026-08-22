# AWS 凭据与高级身份

S3 Explorer 为每个 Amazon S3 连接保存明确的凭据来源。桌面端、CLI、连接测试、Bucket/Object 操作和文件夹同步共用同一个解析器；引用的 Access Key 和 AssumeRole External ID 由统一 Credential Vault 保存于 DPAPI 保护的 `configuration.json`。S3-compatible 连接仍只允许明确保存的密钥，不读取本机 AWS 身份。

## 可选来源

| 来源 | 保存内容 | 运行时行为 |
| --- | --- | --- |
| 已保存密钥 | 引用统一 Vault 中的 Access Key 凭据 | 始终使用该连接自身的密钥 |
| AWS shared profile | 非敏感 Profile 名称 | 从 `~/.aws/credentials` 和 `~/.aws/config` 读取指定 Profile |
| AWS 环境变量 | 只保存来源枚举 | 读取 `AWS_ACCESS_KEY_ID`、`AWS_SECRET_ACCESS_KEY` 和可选 `AWS_SESSION_TOKEN` |
| AWS 容器角色 | 只保存来源枚举 | 读取 `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` 或 `AWS_CONTAINER_CREDENTIALS_FULL_URI` 指向的端点 |
| AWS EC2 实例角色 | 只保存来源枚举 | 使用 EC2 Instance Metadata；`AWS_EC2_METADATA_DISABLED=true` 时明确失败 |
| AWS SDK 默认凭据链 | 只保存来源枚举 | 由 AWS SDK 选择，并在连接测试结果中显示实际采用的来源 |
| IAM Identity Center (SSO) | 非敏感 SSO Profile 名称 | 读取 AWS config 与 SDK token cache；仅在用户发起连接测试时允许打开登录页 |
| AssumeRole | 源 Profile、Role ARN、会话名称、可选 Source Identity、会话时长；引用统一 Vault 中的 External ID | AWS SDK 使用源 Profile 获取短期角色会话并自动刷新 |
| Web Identity | Role ARN、会话名称、会话时长和 token 文件绝对路径 | AWS SDK 按需读取文件并获取短期角色会话；应用不读取或保存 token 内容 |

除了“默认凭据链”，其他外部来源都是锁定来源。指定 Profile 缺失、环境变量不完整、容器端点不存在或角色配置无效时会直接报告原因，不会静默切换到另一个 AWS 身份。

## SSO 登录边界

- 普通浏览、传输、同步和自动化不会主动发起浏览器登录，只使用 AWS SDK 已有的有效缓存。
- 用户点击“测试连接”时，若 SSO token 缺失或过期，SDK 可打开完整验证 URL；应用不记录 URL、user code、access token、refresh token 或 ID token。
- SSO 浏览器 token 位于 AWS SDK 自己的缓存，不进入 `configuration.json`、`.s3connections` 或 DPAPI 长期密钥字段。
- 连接测试会显示 SSO account/role，并在无法后台刷新时提示需要用户触发登录。

## AssumeRole 与 Web Identity 诊断

连接测试会显示源身份、目标 Role ARN、External ID 的“已配置/未配置”状态，以及 SDK 已生成角色会话后的本地到期时间。External ID 的实际值和 Web Identity token 文件内容不会出现在诊断或日志中。

角色会话按连接配置缓存并由 AWS SDK 刷新，和已保存长期密钥、SSO token cache、连接包密码分别管理。配置变更会使用新的会话缓存键。

## CLI

指定 SSO Profile：

    s3explorer-cli profile add --name aws-sso --type amazon `
        --credential-source sso --aws-profile company-readonly

配置 AssumeRole：

    $env:AWS_AUDIT_EXTERNAL_ID = "provided-out-of-band"
    s3explorer-cli credential add --name aws-external-id --provider aws --kind secret-value --secret-env AWS_AUDIT_EXTERNAL_ID
    s3explorer-cli profile add --name aws-audit-role --type amazon `
        --credential-source assume-role --source-profile bootstrap `
        --role-arn arn:aws:iam::123456789012:role/Audit `
        --role-session-name s3explorer-audit --source-identity operator-42 `
        --external-id-credential aws-external-id --session-duration 1800

配置 Web Identity：

    s3explorer-cli profile add --name workload --type amazon `
        --credential-source web-identity `
        --role-arn arn:aws:iam::123456789012:role/Workload `
        --role-session-name s3explorer-workload `
        --web-identity-token-file D:\identity\token.jwt

原有 `profile|environment|container|instance|default` 来源继续兼容。`profile list`、`profile show --output json` 与 `connection test --output json` 返回来源和非敏感诊断，不返回解析后的 Access Key、Secret Key、Session Token、External ID 或 token。

## 持久化与连接包

- `configuration.json` 保存连接分组和高级身份配置；Secret Key、Session Token 与 External ID 由统一 Vault 保护。
- `.s3connections` 格式 5 保留 SSO/AssumeRole/Web Identity 的非敏感引用；账户分组是目标设备的本地组织方式，不随连接包导出。
- 默认无凭据连接包省略 External ID。明确勾选凭据并设置迁移密码后，External ID 与已保存 S3/CDN 凭据一起进入整体 AES-GCM 加密载荷。
- SSO token、短期角色会话和 Web Identity token 内容在任何情况下都不会进入连接包。
- 导入到另一台机器后，目标环境必须自行配置同名 Profile、SSO 登录环境或 token 文件；导入预览可把新增/覆盖连接放入指定本地分组。

## 日志脱敏

日志和用户可见错误会清理 Authorization、预签名签名字段，以及 `access_token`、`refresh_token`、`id_token`、`web_identity_token`、`external_id`、`client_secret`、`device_code` 和 `user_code` 形式的值。安全验收不依赖调用方避免出错文本，而是在基础设施与 CLI 输出边界再次脱敏。
