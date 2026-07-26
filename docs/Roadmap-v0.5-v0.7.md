# S3 Explorer v0.5–v0.7 完善路线图

本文基于 2026-07-27 的代码审查、桌面 UI 冒烟结果以及 S3 Browser 的账户、Folder Sync 与 CLI 工作流整理。原则是每个版本形成可验证闭环，不保留可点击但无法完成的入口。

## 当前审查结论

### 已有优势

- Core / S3 Infrastructure / WinForms 分层明确，凭据通过 Windows DPAPI CurrentUser 加密。
- 传输队列具备持久化、暂停、恢复、重试、Multipart 检查点与批次失败明细。
- Bucket 属性、Policy、ACL、Public Access Block、Object Ownership 和安全清空已有实现。
- 单元测试、MinIO opt-in 集成测试、发布脚本和 UI 冒烟自动化已经形成基本交付链路。

### 主要债务

- `MainForm.cs` 与 `S3StorageService.cs` 体积过大，UI 编排、业务流程和服务兼容逻辑仍耦合在少数类中。
- 原递归对象发现只分页当前前缀，没有继续进入 `CommonPrefixes`；多层文件夹下载、复制、移动存在漏项风险。
- 原“命令行”仅用于启动和 UI 冒烟，不具备可脚本调用的对象存储 API。
- 图标混用 Windows Shell、系统图标和临时线稿；多个命令实际落入同一个矩形占位图。
- 账户窗口直接暴露 9 个枚举值和全部高级参数，未按账户能力隐藏 Region 等无效输入。
- README 的“当前限制”落后于实际实现，仍把已完成的 Bucket Policy / ACL 等列为未支持。

## v0.5：可用性与自动化闭环

状态：本轮实现。

- 统一矢量图标语言；菜单、工具栏、账户树不再使用临时占位图。
- 账户入口收敛为 Amazon S3、S3 兼容存储、Google Cloud Storage；兼容服务使用模板。
- 由 Provider Catalog 统一 Endpoint、签名 Region、地址风格与 TLS 默认值；不需要 Region 时隐藏字段。
- 修复多层对象递归遍历，并增加分页令牌、越界前缀与数量上限保护。
- 新增独立 `s3explorer-cli`，提供 profile / connection / bucket / object / sync 命令、JSON 输出和明确退出码。
- 新增持久化文件夹同步任务：先分析、后执行，支持上传/下载单向镜像、新增/更改/删除、Glob 排除规则与可选哈希比较。
- 桌面同步操作进入原有可恢复传输队列；本地/远端删除也作为可审计任务执行。

## v0.6：管理能力与可靠性

- 将 `MainForm` 拆分为导航、对象命令、Bucket 命令和窗口状态控制器；将 S3 服务按 Bucket/Object/Transfer API 拆分。
- 增加账户管理器：批量删除、复制配置、无凭据导出、显式确认的凭据导入、账户分组。
- Folder Sync 增加分析缓存、每条结果勾选、冲突策略、计划导出、计划执行报告和失败项一键重跑。
- 在上传时写入源修改时间 Metadata；同步比较优先使用 Metadata，减少同大小文件的误判。
- 为 CLI 增加稳定 schema 版本、`--output jsonl`、进度事件流和批次等待/查询命令。
- 为 CLI 与 Folder Sync 增加隔离 MinIO 端到端测试，覆盖上传、下载、删除传播和中断恢复。

## v0.7：S3 高级管理

- Bucket CORS、生命周期、版本控制与 Object Lock 的能力探测和编辑界面。
- 对象版本列表、恢复、删除标记管理、存储类型变更、Tagging 与批量 Metadata 操作。
- 跨账户复制/移动；先能力验证，再选择服务端 Copy 或受控下载上传回退。
- 大规模列表虚拟化，避免百万对象场景把完整列表常驻 UI 内存。
- 可选 Windows Task Scheduler 集成：只生成并展示命令，用户明确确认后才创建计划任务。

## 验收门槛

每个版本至少满足：全量测试通过、Release 零警告构建、CLI 离线冒烟、UI 冒烟、发布包结构检查、真实隔离 MinIO CRUD/同步验证。任何涉及删除传播、凭据导入或计划任务的能力必须默认关闭，并具有明确确认与日志记录。
