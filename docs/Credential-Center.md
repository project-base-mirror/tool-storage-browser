# 统一凭据中心

S3 Explorer 使用统一 Credential Vault 管理对象存储、AWS 外部 ID 和 CDN **控制面**凭据。对象存储连接保存 `CredentialId`，CDN Profile 保存 `ControlCredentialId`。CDN 内容 URL 使用的 Bearer Token 或自定义 Header 属于该 CDN Profile 的内联内容认证，不作为全局凭据中心项目，也不会被控制面 API 复用。

## 存储与迁移

运行时唯一的配置文件是：

```text
%APPDATA%\S3Explorer\configuration.json
```

文件整体使用 Windows DPAPI `CurrentUser` 保护，只有当前 Windows 用户上下文可以解密。凭据载荷不会以明文写入 JSON；日志、连接列表和 CLI 输出只显示凭据类型、提供方和非秘密指纹。

首次启动时，如果不存在 `configuration.json`，程序会读取旧的 `profiles.json`、`cdn-config.json` 和 `cdn-credentials.json`。对象存储密钥、AWS External ID 和仍用于 CDN 控制面的凭据进入统一 Vault；旧 CDN 内容 Token 则迁入对应 Profile 的内联内容认证。写入新的加密配置后，将旧文件移动到：

```text
%APPDATA%\S3Explorer\legacy-archive\<UTC 时间戳>\
```

迁移只执行一次。已有 `configuration.json` 时不会再次合并旧文件，也不会让旧文件覆盖当前配置；程序会在成功验证统一配置后继续归档遗留的旧文件。旧归档用于审计和恢复，确认不再需要后可由用户手动处理。

首次迁移时不要同时运行旧版桌面程序或旧版 CLI。旧版本不认识统一配置，迁移后继续保存旧文件可能产生两份彼此不同的配置；应先退出旧版本，再启动新版本完成一次性迁移。

## 桌面端

从顶级“凭据 → 凭据中心”打开独立凭据中心。可以新增、查看、编辑和删除凭据，并在对象存储连接或 CDN 配置中选择已有凭据。删除仍被连接或 CDN Profile 引用的凭据会被阻止。CDN 配置与 Bucket/前缀关联仍保留在“CDN / 分发 → CDN 配置中心”，不会混入凭据窗口。

保存配置时会校验引用关系、提供方和凭据类型的一致性。Alibaba CDN 控制面只接受 Alibaba Cloud AccessKey；通用 HTTP 刷新端点的控制面只接受 Generic HTTP Bearer Token 或自定义 Header。CDN 内容认证在 CDN Profile 编辑器内单独设置。

修改提供方或凭据类型导致已有引用不兼容时，“保存更改”会列出受影响的对象存储连接、AWS AssumeRole External ID 和 CDN 控制面，默认不确认。选择“是”才会在一次原子保存中更新凭据并清除列出的不兼容引用，不会删除凭据、连接或 CDN Profile。取消确认不写配置，仍可继续编辑或放弃草稿。仍然兼容的引用、CDN 内容认证、关联、Endpoint 等设置保持不变；被清除引用的连接或控制面需要重新选择兼容凭据。仅更名、轮换 Secret 或其他仍然兼容的更改不会触发清除。

保存时重新读取最新配置并检查已确认的引用。如果其他窗口或 CLI 在此期间新增了不兼容引用、修改了受影响连接/CDN 配置，或更改/删除了正在编辑的凭据，本次保存会整体中止，要求重新打开后查看并确认，不会扩大清除范围。其他凭据的并发新增和修改会保留。清除存储引用也会清除内存中解析出的 Access Key、Secret Key 和 Session Token；清除 External ID 引用也会清除解析出的 External ID。所有写入在替换配置和备份前，都会使用新凭据重新解析并校验连接；例如已被 AssumeRole 引用的 External ID 改成过短或含空格的值，会在写盘前拒绝，不会出现提示保存失败却已写入坏配置的情况。

凭据中心提供“检查关联权限”。默认检查是无副作用的：对象存储检查列举、对象属性，并且仅在前 100 个对象中找到不超过 64 KiB 的文件时验证内容下载；找不到小文件就保持“无法确定”，不会为了权限验证下载大文件。写入、删除和 ACL 权限必须通过显式探针。CDN 这里只检查控制面；内容认证由 CDN 配置中心使用实际 Bucket/Prefix 关联对象检查。

“凭据 → 权限检查”首先显示权限矩阵：每项凭据一行，列举、属性、下载、上传、删除、ACL、CDN 控制面查询和 CDN 刷新/预热分别以 `√`、`×`、`?`、`—` 表示通过、明确拒绝、无法确定和未记录/不适用。一个凭据关联多个 Bucket、Prefix 或 CDN 控制面时，矩阵按最严格结果汇总，具体目标结果可从二级“检查记录”窗口查看。

矩阵中的“立即检查 OSS/CDN 控制面”执行无副作用检查并更新最近结果：阿里云 CDN 调用 `DescribeUserDomains`；通用 HTTP 只确认控制端点配置，不发送刷新请求，也不再向内容 Base URL 发送控制凭据。刷新/预热会产生真实控制面任务，因此写权限列通常显示 `?`，而不是误报成功。“存储探针”只列出当前凭据关联的对象存储连接。需要验证 Put/Delete/ACL 时，每次都必须选择具体连接、Bucket 和非空隔离 Prefix，再输入 `PROBE` 并勾选一次性确认。程序会上传临时对象、按需设置 Private ACL，然后删除临时对象；确认不会保存为永久设置。删除失败会明确提示对象可能残留。

检查结果保存在非敏感的 `%APPDATA%\S3Explorer\permission-check-history.json` 中，只包含凭据名称、类型、指纹、目标和脱敏后的检查信息，不包含密钥、Token 或其他秘密值。该文件在 Debug 下保存在独立的 `%APPDATA%\S3Explorer.Debug` 目录，不会与正式版检查记录混用。

## CLI

```text
s3explorer-cli credential list
s3explorer-cli credential show <name-or-id>
s3explorer-cli credential add --name <name> --provider <provider> --kind <kind> --secret-env <ENV_NAME> [--access-key-id <id>] [--session-token-env <ENV_NAME>] [--header <name>]
s3explorer-cli credential update <name-or-id> [--name <name>] [--provider <provider>] [--kind <kind>] [--secret-env <ENV_NAME>] [--access-key-id <id>] [--session-token-env <ENV_NAME>] [--header <name>] [--clear-incompatible-references --yes]
s3explorer-cli credential delete <name-or-id> --yes
```

`credential add` 和 `credential update` 只从环境变量读取新的秘密，避免 Secret 出现在命令行历史中。`update` 保留未指定的字段和原凭据 ID；改变类型时清除不再适用的 Access Key、Session Token 或 Header 字段，并校验新类型所需字段。缺失/空环境变量会阻止保存，不会把旧秘密意外替换为空。对象存储连接使用 `profile add --credential <name-or-id>`；AWS AssumeRole 的 External ID 使用独立的 `SecretValue` 凭据并通过 `--external-id-credential` 引用。

CLI 更新遇到不兼容引用时会列出名称和 ID，并拒绝写入。确认影响后重试时必须同时提供 `--clear-incompatible-references --yes`；单独 `--yes` 不授权清除引用。CLI 与桌面端使用同一套原子更新与并发复核规则。JSON 成功结果包含安全的 `credential` 概要和实际清除的 `clearedReferences`，不含秘密值。

```text
# 先查看类型更改影响（如有引用，不会保存）
s3explorer-cli credential update r2-key --kind bearer-token
# 确认清除列出的不兼容引用后再保存；凭据 ID 保持不变
s3explorer-cli credential update r2-key --kind bearer-token --clear-incompatible-references --yes
# 兼容的名称/Secret 更新不需要清除标志
s3explorer-cli credential update r2-key --name r2-new-name --secret-env R2_NEW_SECRET
```

权限检查：

```text
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --operation read
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --prefix <prefix> --operation mirror
s3explorer-cli permission check --cdn-profile <name-or-id>
s3explorer-cli permission check --storage-profile <name-or-id> --bucket <bucket> --operation publish --probe-write --yes
```

CLI 仍可用于自动化。`--probe-write` 会在指定范围创建临时探针对象、执行必要的 ACL 操作并删除它；只允许与 `--yes` 一起使用。删除探针失败会报告为失败，不会伪装成权限检查成功。探针应使用隔离 Bucket 或明确的专用前缀。

## 连接包

连接包 v6 同时表达内联内容认证、控制面凭据引用和非秘密控制资源 ID。默认导出会清除两类秘密和引用，但保留 Cloudflare Zone ID 等非秘密配置；选择“包含凭据”时，统一凭据与内联内容秘密都由迁移密码使用 AES-GCM 加密。旧 v1-v5 连接包通过一次性导入迁移路径转换：旧 Generic HTTP 凭据复制为内联内容认证，只有配置了刷新端点时才继续保留控制面凭据；旧 Aliyun 引用迁为控制面凭据。

无凭据导入可以保留未配置引用，方便先迁移 Endpoint 模板；需要执行对象或 CDN 操作前必须补齐对应凭据。共享凭据引用不会在导入界面被拆成两份。

## 安全边界

- 不复用 S3 SecretKey 作为通用 CDN Token。
- CDN 内容 Token 只发送到映射后的内容 URL；CDN 控制面凭据只发送到 Provider 管理 API 或明确配置的刷新端点。
- 不把 SSO token、Web Identity token、短期角色会话或环境/容器/实例角色凭据写入 Vault。
- 不在日志、JSON 输出或错误消息中输出秘密值。
- `configuration.json` 是唯一运行时配置来源；旧文件只用于首次迁移，不作为回退路径。
- 权限探针是显式、有范围、可审计的操作，不会被普通连接测试或 CDN 刷新检查隐式触发。
