# 统一凭据中心

S3 Explorer 使用一个统一的 Credential Vault 管理对象存储、AWS 外部 ID 和 CDN 凭据。对象存储连接与 CDN Profile 只保存 `CredentialId` 引用；秘密值不再分别保存在各自的运行时配置文件中。

## 存储与迁移

运行时唯一的配置文件是：

```text
%APPDATA%\S3Explorer\configuration.json
```

文件整体使用 Windows DPAPI `CurrentUser` 保护，只有当前 Windows 用户上下文可以解密。凭据载荷不会以明文写入 JSON；日志、连接列表和 CLI 输出只显示凭据类型、提供方和非秘密指纹。

首次启动时，如果不存在 `configuration.json`，程序会读取旧的 `profiles.json`、`cdn-config.json` 和 `cdn-credentials.json`，把密钥、Token 和 AWS External ID 迁入统一 Vault，写入新的加密配置后，将旧文件移动到：

```text
%APPDATA%\S3Explorer\legacy-archive\<UTC 时间戳>\
```

迁移只执行一次。已有 `configuration.json` 时不会再次合并旧文件，也不会让旧文件覆盖当前配置；程序会在成功验证统一配置后继续归档遗留的旧文件。旧归档用于审计和恢复，确认不再需要后可由用户手动处理。

首次迁移时不要同时运行旧版桌面程序或旧版 CLI。旧版本不认识统一配置，迁移后继续保存旧文件可能产生两份彼此不同的配置；应先退出旧版本，再启动新版本完成一次性迁移。

## 桌面端

从顶级“凭据 → 凭据中心”打开独立凭据中心。可以新增、查看、编辑和删除凭据，并在对象存储连接或 CDN 配置中选择已有凭据。删除仍被连接或 CDN Profile 引用的凭据会被阻止。CDN 配置与 Bucket/前缀关联仍保留在“CDN / 分发 → CDN 配置中心”，不会混入凭据窗口。

保存配置时会校验引用关系、提供方和凭据类型的一致性。比如 Alibaba Cloud 凭据只能用于 Alibaba OSS 或 Alibaba CDN Profile；Generic HTTP CDN 使用 Bearer Token 或自定义 Header。修改凭据类型导致已有引用不再有效时，保存会失败并保留原配置。

凭据中心提供“检查关联权限”。默认检查是无副作用的：对象存储只检查列举和对象属性读取，写入、删除和 ACL 权限显示为“无法确定”；CDN 只执行配置端点和认证响应分类，不发送刷新请求。

“凭据 → 权限检查”按凭据和目标范围显示最近一次结果，并区分只读检查与写入探针。结果保存在非敏感的 `%APPDATA%\S3Explorer\permission-check-history.json` 中，只包含凭据名称、类型、指纹、目标和脱敏后的检查信息，不包含密钥、Token 或其他秘密值。

需要验证 Put/Delete/ACL 时，可在该列表中选择“执行写入探针”。每次执行都必须选择具体对象存储连接、Bucket 和非空隔离 Prefix，再输入 `PROBE` 并勾选一次性确认。程序会上传临时对象、按需设置 Private ACL，然后删除临时对象；确认不会保存为永久设置。删除失败会明确提示对象可能残留。

## CLI

```text
s3explorer-cli credential list
s3explorer-cli credential show <name-or-id>
s3explorer-cli credential add --name <name> --provider <provider> --kind <kind> --secret-env <ENV_NAME> [--access-key-id <id>] [--session-token-env <ENV_NAME>] [--header <name>]
s3explorer-cli credential delete <name-or-id> --yes
```

`credential add` 只从环境变量读取秘密，避免 Secret 出现在命令行历史中。对象存储连接使用 `profile add --credential <name-or-id>`；AWS AssumeRole 的 External ID 使用独立的 `SecretValue` 凭据并通过 `--external-id-credential` 引用。

权限检查：

```text
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --operation read
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --prefix <prefix> --operation mirror
s3explorer-cli permission check --cdn-profile <name-or-id>
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --operation publish --probe-write --yes
```

CLI 仍可用于自动化。`--probe-write` 会在指定范围创建临时探针对象、执行必要的 ACL 操作并删除它；只允许与 `--yes` 一起使用。删除探针失败会报告为失败，不会伪装成权限检查成功。探针应使用隔离 Bucket 或明确的专用前缀。

## 连接包

连接包 v4 使用统一凭据模型。默认导出不带秘密，只导出配置和引用；选择“包含凭据”时，所有选定的凭据由迁移密码使用 AES-GCM 加密，导入后写入目标设备的 DPAPI Vault。旧 v1-v3 连接包仍只通过一次性导入迁移路径转换为统一凭据；不会把旧的明文运行时模型重新引入生产配置。

无凭据导入可以保留未配置引用，方便先迁移 Endpoint 模板；需要执行对象或 CDN 操作前必须补齐对应凭据。共享凭据引用不会在导入界面被拆成两份。

## 安全边界

- 不复用 S3 SecretKey 作为通用 CDN Token。
- 不把 SSO token、Web Identity token、短期角色会话或环境/容器/实例角色凭据写入 Vault。
- 不在日志、JSON 输出或错误消息中输出秘密值。
- `configuration.json` 是唯一运行时配置来源；旧文件只用于首次迁移，不作为回退路径。
- 权限探针是显式、有范围、可审计的操作，不会被普通连接测试或 CDN 刷新检查隐式触发。
