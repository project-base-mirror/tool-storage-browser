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

## R2 保留源对象的条件复制恢复

2026-11-04 核对 [R2 官方扩展说明](https://developers.cloudflare.com/r2/api/s3/extensions/#conditional-operations-in-copyobject-for-the-destination-object)及其[固定文档提交 `731a8bf1c896051aeb4b11b6d3b8e4c897bd90e8`](https://github.com/cloudflare/cloudflare-docs/blob/731a8bf1c896051aeb4b11b6d3b8e4c897bd90e8/src/content/docs/r2/api/s3/extensions.mdx)：R2 已提供仍标为 beta 的目标条件复制 Header。不能将 AWS 的普通 `If-Match` / `If-None-Match` 当作 R2 CopyObject 的目标条件。

- 新 R2 复制使用 `x-amz-copy-source-if-match` 选择源快照；目标不存在策略使用 `cf-copy-destination-if-none-match: *`，明确的条件替换使用 `cf-copy-destination-if-match`。这些 Header 由已固定版本的 AWS SDK 在签名前序列化，不在 HTTP 发送后改写。
- 官方说明明确指出这两组条件 **不构成两个 Key 的共同原子事务**：源条件在选取复制对象时检查，目标条件在提交目标时检查。源选择后若源内容再次改变，目标仍可得到先前选中的快照。本功能不保证复制返回时源仍是该快照；绝不自动删除源，也不允许源和目标为同一 Key。
- 路径保持为单次 CopyObject，客户端上限仍为 5 GiB。低于或等于上限的对象不因可选分片阈值转成 UploadPartCopy；超过上限、旧分片断点和自动移动均拒绝。R2 不提供 S3 版本寻址，不使用偶然出现的 VersionId 绕过限制。服务端若拒绝单次复制或要求分片，不移除条件，也不退回分片。
- 明确设置 `x-amz-metadata-directive: COPY`，让服务端复制它支持的内容 Headers 与自定义 Metadata，不将 HEAD 读到的子集重新写入。特别是 R2 的 Unicode Metadata Key 可能不在 S3 HEAD 响应中出现。此处不是原地 Metadata 编辑，也不声称保留不受支持的 ACL、Object Lock、Tags 或 Website Redirect。
- 409 / 412 会按不可重试的冲突呈现。SDK 对暂时服务错误的重发仍带相同源条件和目标条件；首次写入成功但响应丢失后，非覆盖重发遇到目标存在会返回冲突，应用不会把它改成覆盖或当作已确认成功。源对象保留供核对。

`R2ConditionalCopyProtocolTests` 使用生产工厂与 AWS SDK 的实际序列化、签名、HTTP transport，覆盖 0 字节至 5 GiB、阈值、禁用分片、来源 ETag 缺失/变化、源选定后变化、目标并发创建/替换、409/412、响应丢失后的条件重发、元数据 COPY、精确 Key、伪 VersionId、超限、旧断点与禁止移动。测试的服务端是隔离协议模型，**不是 Cloudflare R2 实例**；模型用于检查客户端所发请求与状态处理，不能证明托管服务原子性。托管 R2 尚未实测，最终客户端测试结果以对应提交的 CI 为准。

## Supabase 进一步恢复的边界

额外核对官方仓库固定提交 [`e7e8656660760776f3ddfc73982820ac57f08be4`](https://github.com/supabase/storage/tree/e7e8656660760776f3ddfc73982820ac57f08be4)。源码与托管部署版本可能不同，以下为设计证据，不是已验证的账户能力。

1. [S3 `copyObject`](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/protocols/s3/s3-handler.ts#L1434-L1491) 固定 `upsert: true`，只传递源 `CopySourceIf*` 条件。[`putObject`](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/protocols/s3/s3-handler.ts#L961-L999) 和 [`completeMultiPartUpload`](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/protocols/s3/s3-handler.ts#L727-L829) 不处理目标 If-Match / If-None-Match，最终按覆盖语义完成。因此先 HEAD 再写入、客户端 GET→PUT 或 GET→UploadPart 都无法补出原子目标不存在条件；S3 条件删除也不能由客户端补成事务。
2. [S3 GET](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/protocols/s3/s3-handler.ts#L1185-L1260) 确实转发 If-Match 和 Range，因此受控分段中转可以保护内容身份，并可设计为限定内存的可恢复上传。但本固定实现的 GET/PUT/MPU 没有完整往返 Content-Disposition、Content-Language、Expires，不能承诺与普通复制等价的属性保留。还会增加客户端网络与可能的暂存盘开销。本轮不静默加入这样的中转降级，不把它命名为移动。
3. 原生 REST 有不同且更强的服务端实现。[`moveObject`](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/object.ts#L1212-L1434) 先复制后端固定内容，再锁住两端对象，重新核对来源 id/version、权限与目标不存在后变更数据库。`sourceVersionId` 是所选源版本，不是可以直接等同于“当前 Key 未改变”的通用 CAS Header；不同版本状态有不同删除/提升语义，后续适配必须单独验证。
4. REST `/copy`、`/move` 经由[独立 JWT 认证注册](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/http/routes/object/index.ts)，其 [JWT 插件](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/http/plugins/jwt.ts) 验证 Bearer JWT，不把 SigV4 当作 REST 登录。现有配置只描述 S3 密钥/SessionToken，没有声明 REST endpoint、JWT 类型、账户绑定或 REST 权限。不能将普通 S3 SecretKey 或任意 SessionToken 自动发送到另一个 REST URL。
5. [官方认证说明](https://supabase.com/docs/guides/storage/s3/authentication) 确实另有 `project_ref + anonKey + user JWT` 的 S3 SessionToken 模式，这可能为未来显式配置的 REST adapter 提供同一身份，但需用户确认配置、用途与新鉴权发送，并验证 endpoint/account 绑定、RLS、过期与安全保存。它不适用于自动猜测普通 S3 access-key pair。
6. [官方复制/移动管理文档](https://supabase.com/docs/guides/storage/management/copy-move-objects) 将原生 API 复制与移动均限制为 5 GB，并明确新对象所有者会变为发起人。因此 REST 不是已证实的大对象逃生路径，也不能悄悄替换 S3 移动。应用当前仅给出外部手动原生 API 的替代提示，不新增凭据、不调用 REST、不改变所有权。

当前可用路径保持为：≤5 GiB 的 S3 源条件单次复制需用户明确允许目标覆盖，且保留源；需要大对象时另行下载与上传，人工核对内容、属性及目标覆盖选择；需要原生复制/移动时使用明确配置的 Supabase 工具并检查其权限、所有权及大小限制。MinIO/Custom 的已知 Key 别名保护继续保留，MinIO 公开访问仍通过 Bucket Policy 配置，不伪装成公开 ACL。
