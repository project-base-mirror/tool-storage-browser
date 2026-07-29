# AWS 外部凭据来源

S3 Explorer v0.5.6 起不再用空 Access Key 暗示“可能由外部提供凭据”，而是为每个 Amazon S3 连接保存明确的凭据来源。桌面端、CLI、连接测试、Bucket/Object 操作和文件夹同步共用同一个解析器。

## 可选来源

| 来源 | 保存内容 | 运行时行为 |
| --- | --- | --- |
| 已保存密钥 | Access Key；DPAPI 加密的 Secret Key/Session Token | 始终使用该连接自身的密钥 |
| AWS shared profile | 非敏感 Profile 名称 | 从 `~/.aws/credentials` 和 `~/.aws/config` 读取指定 Profile |
| AWS 环境变量 | 只保存来源枚举 | 读取 `AWS_ACCESS_KEY_ID`、`AWS_SECRET_ACCESS_KEY` 和可选 `AWS_SESSION_TOKEN` |
| AWS 容器角色 | 只保存来源枚举 | 读取 `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` 或 `AWS_CONTAINER_CREDENTIALS_FULL_URI` 指向的端点 |
| AWS EC2 实例角色 | 只保存来源枚举 | 使用 EC2 Instance Metadata；`AWS_EC2_METADATA_DISABLED=true` 时明确失败 |
| AWS SDK 默认凭据链 | 只保存来源枚举 | 由 AWS SDK 选择，并在连接测试结果中显示实际采用的来源 |

除了“默认凭据链”，其他外部来源都是锁定来源：指定 Profile 缺失、环境变量不完整或容器角色端点不存在时会直接报告原因，不会静默切换到另一个 AWS 身份。

## 桌面端

在“新建/编辑对象存储连接”中选择 Amazon S3，然后选择“凭据来源”。

- 选择已保存密钥时显示 Access Key、Secret Key 和可选 Session Token。
- 选择 shared profile 时只显示 AWS Profile 名称。
- 选择环境变量、容器角色、实例角色或默认链时不显示密钥输入框。
- 切换到 S3-compatible、Google XML API 或其他 Provider 后，来源会锁定为已保存密钥，避免读取本机 AWS 身份。

“测试连接”成功时会显示实际凭据来源。账户树悬停提示和连接摘要也会显示来源，但不会显示密钥值。

## CLI

指定 shared profile：

    s3explorer-cli profile add --name aws-audit --type amazon `
        --credential-source profile --aws-profile readonly

锁定环境变量：

    s3explorer-cli profile add --name aws-env --type amazon `
        --credential-source environment

容器角色、实例角色和默认链分别使用：

    --credential-source container
    --credential-source instance
    --credential-source default

默认值仍是 `stored`。保存密钥时继续使用 `--access-key` 与 `--secret-key-env`。外部来源与这些密钥参数同时出现时 CLI 会拒绝，防止用户误以为参数会被采用。

`profile list`、`profile show --json` 与 `connection test --json` 都返回凭据来源；不会返回解析后的 Access Key、Secret Key 或 Session Token。

## 持久化与连接包

- `profiles.json` schema 3 保存 `credentialSource` 和可选 `awsProfileName`。外部来源的 Access Key、加密 Secret Key 与加密 Session Token 字段保持为空。
- `.s3connections` 格式 2 保存相同的非敏感来源引用，同时兼容导入格式 1。
- 即使用户选择“包含凭据”导出，外部来源解析得到的临时或长期凭据也不会复制到连接包。
- 导入到另一台机器后，目标环境必须自行配置同名 Profile、环境变量或角色。

## 当前边界

当前版本不接管 AWS SSO、AssumeRole、Web Identity、浏览器登录或它们的令牌缓存。如果 shared profile 或默认链解析到这些高级身份类型，客户端会说明该能力计划在 v0.6.8 高级身份阶段接入，而不是把令牌混入长期密钥存储或连接包。
