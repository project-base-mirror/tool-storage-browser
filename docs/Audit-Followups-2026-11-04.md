# 2026-11-04 审计后续修复

此前的 [PR #3](https://github.com/project-base-mirror/tool-storage-browser/pull/3) 已合入 `main`，合并提交为 `c7dfed6aa156f666945ded31a0852bc47cb7d1c2`。合并树与经过测试的 PR head 完全一致，合并后的 [Validate](https://github.com/project-base-mirror/tool-storage-browser/actions/runs/41783713593) 也已通过。本记录描述后续独立分支，不表示这些后续变化已经合入或发布。

## Metadata：编辑后另存，不覆盖原对象

新增的是 **将编辑后的固定版本快照另存为同一 Bucket 的新 Key**。它不是恢复原地修改，也不会自动删除原对象。原地及批量重写仍拒绝执行，因为 ETag 只识别内容，不能证明 Metadata、ACL、Tags 或保护设置没有单独变化。

安全复制限定在 Amazon S3、非 null 的固定版本 ID、明确的 SSE-S3 加密、不超过 5 GiB 的对象，以及禁用 ACL 的 BucketOwnerEnforced、无 Object Lock 配置的 Bucket。保存时重新读取该固定版本并核对身份，再通过目标 `If-None-Match: *` 创建新对象。KMS、SSE-C、缺失 Metadata、无法读取安全配置等情况不会降级为无条件复制。

可编辑 Header 和自定义 Metadata 作为用户选择的快照保存；不可编辑的 Content-Language、网站重定向、存储类型等继续保留，Tags 由服务端复制。新对象的 Key 和原对象不同，目标已存在或保存过程中出现时失败。

完整前提、未知结果处理和官方协议来源见 [Metadata 快照另存说明](Metadata-Snapshot-Copy-2026-11-04.md)。

## MinIO 与 Supabase

- MinIO 空对象非覆盖复制不再依赖分片复制：先用条件 GET 核对零字节来源，再使用条件 PUT 写入空对象，保留对应 Header、Metadata 和 Tags。目标冲突不覆盖、不降级。
- Supabase 不超过 5 GiB 的复制继续使用其支持源条件的单次 CopyObject，不再仅因客户端分片阈值而拒绝。Supabase 不支持的版本地址不会发送。
- Supabase 的分片复制、原子非覆盖目标和条件删除仍不能由现有协议保证。自动移动仍拒绝；可显式复制、核对目标，再单独决定是否删除来源，不会把只复制伪装成移动成功。
- MinIO 条件写入还依赖服务端正确实现协议。隔离 CI 使用包含上游读仲裁故障条件检查修复的官方提交；不能凭兼容类型名称或第三方发行版日期保证任意部署具有该修复。

服务端协议、固定提交及具体回归清单见 [Provider 复制补充证据](Provider-Copy-Followups-2026-11-04.md)。另修正 SDK Expires 的读取与复制，避免日期 Header 丢失或在原本无值时产生默认日期。

## 精确对象路径

导航与对象传输保留重复斜杠、开头斜杠、反斜杠、空白和字面点路径段。界面可用说明标签显示空路径段，但实际请求、复制、重命名不得从显示标签重新构造 Key。无法保真映射为本地文件的同步路径在生成计划前拒绝，而不是改名后指向其他远端对象。

## 安装器生命周期

生命周期测试在隔离 Windows runner 本地构建测试专用旧版、新版和故障版 MSI，不下载历史 Release 安装包，也不发布新资产。故障仅注入到测试复制的 WiX 源文件，正式安装器没有故障开关。

测试检查自定义安装目录、升级失败回滚后的旧产品及文件/注册表/快捷方式、成功升级、安装后的 GUI/CLI 启动和卸载。测试版使用当前应用载荷与变化的哨兵文件，用于证明安装器事务，不代表所有历史应用版本的升级兼容性。

## 验证状态

后续分支的 Windows、MinIO 与 MSI 实测结果以该分支最终提交的 CI 为准；尚未执行或失败不得视为通过。本地云电脑没有 .NET/PowerShell，没有安装新软件，也没有访问用户电脑、真实用户配置、凭据或生产存储。

没有创建 tag、Release 或新版本。本轮未访问真实 AWS、R2 或托管 Supabase。
