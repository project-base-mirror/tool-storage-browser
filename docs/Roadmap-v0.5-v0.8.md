# S3 Explorer v0.5–v0.8 功能对标与交付路线图

本文以 2026-07-27 的仓库状态为基线，参考 S3 Browser 的官方功能页、Folder Sync 与 CLI 文档，规划 S3 Explorer 后续版本。目标不是机械复制菜单，而是按“跨 S3 兼容服务可用、危险操作可恢复、每个入口形成闭环”的标准逐步补齐。

参考资料：

- [S3 Browser 功能总览](https://s3browser.com/)
- [Folder Sync Tool](https://s3browser.com/amazon-s3-folder-sync.aspx)
- [Command Line Interface](https://s3browser.com/s3cmd.aspx)
- [S3 Browser 在线帮助目录](https://s3browser.com/help.aspx)

## 规划原则

1. 一个需求控制在 1–2 个提交；同一提交不得混入无关需求。
2. 先完成能力探测，再显示管理入口；不支持的 Provider 不发送试探性写请求。
3. 删除、覆盖、公开访问、生命周期、版本清理等高风险操作默认关闭，并提供预览、确认和审计结果。
4. Amazon S3 专有能力与 S3-compatible 通用能力分层实现，不能用 AWS 成功代替 MinIO、Ceph、Google XML API 等兼容验证。
5. GUI、CLI 和同步任务共享 Core 模型与服务，不维护三套行为不一致的实现。
6. 每个版本必须同步维护 `docs/versions/`，并通过全量测试、Release 构建、CLI 冒烟、UI 冒烟和发布包检查。

## 当前能力快照

| 领域 | 已完成 | 主要缺口 | 优先级 |
| --- | --- | --- | --- |
| 账户与兼容性 | Amazon S3、S3 兼容、Google XML API；Provider 模板；Region 自动处理；指定 Bucket；DPAPI | AWS Profile/SSO/AssumeRole；配置导入导出；账户分组；代理 | P1 |
| Bucket 基础管理 | 创建、删除、安全清空、属性、ACL、Policy、Public Access Block、Object Ownership | CORS、版本控制、生命周期、默认加密、标签、日志、Object Lock | P1–P2 |
| 对象管理 | 分页、递归、上传下载、复制移动、重命名、删除、Metadata、预签名 URL | 版本管理、Tagging、存储类型、跨账户复制、拖放、直接打开/编辑 | P1–P2 |
| 传输可靠性 | 持久队列、暂停恢复、重试、限速、Multipart 检查点、批次失败明细 | 完整性校验报告、冲突策略、临时空间检查、后台托盘 | P1–P2 |
| 文件夹同步 | 持久任务、单向镜像、分析后执行、排除规则、可选哈希、删除传播 | 逐项勾选、分析缓存、冲突策略、计划任务、报告导出、增量扫描 | P1 |
| 自动化 | 独立 CLI、JSON、退出码、连接/Bucket/对象/同步命令 | JSONL 事件流、队列查询、更多设置 API、PowerShell completion | P2 |
| 交付 | 本地构建/发布脚本、发布包检查、UI 自动化 | Pages、GitHub Release、客户端更新检查、签名、SBOM | P0–P2 |

优先级定义：P0 是交付基础；P1 是近期高价值；P2 是增强能力；P3 是高复杂度或平台相关能力。

## v0.5.1：项目交付与更新闭环

状态：本轮交付。

- 建立 GitHub Pages 项目主页，提供功能概览、下载入口、CLI 示例、路线图和版本历史链接。
- tag 推送触发 Windows Release 工作流，复用仓库发布脚本并上传两种 ZIP、指标文件和校验文件。
- 客户端实现“检查更新”“打开项目主页”“报告问题”，启动时后台检查公开 GitHub Release；网络失败不阻塞启动。
- 设置页允许关闭启动检查；更新窗口只提供版本说明和下载链接，不静默替换正在运行的程序。
- 回填 `v0.2.1` 至当前版本的逐版本记录，并明确无 Git tag 的历史来源。

验收：Pages 工作流语法检查；Release workflow 的 tag/版本一致性门禁；更新解析和版本比较单元测试；离线 UI 冒烟不访问网络。

## v0.6：日常管理闭环

### v0.6.0 Bucket 配置第一组

- CORS：规则列表、JSON/表单双视图、语法验证、读取/保存/删除。
- 版本控制：查看 Disabled/Enabled/Suspended 状态；启用和暂停前解释影响。
- 默认加密：SSE-S3 与 SSE-KMS；仅在 Provider 明确支持时显示 KMS。
- Bucket Tagging：键值编辑、重复键校验、费用分配标签提示。

验收：读取失败不覆盖现有配置；保存前展示差异；MinIO 与 Amazon 能力分开验证。

### v0.6.1 对象版本与恢复

- “显示版本”模式、Delete Marker、版本 ID、是否当前版本列。
- 下载指定版本、恢复历史版本、删除单个版本、批量清理 Delete Marker。
- 对启用版本控制的 Bucket，普通删除明确提示会创建 Delete Marker。

验收：分页版本列表；恢复不覆盖错误 Key；删除版本需要二次确认；CLI 同步暴露版本 ID。

### v0.6.2 同步任务第二阶段

- 分析结果支持逐项勾选、只显示新增/更改/删除/跳过、按路径与扩展名排序。
- 从结果右键快速创建排除规则；支持文件、扩展名、目录、大小和日期条件。
- 分析快照带源/目标身份和过期时间，连接或路径变化后强制重新分析。
- 执行结束生成 JSON/CSV 报告；失败项可重新入队。

验收：未勾选项不入队；改变账户/Bucket 后旧结果失效；删除传播仍需显式确认。

### v0.6.3 账户与凭据来源

- 无凭据导出、显式选择的凭据导入、重复名称处理和预览。
- AWS shared credentials/config、环境变量和实例/容器角色。
- AWS SSO 与 AssumeRole 分阶段接入；令牌缓存与长期密钥分开存储。
- 账户分组、复制连接、连接健康状态与最近成功时间。

验收：导出默认不含 Secret；日志不出现 token；凭据来源切换可回滚；自动化数据目录保持隔离。

## v0.7：高级 S3 管理

### v0.7.0 生命周期与 Object Lock

- 生命周期规则编辑器：前缀/标签过滤、存储类型转换、过期、非当前版本与未完成 Multipart 清理。
- Object Lock 状态只读探测；Retention/Legal Hold 操作单独授权、逐对象确认。
- 规则冲突、非法天数和 Provider 不支持在本地验证阶段阻止提交。

### v0.7.1 对象元数据与发布

- Object Tagging、批量 Metadata/Header、Content-Type 映射与默认 Header 规则。
- 存储类型变更和服务端复制重写；显示请求成本与跨区域风险提示。
- 高级 URL 生成器：过期时间、响应 Header、虚拟主机/路径样式；不把签名 URL 写入日志。
- 静态网站配置与站点 URL，仅对支持 Website API 的 Provider 开放。

### v0.7.2 跨账户与共享

- 跨 Bucket/跨账户复制移动：先判断服务端 Copy 可行性，再回退到受控下载上传。
- 外部 Bucket 向导、Requester Pays、共享 Bucket 权限说明。
- Bucket Sharing Wizard 将授权对象、动作和资源范围可视化，最终仍以 Policy/ACL 差异确认。

### v0.7.3 运维配置

- Bucket Logging、Transfer Acceleration、Replication 的只读探测与分阶段编辑。
- 统一 Provider capability registry；菜单按支持/只读/不支持显示原因。
- 网络代理、临时目录、磁盘空间预检、数据完整性校验策略。

## v0.8：规模化与平台集成

### v0.8.0 大规模列表与搜索

- ListView 虚拟化、增量排序、百万对象缓存预算、后台分页预取。
- 服务端前缀搜索与本地过滤明确区分；保存搜索和书签。
- 预览文本/图片时限制大小、Content-Type 和临时文件生命周期。

### v0.8.1 自动化与计划任务

- CLI JSONL 事件流、队列状态/等待/取消、稳定 schema 版本和 shell completion。
- 同步任务生成 Windows Task Scheduler 命令；用户确认后才创建、修改或删除系统任务。
- 托盘图标、完成通知和“传输时防止睡眠”策略统一。

### v0.8.2 发布安全

- 可复现构建、NuGet 锁文件、SBOM、发布资产 SHA-256、代码签名与签名验证说明。
- 更新检查支持发布通道和跳过版本；安装更新仍保持用户确认，不做后台静默执行。
- 崩溃报告只生成本地脱敏包，由用户审阅后主动提交。

## 明确延后或不直接照搬的功能

| 功能 | 决策 | 原因 |
| --- | --- | --- |
| CloudFront 全量管理 | 延后到 v0.9+ | 不属于通用 S3 API，会扩大权限和兼容测试矩阵 |
| 挂载为 Windows Drive | 延后到独立项目评估 | 需要文件系统驱动/WinFsp、缓存一致性和随机写语义 |
| 客户端 AES 压缩加密格式 | 延后 | 必须先定义可迁移格式、密钥恢复和流式兼容性，不能制造私有不可恢复数据 |
| 一键公开 Bucket/对象 | 不提供无确认快捷操作 | 容易产生数据泄露；只能通过可审计 Policy/ACL 流程完成 |
| 自动静默安装 | 当前不做 | 桌面程序自替换涉及签名、回滚、权限和被占用文件，先完成可信发布链 |

## 需求与提交控制

每个小版本先拆成可独立验收的需求，每个需求控制在 1–2 个提交：

1. Core/Infrastructure 与测试可作为第 1 个提交。
2. UI/CLI 接入与端到端验证可作为第 2 个提交。
3. 只有文档、工作流或静态站点本身就是独立需求时，才单独提交。
4. 不为了“整理”拆出只有重命名、只有格式化、无法独立回滚的碎片提交。

每个版本完成后，在 `docs/versions/vX.Y.Z.md` 记录范围、行为变化、兼容性、验证、升级说明、已知限制和关联提交。
