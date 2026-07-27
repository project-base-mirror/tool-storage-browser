# CDN 内容交付集成设计与实施计划

本文定义 S3 Explorer 的 CDN / 内容分发能力边界、第一阶段实现、后续 Provider 扩展方式和自动化路线。CDN 与对象存储有关联，但不是对象存储连接本身：对象存储负责原始对象和 S3 API，CDN 负责公网交付 URL、缓存刷新、缓存预热和下载质量探测。

## 1. 产品边界

### 1.1 目标

- 为一个对象存储连接下的 Bucket 或对象 Key 前缀关联一个或多个 CDN。
- 根据当前对象生成稳定的 CDN URL，并支持复制和使用系统浏览器打开。
- 使用真实 HTTP GET + Range 请求测试 CDN 下载状态、响应头耗时、吞吐和缓存 Header。
- 通过 HEAD、Range GET 或完整 GET 执行通用 HTTP 预热。
- 通过用户配置的 HTTP 端点、方法和 Body 模板手动提交缓存刷新。
- CDN 凭据与 S3 Access Key/SecretKey 分开保存和授权。
- 保持 Provider 扩展点，后续接入 CloudFront、Cloudflare、阿里云 CDN、腾讯云 CDN 等原生 API。
- 不改变现有 S3 下载、预签名 URL、上传队列和对象管理语义。

### 1.2 非目标

第一阶段不创建或修改 CDN 分发、源站、证书、DNS、WAF、缓存规则、计费和日志服务，也不在上传完成时自动执行刷新或预热。完整 CloudFront/厂商控制台能力仍属于后续阶段。

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

- CDN 配置、独立凭据、Bucket/前缀关联有稳定 Core 模型。
- 配置与凭据分文件持久化；秘密值不以明文落盘。
- URL 映射使用最长前缀规则，支持一个范围关联多个 CDN 和一个默认 CDN。
- 下载探测、HTTP 预热和通用刷新均可独立测试。
- 不依赖任何特定云厂商 SDK。

最终提交：`feat(cdn): add profiles bindings and generic delivery services`

### 需求 B：桌面交付工作流

验收：

- 顶级“CDN / 分发”菜单提供配置、URL、下载测试、预热和刷新。
- Bucket 右键可直接打开关联管理；对象右键可执行交付动作。
- CDN 配置中心统一管理配置、独立凭据和关联。
- 命令按当前对象是否存在有效关联、是否支持刷新自动启用。
- 大字体和最小窗口尺寸下主要按钮可见、可读。
- README、路线图和本文档反映真实实现与后续边界。

最终提交：`feat(ui): integrate generic CDN delivery workflows`

## 4. 架构

```text
S3Explorer.Core
  CdnModels.cs
    CdnProfile / CdnCredential / CdnBinding
    ICdnConfigurationStore / ICdnCredentialStore
    ICdnDeliveryService
    CdnUrlMapper / CdnConfigurationValidator

S3Explorer.Infrastructure.Cdn
  JsonCdnStores.cs
  DpapiCdnCredentialProtector.cs
  GenericHttpCdnDeliveryService.cs

S3Explorer.App
  CdnConfigurationDialog.cs
  CdnDownloadTestDialog.cs
  MainForm.Cdn.cs
```

`S3Explorer.Infrastructure.S3` 不引用 CDN。通用 CDN 基础设施只依赖 Core，后续厂商适配器可以继续放在 `Infrastructure.Cdn` 中，或者在 SDK 依赖明显增大时拆为独立程序集。

## 5. 数据模型

### 5.1 CdnProfile

关键字段：

- `Id`、`Name`、`Enabled`
- `ProviderId`：第一阶段固定为 `generic-http`
- `BaseUrl`：对象交付基础地址
- `CredentialId`：可选独立凭据
- `WarmupMode`：HEAD、Range GET、完整 GET
- `WarmupRangeBytes`
- `TimeoutSeconds`、`FollowRedirects`
- `PurgeEndpointTemplate`、`PurgeHttpMethod`
- `PurgeBodyTemplate`、`PurgeContentType`

能力由配置推导：所有通用 HTTP 配置支持 URL、探测和预热；只有设置刷新端点后才声明 `Purge`。

### 5.2 CdnCredential

认证方式：

- 无认证
- `Authorization: Bearer <token>`
- 自定义 Header

第一阶段只解决通用 HTTP 鉴权，不假设 S3 密钥可复用。厂商签名 API 后续由 Provider 适配器使用专用凭据类型。

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

默认数据位置：

```text
%APPDATA%\S3Explorer\cdn-config.json
%APPDATA%\S3Explorer\cdn-credentials.json
```

自动化模式使用隔离数据目录中的同名文件，不读取真实用户配置。

`cdn-config.json` 只保存非敏感配置和凭据 ID。`cdn-credentials.json` 的 Secret 使用 Windows DPAPI CurrentUser 加密，并使用与 S3 凭据不同的 entropy。结果只能由同一 Windows 用户上下文解密。

安全约束：

- 不自动复用 S3 SecretKey。
- 日志不记录 CDN Secret、Authorization Header 或自定义鉴权 Header。
- 通用刷新响应只在对话框中显示最多 4096 字符摘要；日志只记录状态、耗时和字节数。
- 下载测试和预热仅接受 HTTP/HTTPS 绝对 URL。
- 基础 URL 不允许内嵌用户名密码、查询参数或片段；鉴权秘密不允许换行，自定义 Header 名称必须符合 HTTP token 规则。
- 配置与凭据 JSON 只读取当前明确支持的文档版本，未知版本会停止加载而不是静默误读。
- 超时、Range 大小和 HTTP 方法在本地校验。
- 删除仍被 CDN Profile 引用的凭据会被阻止。
- 同一连接、Bucket、源前缀只能有一个默认 CDN。

## 8. 桌面工作流

### 8.1 CDN 配置中心

三个页签：

1. **CDN 配置**：域名、预热行为、刷新端点、鉴权引用和超时。
2. **独立凭据**：无认证、Bearer Token、自定义 Header。
3. **Bucket / 前缀关联**：源连接、Bucket、源前缀、CDN、目标路径前缀和默认项。

保存时统一运行模型校验；失败时不覆盖磁盘配置。保存顺序为凭据后配置，因此中途失败最多留下一个未被引用的凭据，不会让配置引用尚未落盘的秘密。

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

预热成功只表示 HTTP 请求完成且状态码为 2xx/3xx，不保证所有边缘节点已经缓存。厂商原生预热任务在后续 Provider 中应返回任务 ID 和状态查询能力。

## 11. 通用 HTTP 刷新

用户可以配置：

```text
Endpoint: https://api.example.com/purge?target={url}
Method:   POST
Body:     {"path":"{path}"}
Type:     application/json
```

端点中的 `{url}` 和 `{path}`使用 URL 编码；Body 中使用 JSON 字符串内容转义。2xx 表示提交成功。没有端点时，UI 隐藏能力并禁用刷新命令。

这是一种可配置适配层，不替代厂商签名逻辑。要求 HMAC、AWS SigV4、TC3 或 RPC 签名的 API 必须通过后续 Provider 实现，不能让用户把长期密钥拼进 URL 或 Body。

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

## 13. 上传完成自动化设计（后续阶段）

现有传输队列在任务首次进入 Completed 时发出完成事件，可作为生成 CDN 作业的触发信号，但不能直接在 UI 事件中调用外部 API。

建议新增持久 `CdnJob` 队列：

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

批量任务需要：

- URL/目录聚合
- Provider 配额和速率限制
- 指数退避
- 任务 ID 状态轮询
- 用户取消
- 脱敏日志
- 启动恢复

## 14. Provider 扩展计划

### 第二阶段优先级

1. AWS CloudFront：Invalidation；预热仍可复用 HTTP GET。
2. Cloudflare：Zone/R2 自定义域名、按 URL Purge；预热复用 GET。
3. 阿里云 CDN：URL/目录刷新和原生预热。
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

JSON 文档当前为版本 1。新增字段必须提供兼容默认值；破坏性变化需要显式版本迁移。凭据文件和普通配置文件保持分离，迁移配置时不得把 DPAPI 密文当作跨机器可移植凭据。

连接导入导出第一阶段不包含 CDN 配置。后续若支持导出：

- 默认只导出 Profile/Binding 非敏感字段。
- 独立凭据必须由用户显式选择，并使用迁移密码重新加密。
- 不导出本机 DPAPI 密文。

## 16. 错误处理

- 配置读取失败：记录脱敏错误，CDN 能力回退为空，不影响 S3 浏览。
- 配置保存失败：显示错误，不更新内存状态。
- 缺失凭据：禁用对象动作并提示修复。
- 下载测试失败：保留窗口和 URL，允许重试。
- 预热/刷新失败：不修改对象状态，显示 HTTP 状态与有限响应摘要。
- 取消：下载测试使用独立 CancellationToken；关闭窗口会取消正在进行的请求。
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
- 凭据文件不出现明文秘密
- Range Header、缓存 Header 识别
- 完整 GET 预热
- 刷新模板和 Bearer 鉴权

App：

- 配置中心三页签存在
- 主要新增/保存按钮在 12pt 字体和最小尺寸下可见、可读
- 下载测试开始/关闭按钮布局
- UI 自动化报告验证 CDN 菜单命令已注册

完整交付门禁：

- 全量测试
- Release 构建
- 依赖安全审计
- 隔离自动化数据目录的 UI 冒烟和截图（运行能力可用时）

## 18. 已知限制

- 第一阶段刷新仅支持不需要厂商签名的 HTTP API。
- 下载测试的响应头耗时是 `SendAsync(ResponseHeadersRead)` 的客户端观测值，不是浏览器 Navigation Timing。
- 单次本地预热无法证明全球边缘节点已缓存。
- 暂无持久 CDN 作业队列、自动上传后处理和任务历史。
- 暂无 CDN CLI。
- 暂无 Prefix Purge 的厂商统一抽象。
- 不管理 DNS、证书、源站和 CDN 分发本身。
