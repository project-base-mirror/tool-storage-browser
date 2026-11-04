# 精确 Key 的预签名链接和重定向

本实现针对锁定的 `AWSSDK.S3 4.0.102` 和 `AWSSDK.Core 4.0.100.9`。它修复 SDK 路径组合的差异，不改变 S3 Key，也不在签名后修改 URL。

## 支持范围

- 以 `/` 开头的普通 Bucket 对象可生成 GET/HEAD 预签名链接，支持 path-style、virtual-hosted、Endpoint 基础路径和非默认端口。
- 使用 SDK 自带的 Endpoint Resolver、`AWS4PreSignedUrlSigner` 和凭据刷新；支持 session token、VersionId、响应 Header 覆盖、自定义已签名查询参数，以及最多 7 天的 SigV4 有效期。
- 签名前将 Endpoint 路径移到 SDK ResourcePath 标签，使签名路径与实际发送路径一致。失败退出时恢复原 Endpoint 和模板，让 SDK 的 Bucket 区域识别及重试继续看到正确 Bucket。
- 临时重定向保留请求方法、原始 Key、查询参数和可重放请求体，再由 SDK 重新签名。HTTP 客户端自身的自动重定向始终关闭。

## 必须保留的限制

- 含独立 `.` 或 `..` 路径段的 Key 不生成浏览器下载链接。普通浏览器/URI 消费者会合并这些路径段；应用内请求仍使用不规范化的 URI。字面 `%2e` Key 会编码为 `%252e`，不属于独立点段。
- 以 `/` 开头的 Key 的预签名 PUT/DELETE、上传分片、请求 Header/Metadata、加密 Header、访问点/ARN 和 S3 Express 目录 Bucket 尚不支持。不得静默忽略这些选项。正常 Key 仍走 SDK 原生预签名方法。
- MinIO 和未经验证的 Custom 服务继续拒绝其服务端可能规范化为别名的特殊 Key；客户端正确签名无法消除服务端别名行为。
- 重定向不能更改协议、端口、原始查询参数或对象身份，不能带用户信息、fragment、非法转义或空白。配置自定义 Host Header 的连接不自动重定向。
- 同一 authority 的兼容服务重定向仅在路径和查询参数完全相同时接受。跨 authority 只接受 Amazon S3 同一分区、同一 Bucket 的已知标准区域 Endpoint，支持 path-style 与 virtual-hosted 切换；不接受任意 S3 形状的域名、跨 Bucket、自定义服务跨域、未知区域或高级 Endpoint 变体。最多跟随三次 307，避免循环。

## 自动化验证

`ExactObjectKeyTransportTests.cs` 和 `ExactObjectKeyLinkTests.cs` 使用锁定 SDK 和内存 HTTP transport，检查 SDK 最终交给 HTTP 的路径、方法、查询参数、Host、session token 和响应体释放。测试用独立 HMAC-SHA256 实现重建 SigV4 canonical request，不调用 SDK 签名验证函数。覆盖预签名 GET/HEAD、特殊字符和百分号、浏览器普通 URI 消费、Endpoint 基础路径、307 区域及寻址风格切换、PUT 请求体重放、COPY/DELETE、恶意 Location、重定向循环、临时失败重试和区域纠正重试。

这些是无真实凭据、无网络访问的协议回归测试，不代表已对真实 AWS/R2 等服务完成集成验收。测试是否通过以对应提交的 CI 结果为准。

## 实现依据

- [S3 4.0.102 官方预签名流程](https://github.com/aws/aws-sdk-net/blob/da5f28f3e93dc1f2ecd101e540aa46fdc36b9c2c/sdk/src/Services/S3/Custom/AmazonS3Client.Extensions.cs)
- [Core 4.0.100.9 官方 SigV4 预签名器](https://github.com/aws/aws-sdk-net/blob/1170671947ab162995db95f69b2cfcddd0803fcb/sdk/src/Core/Amazon.Runtime/Internal/Auth/AWS4Signer.cs)
- [Core 4.0.100.9 官方重定向处理器](https://github.com/aws/aws-sdk-net/blob/1170671947ab162995db95f69b2cfcddd0803fcb/sdk/src/Core/Amazon.Runtime/Pipeline/Handlers/RedirectHandler.cs)
- [S3 4.0.102 官方区域纠正逻辑](https://github.com/aws/aws-sdk-net/blob/da5f28f3e93dc1f2ecd101e540aa46fdc36b9c2c/sdk/src/Services/S3/Custom/Util/BucketRegionDetector.cs)
