# 编辑与 CDN 修复边界（2026-11-07）

本页描述本轮源码修复，不代表已经发布；正式发布以 tag 和 Release 为准。

## 原地与批量 Header / Metadata

普通对象可在属性页选择“保存当前对象”，也可选中多个文件使用“批量 Header / Metadata”。固定版本另存入口继续保留，原有的 AWS 固定版本、SSE-S3、BucketOwnerEnforced、无 Object Lock 限制不变。

原地保存支持：

- Amazon S3：当前源 ETag 条件和目标 If-Match；BOE Bucket 不写 ACL，其他 Bucket 读取并重用对象 ACL grants。
- Cloudflare R2：当前源 ETag 条件和 R2 专用目标条件 Header；按 R2 自动加密语义保存，不发送其不支持的 SSE-S3/Tagging 设置。
- MinIO：当前源 ETag 条件、private ACL 语义和保存前检查，不声称服务端原子检查 Metadata。
- 标准 Custom：要求能读取完整 ACL grants，并明确确认 Bucket 没有 Object Lock。API 缺失、拒绝或状态不明均停止，不把错误当作“无保护”。

后续独立增量已为阿里云 OSS、腾讯云 COS、Backblaze B2、Google Cloud Storage、Supabase 增加专用路径，不再整类禁用。各家 Endpoint、版本/WORM、ACL、Header 与并发风险有不同实际限制，详见 [专用服务商 Metadata 扩展](Metadata-Provider-Expansion-2026-11-07.md)。旧交付包不因此改变。

普通 S3 路径保存前两次读取当前对象，比较内容身份、版本、Header、Metadata 和已暴露的保护属性；检测到变化立即停止，要求刷新重新编辑。复制不指定历史 SourceVersionId，不会从旧版本回滚当前内容。批量按对象处理，独立报告成功与失败，不回滚已成功对象。

这些检查不是完整属性事务。ETag 不反映 Metadata/Tags/ACL 变化，检查后仍可能有并发修改，确认框会说明此窗口并建议暂停其他写入者。ACL grants 保留不保证对象所有者不变；所有者可能变为执行写入者。新版本和请求可能产生费用。发出写请求后超时、取消或冲突不能认定未写入，需先重新读取核对，不自动降级为无条件覆盖。

通用路径仍限制对象大小不超过 5 GiB（B2 为 5 GB）；KMS、SSE-C、Object Lock/Retention/Legal Hold、缺失 Metadata、无法解析的 Expires 不能在此流程无损保存。需要的 ACL/Lock 读取权限缺失会给出具体错误，而不是继续写入默认值。

## 文本预览与回传

- 普通对象不再按服务商白名单一律只读。AWS、MinIO、R2 小文本使用条件写入；其他服务商在用户确认后先重新检查内容身份、Header/Metadata 和可读取的 Tags，再按普通上传保存。后者有检查后的并发覆盖窗口，确认框明确说明。
- 既有 Tag Key/Value 和 Metadata 值不裁剪空白，`team` 与 ` team` 不再被误合并。验证仍执行长度、重复键与 Header 注入检查；普通新上传的手工输入规范化不变。
- 保留 Content-Language、网站重定向与原有内容 Header；支持 identity、gzip、br、deflate 及组合编码。解压每层都有大小上限，编辑后按原编码重新压缩，不能把明文配上 gzip Header 上传。
- 文本仍限 2 MiB，图片保持原有大小/像素上限。未知编码、KMS/SSE-C、Object Lock、缺失 Metadata、无有效对象身份或无损无法解析的 Expires 继续拒绝回传。AES256 的显式保留仅在支持的服务商路径启用；R2 使用服务端自动加密。
- ACL 等独立配置不属于文本编辑器的保留承诺，替换写入前会明确提示按服务端规则处理。失败、冲突或未确认上传的本地编辑继续保留。

## CDN 精确对象身份

CDN 映射不再把反斜杠改成斜杠或删除 Key 开头的 `/`。源前缀按原始值匹配，解析结果仍携带原始 Key，路径逐段编码，保留重复斜杠、空白、Unicode、字面的 `%`、`?` 和 `#`。

`/a.txt` 与 `a.txt`、`a\b.txt` 与 `a/b.txt` 不会再得到同一个映射 URL。浏览器会折叠的独立 `.` / `..` 路径段明确拒绝生成 CDN 链接，提示使用对象存储下载；不会静默预热、下载或刷新另一个对象。CDN/源站自己的额外 URL 规范化设置仍需由用户配置正确。

## 验证

本轮运行通过 Core 聚焦 50 项、S3 聚焦 71 项测试，包含 exact-key URL、空白标签/Metadata、单次及分片上传实际 SDK Header、压缩往返与展开上限、当前版本/同 ETag 属性冲突、ACL 保留、目标条件和未知保护状态。完整验收数量以最终集成测试结果为准。

Windows UI 布局/交互测试已补充但本轮 Linux 环境不执行 Windows UI；没有对生产 S3/CDN 做写入验证，不把 mock 协议覆盖等同线上厂商实测。
