# S3 Explorer 文档导航

根目录 [`README.md`](../README.md) 保留项目简介、快速构建、常用 CLI 和用户最常用的运行信息；本页作为详细文档的统一分类入口，避免同一专题在根 README 中重复维护多份链接和说明。

## 当前变化与版本

- [`versions/next.md`](versions/next.md)：已经合入 `main`、但尚未进入正式 tag 的 Next / Unreleased 变化。
- [`versions/README.md`](versions/README.md)：逐版本历史记录；正式 tag 对应的版本文档作为历史快照保留。
- [`Roadmap-v0.5-v0.8.md`](Roadmap-v0.5-v0.8.md)：当前路线图、后续优先级和明确延后项。
- [`Release-Plan-v0.2-v0.3.md`](Release-Plan-v0.2-v0.3.md)：早期 `v0.2`–`v0.3` 需求与验收历史。

## 凭据、连接与迁移

- [`Credential-Center.md`](Credential-Center.md)：统一 Credential Vault、DPAPI 配置、旧文件一次性迁移、GUI/CLI 与权限探针安全边界。
- [`Aws-Credential-Sources.md`](Aws-Credential-Sources.md)：AWS shared profile、环境/角色凭据、SSO、AssumeRole 与 Web Identity。
- [`Connection-Import-Export.md`](Connection-Import-Export.md)：连接、凭据、CDN Profile 和 Binding 的导入导出与冲突策略。

## S3 兼容性与测试

- [`S3-Provider-Matrix.md`](S3-Provider-Matrix.md)：Amazon S3、MinIO、OSS 等 Provider 的能力与真实验证矩阵。
- [`MinIO-Testing.md`](MinIO-Testing.md)：隔离 MinIO、真实 CRUD、Multipart、故障与 UI 回归步骤。

## 发布、自动化与内容分发

- [`Unity-Publish-Integration.md`](Unity-Publish-Integration.md)：Unity / CI 通过 CLI、Manifest、Verify 和 CDN 接入发布流程。
- [`Cdn-Delivery-Integration.md`](Cdn-Delivery-Integration.md)：CDN 配置、绑定、任务、预热/刷新、证书与 Provider 规划。

## 项目交付与正式发布

- [`Release-Process.md`](Release-Process.md)：正式版本号、门禁、tag、Release、Pages 与失败恢复流程。
- [`GitHub-Delivery.md`](GitHub-Delivery.md)：GitHub Actions、Pages、Release 和远端交付边界。

## 站点文件

`docs/site/` 是 GitHub Pages 的发布源，不作为产品说明文档入口。修改 `update.json`、稳定版本链接或下载资产名时必须同时遵守 [`Release-Process.md`](Release-Process.md) 的一致性要求。

## 文档维护原则

1. 已发布行为写入对应 `versions/vX.Y.Z.md`，发布后不回写历史事实。
2. 已进入 `main` 但未发布的用户可见、兼容性、安全或运维变化先写入 `versions/next.md`。
3. 未来计划只维护在路线图，不提前写进“已交付”或版本记录。
4. 专题细节只在对应专题文档维护；根 README 和本页只提供稳定入口与摘要。
