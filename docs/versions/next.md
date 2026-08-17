# Next / Unreleased

本文件只记录已经合入 `main`、但尚未进入正式版本 tag 的变化。它不是版本号，也不表示已经正式发布。最近正式版本仍是 `v0.7.6`；已发布历史见 [`README.md`](README.md)。

## 修复

- 新增统一 Credential Vault：对象存储、AWS External ID 与 CDN 凭据统一由加密的 `configuration.json` 管理。
- 首次启动自动迁移旧配置，并将旧文件归档到 `legacy-archive`；运行时不再回读旧文件。
- 新增桌面端“配置与凭据中心”、CLI `credential` 命令和关联权限检查。
- 权限检查默认无副作用；真实写入探针必须显式使用 `--probe-write --yes`。
- 新增 Alibaba Cloud CDN 原生 Provider；Alibaba AccessKey 可同时关联 OSS 与 CDN，原生刷新/预热使用正式控制面 API。

## 维护

- 暂无。

## 兼容性 / 数据格式

- 连接包 v4 使用统一凭据引用；v1-v3 仍可导入并迁移为统一凭据。含凭据包继续使用迁移密码加密，目标机器重新写入 DPAPI。
- CLI 与桌面端共用 `%APPDATA%\S3Explorer\configuration.json`，不再分别维护 S3/CDN 运行时凭据文件。

## 已知限制

- 首次迁移统一配置时应先退出旧版桌面程序和旧版 CLI，避免旧版本在迁移后继续写入已归档的旧格式配置。
- Alibaba CDN 的无副作用权限检查只验证精确域名查询，不能证明刷新/预热写权限。
- 通用 HTTP CDN 的普通响应不能证明服务端实际校验了 Bearer Token 或自定义 Header，因此认证结果会标记为“无法确定”。

## 发布时处理

1. 选择下一个版本号后，把本文件中属于该版本的条目整理到新的 `vX.Y.Z.md`。
2. 完成版本号、更新清单、Pages 和发布包文件名的一致性检查。
3. 在同一个发布提交中把本文件重置为本模板，并保留空的“修复 / 维护 / 兼容性 / 已知限制”章节。
