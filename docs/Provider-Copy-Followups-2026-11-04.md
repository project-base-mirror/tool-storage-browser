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
