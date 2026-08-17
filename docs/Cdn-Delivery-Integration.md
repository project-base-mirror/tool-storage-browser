# CDN 内容交付集成设计与实施计划

本文定义 S3 Explorer 的 CDN / 内容分发能力边界、当前实现、Provider 扩展方式和自动化路线。CDN 与对象存储有关联，但不是对象存储连接本身：对象存储负责原始对象和 S3 API，CDN 负责公网交付 URL、缓存刷新、缓存预热和下载质量探测。

## 1. 产品边界

### 1.1 目标

- 为一个对象存储连接下的 Bucket 或对象 Key 前缀关联一个或多个 CDN。
- 根据当前对象生成稳定的 CDN URL，并支持复制和使用系统浏览器打开。
- 使用真实 HTTP GET + Range 请求测试 CDN 下载状态、响应头耗时、吞吐和缓存 Header。
- 通过 HEAD、Range GET 或完整 GET 执行通用 HTTP 预热。
- 通过用户配置的 HTTP 端点、方法和 Body 模板手动提交缓存刷新。
- CDN 与对象存储通过统一 Credential Vault 按 Provider/类型引用凭据；兼容的 Alibaba Cloud AccessKey 可以共享，其他协议保持隔离。
- 通过 Provider 扩展点接入原生控制面；当前已实现 Alibaba Cloud CDN，CloudFront、Cloudflare 与腾讯云仍属后续范围。
- 不改变现有 S3 下载、预签名 URL、上传队列和对象管理语义。

### 1.2 非目标

工具不创建或修改 CDN 分发、源站、证书、DNS、WAF、缓存规则、计费和日志服务。上传完成后的刷新/预热只对用户显式启用的 Bucket/前缀关联执行；完整厂商控制台能力不在范围内。

## 2. 为什么 CDN 不属于 S3 连接字段

同一个 Bucket 可能同时通过 CloudFront、Cloudflare、自建 Nginx 或多个地域域名交付；一个 CDN 也可能只覆盖 Bucket 中的某个前缀。S3 Endpoint、签名 Region 和地址风格描述的是对象存储 API，不能用来表达 CDN 的公网域名、刷新 API 和独立权限。

因此关系模型是：

```text
对象存储连接
  └─ Bucket
      ├─ 前缀 assets/ → CDN A（默认）
      ├─ 前缀 assets/ → CDN B
      └─ 前缀 downloads/ → CDN C
```

## 3. 需求与提交边界

### 需求 A：可扩展 CDN 基础能力

验收：

- CDN 配置、统一凭据引用、Bucket/前缀关联有稳定 Core 模型。
- 对象存储、CDN 配置与凭据由统一配置存储原子持久化；秘密值不以明文落盘。
- URL 映射使用最长前缀规则，支持一个范围关联多个 CDN 和一个默认 CDN。
- 下载探测、HTTP 预热和通用刷新均可独立测试。
- 通用 HTTP 基础能力不依赖特定云厂商 SDK；原生 Provider 将 SDK 依赖隔离在 `Infrastructure.Cdn`。

最终提交：`feat(cdn): add profiles bindings and generic delivery services`

### 需求 B：桌面交付工作流

验收：

- 顶级“CDN / 分发”菜单提供配置、URL、下载测试、预热和刷新。
- Bucket 右键可直接打开关联管理；对象右键可执行交付动作。
- CDN 配置中心统一管理配置、Credential Vault 和关联。
- 命令按当前对象是否存在有效关联、是否支持刷新自动启用。
- 大字体和最小窗口尺寸下主要按钮可见、可读。
- README、路线图和本文档反映真实实现与后续边界。

最终提交：`feat(ui): integrate generic CDN delivery workflows`

## 4. 架构

```text
S3Explorer.Core
  CdnModels.cs
    CdnProfile / CredentialProfile / CdnBinding
    IExplorerConfigurationStore
    ICdnDeliveryService
    CdnUrlMapper / CdnConfigurationValidator

S3Explorer.Infrastructure.Cdn
  GenericHttpCdnDeliveryService.cs
  AliyunCdnProvider.cs

S3Explorer.Infrastructure.Configuration
  ExplorerConfigurationStore.cs
  ExplorerConfigurationAdapters.cs

S3Explorer.App
  CdnConfigurationDialog.cs
  CdnDownloadTestDialog.cs
  MainForm.Cdn.cs
```

`S3Explorer.Infrastructure.S3` 不引用 CDN。通用 CDN 基础设施只依赖 Core，后续厂商适配器可以继续放在 `Infrastructure.Cdn` 中，或者在 SDK 依赖明显增大时拆为独立程序集。

## 5. 数据模型

### 5.1 CdnProfile

CDN 配置可填写最多 2000 个字符的非敏感备注，用于记录用途、负责人、域名变更窗口等运维信息。备注保存在统一加密配置中，并随连接包迁移；不要在备注中填写 Token、Header secret 或其他凭据。

关键字段：

- `Id`、`Name`、`Enabled`
- `ProviderId`：`generic-http` 或 `aliyun-cdn`
- `BaseUrl`：对象交付基础地址
- `CredentialId`：可选统一凭据引用
- `WarmupMode`：HEAD、Range GET、完整 GET
- `WarmupRangeBytes`
- `TimeoutSeconds`、`FollowRedirects`
- `PurgeEndpointTemplate`、`PurgeHttpMethod`
- `PurgeBodyTemplate`、`PurgeContentType`

能力由配置推导：所有通用 HTTP 配置支持 URL、探测和预热；只有设置刷新端点后才声明 `Purge`。

### 5.2 CredentialProfile

认证方式由统一 `CredentialProfile` 的类型决定：

- 无认证
- `Authorization: Bearer <token>`
- 自定义 Header
- Alibaba Cloud AccessKeyPair

通用 HTTP 使用 Bearer Token 或自定义 Header；Alibaba CDN 控制面使用 Alibaba Cloud AccessKeyPair，同一个凭据可以被 Alibaba OSS 连接和 Alibaba CDN Profile 共享引用。该 AccessKey 不会发送到 CDN 交付域名；其他 S3 Access Key 也不会自动复用为 CDN Token。

### 5.3 CdnBinding

- `StorageProfileId`
- `Bucket`
- `SourcePrefix`
- `CdnProfileId`
- `CdnPathPrefix`
- `IsDefault`
- `Enabled`

对象存储连接 ID 和 Bucket 共同确定源范围。连接重命名不会破坏关联；删除连接后关联会显示缺失，保存配置前校验会阻止提交无效引用。

## 6. URL 映射

对当前对象：

1. 筛选相同 `StorageProfileId` 和 Bucket 的启用关联。
2. 规范前缀：反斜杠转正斜杠、去掉开头 `/`、非空前缀补结尾 `/`。
3. 保留 `ObjectKey.StartsWith(SourcePrefix)` 的关联。
4. 只使用最长源前缀的一组关联。
5. 默认关联排在同组第一位。
6. 从 Object Key 移除源前缀，前置 `CdnPathPrefix`。
7. 每个路径段独立 URL 编码，保留路径层级。

示例：

```text
BaseUrl:       https://cdn.example.com/base
SourcePrefix:  assets/
CdnPathPrefix: static/
ObjectKey:     assets/js/app 1.js
结果:          https://cdn.example.com/base/static/js/app%201.js
```

## 7. 配置与安全

当前统一数据位置：

```text
%APPDATA%\S3Explorer\configuration.json
```

自动化模式使用隔离数据目录中的同名文件，不读取真实用户配置。

`configuration.json` 整体使用 Windows DPAPI CurrentUser 保护，其中保存 CDN 配置、凭据和对象存储连接的统一图。旧的 `cdn-config.json` 与 `cdn-credentials.json` 仅在首次迁移时读取，随后移动到 `legacy-archive`，不会作为运行时回退。

安全约束：

- 不自动复用 S3 SecretKey。
- 日志不记录 CDN Secret、Authorization Header 或自定义鉴权 Header。
- 通用刷新响应只在对话框中显示最多 4096 字符摘要；日志只记录状态、耗时和字节数。
- 下载测试和预热仅接受 HTTP/HTTPS 绝对 URL。
- 基础 URL 不允许内嵌用户名密码、查询参数或片段；鉴权秘密不允许换行，自定义 Header 名称必须符合 HTTP token 规则。
- 配置与凭据 JSON 只读取当前明确支持的文档版本，未知版本会停止加载而不是静默误读。
- 超时、Range 大小和 HTTP 方法在本地校验。
- 删除仍被 CDN Profile 或对象存储连接引用的凭据会被阻止。
- 同一连接、Bucket、源前缀只能有一个默认 CDN。

## 8. 桌面工作流

### 8.1 CDN 配置中心

配置中心包含以下区域：

1. **CDN 配置**：域名、预热行为、刷新端点、凭据引用和超时。
2. **凭据中心**：统一管理对象存储与 CDN 的 Access Key、Bearer Token、自定义 Header 等凭据。
3. **Bucket / 前缀关联**：源连接、Bucket、源前缀、CDN、目标路径前缀和默认项。

保存时统一运行模型校验；失败时不覆盖磁盘配置。配置、引用和凭据作为一个图原子保存，不会让配置引用尚未落盘的秘密。

### 8.2 主菜单

```text
CDN / 分发
  CDN 配置中心...
  复制 CDN URL
  使用 CDN 打开
  CDN 下载测试...
  HTTP 预热
  刷新 CDN 缓存
```

对象动作只在当前连接、当前 Bucket 中恰好选择一个文件且存在有效默认关联时启用。刷新按钮还要求 Profile 声明 `Purge` 能力。

### 8.3 Bucket 右键

“CDN 关联...”打开配置中心，并把当前对象存储连接和 Bucket 作为新增关联的默认值。

### 8.4 对象右键

“CDN / 分发”子菜单包含复制、打开、测试、预热、刷新和配置入口，不替换原有对象 URL、S3 下载或预签名 URL。

### 8.5 HTTPS 证书有效期检测

CDN 配置列表对 HTTPS 基础 URL 提供“检测 HTTPS 证书”。检测直接连接配置域名并只执行 TLS 握手，不发送 HTTP 请求，也不携带 CDN Token、Header secret 或 S3 凭据。结果显示证书生效/到期时间、剩余天数、TLS 协议、主题、颁发者、SHA-256 指纹、域名匹配和证书链状态。

- 剩余不超过 30 天标记为“即将到期”；过期、尚未生效、域名不匹配和证书链不受信任分别显示。
- 即使服务端证书无效，诊断握手也会采集证书以解释问题；握手后立即断开，不发送应用数据。
- 单次检测最多等待 30 秒，并可从同一按钮取消；关闭配置窗口也会取消检测。
- 检测不查询 CRL/OCSP，不把瞬时结果写入配置；正式监控仍应由服务器或证书管理平台承担。

## 9. CDN 下载测试

测试使用 `GET` 和 `Range: bytes=0-N`，而不是只使用 HEAD。这样更接近真实交付路径，同时限制下载流量。即使服务端忽略 Range，客户端也只读取用户设定的样本字节数。

展示：

- HTTP 状态码和原因
- 最终 URL
- 响应头耗时（近似 TTFB）
- 总耗时
- 读取字节和平均吞吐
- Content-Type、Content-Length
- 所有响应 Header
- `CF-Cache-Status`、`X-Cache`、`X-Cache-Status`、`Age` 或 `Via` 中第一个可识别缓存状态

缓存命中判断只能作为 Header 提示，因为不同 CDN 使用不同格式；工具不把不存在标准的响应强行归一为 HIT/MISS。

## 10. 通用 HTTP 预热

模式：

- **HEAD**：流量最小，但部分 CDN 对 HEAD 和 GET 使用不同缓存路径。
- **Range GET**：默认模式，读取有限字节，适合作为通用第一阶段能力。
- **完整 GET**：确保拉取完整对象，但可能产生较高源站和 CDN 流量。

通用 HTTP 预热成功只表示请求完成且状态码为 2xx/3xx，不保证所有边缘节点已经缓存。Alibaba CDN 使用 `PushObjectCache` 提交原生预热，按最多 100 个 URL 分批并返回任务 ID；持久队列会通过 `DescribeRefreshTaskById` 查询状态。

## 11. 通用 HTTP 刷新

用户可以配置：

```text
Endpoint: https://api.example.com/purge?target={url}
Method:   POST
Body:     {"path":"{path}"}
Type:     application/json
```

端点中的 `{url}` 和 `{path}`使用 URL 编码；Body 中使用 JSON 字符串内容转义。2xx 表示提交成功。没有端点时，UI 隐藏能力并禁用刷新命令。

这是一种可配置适配层，不替代厂商签名逻辑。Alibaba CDN 使用 `RefreshObjectCaches` 原生刷新，按最多 1000 个 URL 分批；其他要求 HMAC、AWS SigV4、TC3 或 RPC 签名的 API 必须通过专用 Provider 实现，不能让用户把长期密钥拼进 URL 或 Body。

## 12. 对象操作与未来自动化矩阵

| 对象操作 | 建议 CDN 行为 |
| --- | --- |
| 上传新 Key | 可选预热；通常无需刷新 |
| 覆盖同一 Key | 刷新，成功后可选预热 |
| 删除对象 | 刷新旧 URL |
| 移动/重命名 | 刷新旧 URL，预热新 URL |
| 复制到新 Key | 可选预热新 URL |
| Metadata / Cache-Control 改动 | 刷新对应 URL |
| 批量上传 | 聚合、分批并按 Provider 配额提交 |

文件名带内容哈希的不可变资源通常只需预热。稳定 URL 的覆盖才需要刷新。

## 13. 持久任务与上传完成自动化

现有传输队列在任务首次进入 Completed 时发出完成事件，可作为生成 CDN 作业的触发信号，但不能直接在 UI 事件中调用外部 API。

v0.5.8 使用持久 `CdnJob` 队列：

```text
传输任务完成
  → CdnAutomationCoordinator 匹配 Binding
  → 写入持久 CdnJob
  → 后台 Provider 执行
  → 独立重试和状态展示
```

幂等键：

```text
TransferTaskId + CdnProfileId + Action + ObjectKey
```

启动时重新扫描已完成传输和未完成 CDN 作业，避免应用恰好在上传完成后退出而漏任务。上传成功与 CDN 失败必须是两个状态，不能因为 CDN 暂时失败把已完成上传标记为失败。

当前任务队列提供：

- 每个 CDN Profile 独立串行、不同 Profile 可并行
- 指数退避
- 随机抖动
- 任务 ID 状态轮询
- 用户取消
- 脱敏日志
- 启动恢复
- 幂等键和活动中的同 URL 去重

Bucket/前缀关联默认不启用自动操作。用户可分别配置：新对象不处理或 HTTP 预热；覆盖对象不处理、刷新或刷新后预热。只有策略需要区分新对象与覆盖对象时，传输执行器才在上传前读取目标对象状态并把结果写入传输任务。状态探测失败只记录脱敏日志并跳过 CDN 自动化，不能阻止原始上传。

通用 HTTP Provider 已接受多 URL 任务模型，但 v0.5.8 的桌面操作和上传自动化仍按对象创建任务；跨任务的 URL/目录聚合与 Provider 配额窗口留待原生 Provider 接入时实现。

## 14. Provider 扩展计划

### 第二阶段优先级

1. AWS CloudFront：Invalidation；预热仍可复用 HTTP GET。
2. Cloudflare：Zone/R2 自定义域名、按 URL Purge；预热复用 GET。
3. Alibaba Cloud CDN：URL 刷新和原生预热（已实现）。
4. 腾讯云 CDN：URL/目录刷新和原生预热。

### 后续

- Google Cloud CDN URL Map invalidation
- Backblaze B2 + Cloudflare 组合模板
- MinIO / 自建反向代理模板
- Provider 原生异步任务状态和配额展示

Provider 接口应按能力声明：

```text
BuildUrl
DownloadProbe
Warmup
PurgeUrl
PurgePrefix
NativePrefetch
QueryJobStatus
```

不能通过“按钮存在但调用后报不支持”替代能力探测。

## 15. 配置版本与迁移

CDN 配置仍有独立的模型版本校验；新增字段必须提供兼容默认值，破坏性变化需要显式迁移。运行时由统一 `configuration.json` 原子保存，迁移连接包时不得把 DPAPI 密文当作跨机器可移植凭据。

连接包格式 4 使用统一凭据模型，并继续兼容导入 v1-v3：

- 单连接导出只携带该连接相关的 Profile/Binding；全部导出还携带未关联 Profile。
- 默认只导出 Profile/Binding 非敏感字段，并移除凭据引用。
- 统一凭据必须由用户显式选择，并使用迁移密码重新加密。
- 导入时重新生成并映射 S3、CDN Profile、Credential 和 Binding ID；覆盖模式保留本机 ID。
- 不导出本机 DPAPI 密文，导入后写入目标用户的统一 DPAPI Vault。

## 16. 错误处理

- 统一配置读取或解密失败：停止加载并显示脱敏错误，不以空 CDN 配置覆盖现有配置图。
- 配置保存失败：显示错误，不更新内存状态。
- 缺失凭据：禁用对象动作并提示修复。
- 下载测试失败：保留窗口和 URL，允许重试。
- 预热/刷新失败：不修改对象状态，显示 HTTP 状态与有限响应摘要。
- 取消与超时：下载测试提供显式取消按钮和 1–3600 秒超时；关闭窗口也会取消正在进行的请求。
- 证书检测失败：区分有效期、域名、证书链、手动取消、超时和网络/TLS 握手错误；不把失败结果持久化为健康状态。
- URL 映射失败：在发出网络请求前本地拒绝。

## 17. 测试与验收

Core：

- 最长前缀和默认 CDN 顺序
- 路径分段编码
- 范围外对象不匹配
- 重复默认关联校验
- 缺失凭据引用校验

Infrastructure：

- 配置和关联 round-trip
- 统一配置文件和连接包均不出现明文秘密
- Range Header、缓存 Header 识别
- 真实本地 TLS 握手、证书有效期、过期证书、域名和不受信任证书链识别
- 完整 GET 预热
- 刷新模板和 Bearer 鉴权
- Alibaba CDN 刷新/预热分批、任务查询、精确域名权限检查和错误脱敏

App：

- 配置中心三页签存在
- 主要新增/保存按钮在 12pt 字体和最小尺寸下可见、可读
- 下载测试开始/关闭按钮布局
- HTTPS 证书检测按钮与结果窗口在大字体和最小尺寸下保持可读
- UI 自动化报告验证 CDN 菜单命令已注册

完整交付门禁：

- 全量测试
- Release 构建
- 依赖安全审计
- 隔离自动化数据目录的 UI 冒烟和截图（运行能力可用时）

## 18. 已知限制

- Alibaba CDN 权限检查只验证 `DescribeUserDomains` 的精确域名查询，不能无副作用证明刷新/预热写权限。
- 下载测试的响应头耗时是 `SendAsync(ResponseHeadersRead)` 的客户端观测值，不是浏览器 Navigation Timing。
- 单次本地预热无法证明全球边缘节点已缓存。
- CLI 已支持 `cdn test`、`cdn cache-test`、`cdn warmup` 与 `permission check --cdn-profile`；尚未暴露独立 purge 命令。
- 暂无 Prefix Purge 的厂商统一抽象。
- 暂无跨任务 URL/目录聚合和厂商配额窗口；当前每个 CDN Profile 同时执行一个任务。
- 不创建、续期或修改 DNS、证书、源站和 CDN 分发；只提供当前 HTTPS 端点的即时只读证书检测。
