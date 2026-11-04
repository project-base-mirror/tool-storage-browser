# Provider 复制安全补充验证（2026-11-04）

本记录描述审计修复后的增量实现与验证前提，不代表已发布。真实远端测试必须使用专门隔离的测试凭据；没有连接用户存储或使用用户已有密钥。

## MinIO 空对象复制

- 受保护的零字节复制不再尝试 Multipart：先发送带源 ETag 条件的 GET，核对版本、长度及实际空响应，再通过 `PutObject If-None-Match: *` 创建目标，或通过 `If-Match` 执行明确的条件替换。
- GET 失败、内容身份不同、源 Metadata 未完整返回、Tagging 读取失败或目标条件冲突都不会改成无条件写入。
- 复制使用已校验 GET 返回的 Content-Type、Cache-Control、Content-Encoding、Content-Disposition、Content-Language、Expires、自定义 Metadata，另读取源版本 Tags。加密、ACL 和存储类别遵循普通跨对象复制的目标默认行为，不是原地 Metadata 替换。
- 零字节复制不要求开启分片复制；非空对象受保护复制仍需开启分片并使用条件完成。自动移动仍然禁用，不会把一次复制冒充成移动完成。

### 服务端前提，不能由客户端单独保证

旧 MinIO 在无法取得读取 quorum 时可能忽略条件写入。官方 [问题 #21603](https://github.com/minio/minio/issues/21603) 与 [修复 #21653](https://github.com/minio/minio/pull/21653) 对此有明确说明。客户端不能通过先 HEAD 后 PUT 修复服务端原子性问题。

隔离 CI 从旧 `9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a` 更新到包含修复的官方提交 [`18f97e70b19db6b2940ae8a1b14c4665d2eeca36`](https://github.com/minio/minio/commit/18f97e70b19db6b2940ae8a1b14c4665d2eeca36)。其 [go.mod](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/go.mod) 使用 Go 1.24.8，保持现有工具链。CI 额外确认下列三个测试存在，并仅运行这三个上游 quorum 回归测试：

- `TestPutObjectConditionalWithReadQuorumFailure`
- `TestNewMultipartUploadConditionalWithReadQuorumFailure`
- `TestCompleteMultipartUploadConditionalWithReadQuorumFailure`

生产部署及第三方 fork 必须由运营方确认包含上述修复或等价实现。版本日期较新、`Server: MinIO` 或普通条件写入测试通过，都不能证明 quorum 故障路径已修复。S3 标准接口不提供可信的提交/功能证明，本客户端不会声称自动识别或拒绝所有旧实现。上述前提同样适用于原有 MinIO 条件上传和条件分片完成，不只空对象复制。仓库本地 Docker 测试脚本的旧镜像不能作为该 quorum 修复的证明；本轮权威隔离验证使用官方固定源码。

## Supabase 保留源对象的普通复制

[官方 S3 兼容性说明](https://supabase.com/docs/guides/storage/s3/compatibility) 区分了单次 `CopyObject` 的源条件和不支持这些条件的 `UploadPartCopy`，并说明 S3 版本寻址不受支持。[官方处理实现](https://github.com/supabase/storage/blob/master/src/storage/protocols/s3/s3-handler.ts) 将普通复制源条件传给后端，但不提供目标条件或条件删除保证。

- 对不超过现有单次复制上限 5 GiB 的新任务，只要用户明确允许目标覆盖，就继续使用带源 ETag 条件的单次 CopyObject，不再仅因可选分片阈值而失败。
- 不把响应中的任意 VersionId 当成可寻址的固定版本，也不向单次 Supabase 复制发送它。
- 超过 5 GiB、已有分片断点、服务端要求改用分片、非覆盖目标条件、条件删除仍不受支持。不会退回无条件分片，也不会在自动移动失败后删除源对象。大对象可另行下载、上传并人工核对后显式删除源对象。
- 未用真实 Supabase 账户进行验证；该变化依据官方条件协议并由记录客户端测试覆盖。不能把它描述成 Supabase 分片复制或自动移动已经恢复。

## 本轮测试范围

修复空对象与原有分片复制的 Expires 处理：读取 SDK 实际反序列化的 `ExpiresString`，保留原始 Header；缺失值保持缺失，不经过 SDK 会写入 year-0001 的 nullable Expires setter。

`ConditionalCopyDestinationTests` 覆盖空对象创建/替换、源 ETag/版本/长度/实际响应/Metadata 异常、目标冲突无回退、不开分片的空对象路径、Supabase 阈值边界和旧分片断点、服务端要求分片、源冲突及禁止移动。

`ConditionalWriteIntegrationTests.EmptyCreateOnlyCopyPreservesPropertiesAndRejectsExistingDestination` 覆盖隔离 MinIO 的真实空对象内容、Headers/Metadata/Tags 保留、已有目标保护、陈旧 ETag 拒绝和当前 ETag 替换。上游 quorum 测试与现有客户端集成套件继续独立验证。

本地云工作区没有 .NET SDK；静态检查不等于运行测试。最终构建、单元测试、上游 quorum 测试与 MinIO 集成结果以对应提交的 CI 为准。

## MinIO 与未验证服务的 Key 身份保护

固定源码揭示了必须在客户端请求发送前阻止的服务端差异，而不是签名 URI 编码问题：

- [`cmd/utils.go` 的 `trimLeadingSlash` / `unescapePath`](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/utils.go#L730-L759) 会先对以 `/` 开头的解码 Key 执行 `path.Clean`，再去掉开头斜线。因此 `/identity//file` 可变成现有的 `identity/file`，HTTP 读写、删除和 CopySource 不能据此保证原始 Key 身份。
- [批量删除](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/bucket-handlers.go#L456-L460) 也会改写 XML 正文中以 `/` 开头的 Key。签名正确、单次 HEAD 成功或 URI 保留原文，都不能阻止删除落到别名对象上。
- [对象名校验](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/object-api-utils.go#L177-L210) 拒绝内部 `//`；[路径段校验](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/generic-handlers.go#L308-L342) 拒绝以 `/` 或反斜线分隔的 `.` / `..` 段，并去掉段两侧 ASCII 空白后比较。内部 `//` 单独出现时是拒绝，不应描述为所有情况下都会规范化。

`MinioObjectKeyPolicy` 对 MinIO、Custom 及未知枚举类型的上述 Key 返回明确、不可重试的验证错误，既不替换 Key 也不继续发送请求。Custom 错误说明服务能力尚未验证，不声称它一定是 MinIO。正常单尾斜线 `folder/`、纯空白、`...`、字面的 `%2F`、普通反斜线及 Unicode 空白名称不因本规则被改写或拒绝。列表 Prefix 保持原样，允许发现原始名称。

工厂客户端在序列化前覆盖对象读写、删除、Tagging、ACL、Retention、LegalHold、恢复、选择内容及分片请求，CopyObject / CopyPart 检查源与目标；预签名入口单独检查。服务层额外在 Copy/Move 读取源前检查两端，在整批选中删除及版本删除开始前检查全部 Key，避免先删除前 1000 项才发现第 1001 项不支持。清空存储桶会在已收集的版本列表及每个随后发现的对象页删除前检查；它不是跨全部页面的事务。

显式 Amazon S3、Cloudflare R2 及其他已声明 Provider 保留原来的 Key 规则和严格 wire 身份测试。本策略依据用户选择的服务类型，不从 `Server` Header 猜测后端；将实际 MinIO 错配为其他 Provider 会绕过这个 MinIO/未验证服务保护，应正确设置连接类型。此改动不证明其他兼容服务支持每个 AWS 合法 Key。

`MinioExactKeyIdentityIntegrationTests.UnsupportedMinioKeysAreRejectedWithoutReadingOrMutatingAliases` 在随机隔离桶内创建 `identity/file` 和 `file` 两个不同内容的真实对象，对不支持的 Key 检查上传、HEAD、下载、复制两端、单删和混合批删都拒绝，再通过真实 HEAD/下载/列表证明别名内容与数量不变；正常 Key 的 create-only 复制及冲突继续验证。测试总执行预算两分钟，失败阶段保留，清理独立限时 30 秒且只使用真实合法 Key。它验证客户端拒绝和别名保全，不假称 MinIO 能存储互相独立的前导/重复斜线 Key。

旧集成测试曾把不可表示的 Key 和其规范化别名一起传给清理批删，运行 6 分 42 秒后在传输超时处失败，并掩盖本体异常。固定源码中[批删先记录校验错误但仍收集锁](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/erasure-server-pool.go#L1241-L1262)，[锁名称再经路径拼接](https://github.com/minio/minio/blob/18f97e70b19db6b2940ae8a1b14c4665d2eeca36/cmd/namespace-lock.go#L231-L266)，与原始字符串去重及规范化后锁名称重复造成等待的解释一致；这是源码支持的诊断推断，不是已取得服务端堆栈证明的根因。新测试不发送此类危险清理请求，不以延长超时掩盖问题。
