# 专用服务商 Metadata 编辑扩展（2026-11-07）

这是已交付审计修复之后的独立源码增量；不会追溯修改旧交付 ZIP，也不表示已经发布或完成生产账户验收。以下功能适用于单个对象的“保存当前对象”和批量 Header / Metadata，固定版本另存流程不变。

## 支持方式

| 服务商 | 实现与保留方式 | 仍需满足的实际条件 |
| --- | --- | --- |
| Google Cloud Storage | XML 同 Key 复制，源和目标同时携带 generation / metageneration 条件；保留自定义 Metadata、内容 Header、Storage Class、Custom-Time，成功后核验代际与完整属性。 | 必须读取到有效代际；ACL 必须是可精确表达的 private/public-read/authenticated-read，或明确启用统一 Bucket 访问。自定义 ACL、KMS/CSEK、已暴露 Retention 等不写入。 |
| Backblaze B2 | 当前源同 Key 复制，写前完整快照检查，源 ETag 条件及写后 Header / Metadata 回读；不发送 B2 不支持的对象 Tags/独立 ACL。显式 SSE-B2 使用 AES256。 | 需明确无 Bucket Object Lock；无权限、未知错误或未知配置不会视为无锁。单次复制 ≤5,000,000,000 字节，不自动转为分片。SSE-C、Retention、网站重定向不支持。 |
| Supabase Storage | S3 同 Key Copy，完整快照检查及写后回读。开放 Content-Type、Cache-Control、Content-Encoding 和完整可读取的字符串 Metadata。 | 当前服务端源码未完整传递 Content-Disposition/Content-Language/Expires/网站重定向；相关编辑控件禁用，含这些不可保留状态的对象明确停止。HEAD 会遗漏非 ASCII Metadata，因此相关值不写入。 |
| 腾讯云 COS | 原生 HTTPS 请求和官方纯离线签名器，原生 ACL XML、Tags、HEAD 双读；显式重放精确 ACL grants/继承状态、Tags、Storage Class、SSE-COS 与内容 Header。 | 仅标准区域 Endpoint 与 Auto/VirtualHosted 寻址。自定义 Host/路径/证书例外、不完整原生属性、KMS/SSE-C、特殊对象类型或未知/已启用 Object Lock 不写。SDK 会规范化的特殊 Key 和不能无损发送的非 ASCII Header 明确停止。 |
| 阿里云 OSS | 严格 HTTPS 原生请求和 OSS V4 签名，原生 HEAD/ACL/版本与 WORM 检查；保留原生 ACL（包括 default 继承）、Storage Class、SSE-OSS、Metadata 和内容 Header。 | 仅标准区域 Endpoint；当前要求 Bucket 从未开启版本控制且明确无 Bucket WORM。版本 Enabled/Suspended、Object WORM、Appendable/Symlink、KMS/SSE-C、无法保留的扩展属性不写。 |

OSS/COS 不使用其 SDK 网络客户端：固定版本官方 SDK 的网络传输实现包含全局证书校验绕过。本增量仅离线使用协议 DTO / 签名辅助，网络使用默认严格 TLS 的 `SocketsHttpHandler`，禁止自动重定向，不修改全局 TLS 回调，不绕开系统代理。

## 冲突与未知结果

- GCS 使用源和目标两套代际条件，不使用历史源版本重放当前 Key；条件失败后不降级为普通 PUT。
- 其他服务商使用当前源、保存前检查和其可用条件，但不能把 ETag 当作 Metadata/ACL/Tags 的事务版本号。GCS 非 MD5 对象的 ETag 也可能随 Metadata 改变，写后内容身份按 generation 核验，不错误要求 ETag 不变。用户确认框说明非原子窗口、所有者变化与费用，建议停止其他写入者。
- Supabase 服务端会先固定源内容版本，再覆盖目标数据库记录。S3 Copy 没有目标版本 CAS，因此并发上传的新文件内容也可能被覆盖，不能只把它描述成属性冲突；客户端双读与写后回读无法消除该窗口。
- B2 未显式报告源加密时可能采用 Bucket 当前默认加密。B2 对象访问由 Bucket 权限控制；Supabase 对象 owner 可能变成当前 S3 身份，访问继续由 Bucket/RLS 控制。
- 批量保存固定原连接和 Bucket，逐对象检查、逐对象报告，不因用户切换导航而写往另一个连接。不回滚已成功项。
- Metadata 写入在创建 AWS SDK client 前设置零重试；OSS/COS 原生 transport 不重试。写请求只发一次，不自动重试、分片降级或删除重建。超时、取消、服务端错误、响应不可验证或写后属性不一致都不能假装“未写入”；先重新读取核对。

## 验证范围

测试使用固定响应/假 HTTP 传输、实际 SDK 序列化与离线签名。覆盖普通路径、保护属性保留、冲突、无权限、未知结果、精确 Key、配置拒绝与 TLS 回调不变。Linux 环境可以编译 Windows UI，但未执行 Windows 窗口交互。本轮没有生产凭据，没有对真实 OSS/COS/B2/GCS/Supabase 写入；模拟协议通过不等于所有厂商部署版本在线验收。

最终验证：锁文件模式还原全部 14 个项目；全解决方案构建 0 警告、0 错误。Core 524 通过；S3 818 通过、13 项真实服务集成测试因未配置环境跳过；CDN 40 通过；Configuration 34 通过、1 项 Windows DPAPI 测试跳过。合计 1,416 通过、14 跳过。App / CLI 的 Windows 测试项目仅交叉编译，未运行。

## 官方协议与源码依据

- GCS：[XML Copy](https://docs.cloud.google.com/storage/docs/xml-api/put-object-copy)、[对象复制条件](https://docs.cloud.google.com/storage/docs/request-preconditions#object_copy_preconditions)、[Header 语义](https://docs.cloud.google.com/storage/docs/xml-api/reference-headers)、[统一 Bucket 访问](https://docs.cloud.google.com/storage/docs/using-uniform-bucket-level-access)、[AWS 签名的 ACL 响应格式](https://docs.cloud.google.com/storage/docs/aws-simple-migration#access_control_in_a_simple_migration_scenario)、[ETag 语义](https://docs.cloud.google.com/storage/docs/metadata#etags)。
- B2：[S3 CopyObject](https://www.backblaze.com/apidocs/s3-copy-object)、[S3 兼容 API](https://www.backblaze.com/docs/cloud-storage-s3-compatible-api)、[Object Lock](https://www.backblaze.com/docs/cloud-storage-enable-object-lock-with-the-s3-compatible-api)。文档未充分说明未配置 Lock 的所有返回形态，本实现只接受明确的已知无配置结果，其他情况报告具体错误。
- Supabase：[兼容性说明](https://supabase.com/docs/guides/storage/s3/compatibility)、[固定源码 s3-handler.ts](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/protocols/s3/s3-handler.ts)、[复制实现 object.ts](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/storage/object.ts)、[Copy 路由](https://github.com/supabase/storage/blob/e7e8656660760776f3ddfc73982820ac57f08be4/src/http/routes/s3/commands/copy-object.ts)。自托管版本可能不同，因此成功后再次读取核对。
- OSS：[CopyObject](https://www.alibabacloud.com/help/en/oss/developer-reference/copyobject)、[GetObjectACL](https://www.alibabacloud.com/help/en/oss/developer-reference/getobjectacl)、[GetBucketWorm](https://www.alibabacloud.com/help/en/oss/developer-reference/getbucketworm)、[Object WORM](https://www.alibabacloud.com/help/en/oss/user-guide/object-level-retention-policy-object-worm)。
- COS：[CopyObject](https://intl.cloud.tencent.com/ind/document/api/436/10881)、[GetObjectACL](https://cloud.tencent.com/document/product/436/7744)、[GetBucketObjectLockConfiguration](https://cloud.tencent.com/document/api/436/7730)、[Object Lock 限制](https://cloud.tencent.com/document/product/436/55294)。
