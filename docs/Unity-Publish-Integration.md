# Unity 发布接入

S3 Explorer 为 Unity 2021.3、CI 和构建工具提供独立的发布契约程序集。每个正式版本的 GitHub Release 都包含：

```text
S3Explorer.Contracts-vX.Y.Z.zip
```

压缩包内只有以下三个文件：

- `S3Explorer.Contracts.dll`：目标框架为 `netstandard2.1` 的 DTO 程序集。
- `S3Explorer.Contracts.xml`：IDE 提示所需的 XML 文档。
- `README.md`：与 DLL 同版本的接入说明。

## 导入 Unity

1. 首次接入时下载 Contracts ZIP；以后只在 Contract API 或 Manifest Schema 超出当前插件支持范围时更新 DLL，产品补丁版本变化不要求替换。
2. 将 `S3Explorer.Contracts.dll` 复制到 Unity 项目的 `Assets/Plugins/S3Explorer/`。
3. 可将 XML 文件放在 DLL 旁边，供 IDE 显示类型说明；运行时不依赖该文件。
4. Unity 2021.3 项目的 API Compatibility Level 使用 `.NET Standard 2.1`。

程序集提供 `PublishManifest`、`PublishPlan`、`PublishResult`、`VerifyResult` 和 CDN 结果等结构化 DTO。它不包含 S3 客户端、网络请求实现或任何凭据；实际上传、校验和 CDN 操作由独立的 `s3explorer-cli.exe` 完成。

## 调用边界

Unity 或构建脚本通过 `Process` 启动 CLI，并读取 `--output json` 的标准输出。自动化调用应同时指定 `--non-interactive --yes`，并按需要设置 `--timeout`、`--cancel-file` 和 `--log-file`：

```text
s3explorer-cli publish --profile minio-dev --source D:\Build\Windows --bucket game-builds --prefix windows/1.2.3 --project game --product windows --version 1.2.3 --output json --non-interactive --yes
```

需要统一 CDN 缓存、Content-Type、自定义 Metadata 或 Object Tags 时，增加 `--header-rules <json-file>`。规则按顺序叠加，未包含 `/` 的 pattern 匹配任意目录中的文件名：

```json
{
  "schemaVersion": 1,
  "defaults": {
    "cacheControl": "public,max-age=300",
    "metadata": { "channel": "stable" }
  },
  "rules": [
    {
      "pattern": "*.json",
      "headers": {
        "contentType": "application/json",
        "cacheControl": "no-cache"
      }
    },
    {
      "pattern": "bundles/**",
      "headers": {
        "cacheControl": "public,max-age=31536000,immutable",
        "tags": { "content": "bundle" }
      }
    }
  ]
}
```

最终生效的 Header、Metadata 与 Tags 会写入 Manifest Schema 2。增量计划同时比较文件 Size、SHA-256 和这些对象属性；只改缓存规则也会把对象标记为 `Modified`。Schema 1 Manifest 仍可读取，但需要解析 Schema 2 远端 Manifest 的 Unity 工具应更新 v0.7.1 Contracts DTO。

构建产物目录需要与远端严格一致时，可显式增加：

    s3explorer-cli publish ... --delete-mode mirror --output json --non-interactive

默认 `--delete-mode none` 不删除任何远端对象。`mirror` 要求安全的非空 Prefix，实时递归扫描目标范围并在 dry-run 中返回删除计划；只有上传、SHA-256 回读验证和对象 ACL 全部成功后才执行删除，删除失败时不会发布新 `publish-manifest.json`。删除范围不包含 Prefix 外对象、目录标记和现有 Manifest，因此可以替代构建工具中“删除远端本地已不存在文件”的发布语义，而无需创建持久化 Folder Sync Job。

Unity 项目只保存 Profile ID/名称、Bucket、Prefix 和可选 CDN Profile ID。Access Key、Secret Key、Session Token 与 CDN 密钥仍由 S3 Explorer 在 Windows 用户配置目录中保存并通过 DPAPI 保护，不应写入 Unity 工程、命令行或日志。

## CDN 匿名读取与缓存探测

对象默认保持存储端现有 ACL。需要让 CDN 通过匿名源站读取本次发布内容时，显式使用：

    s3explorer-cli publish ... --access anonymous-read --yes

该选项只把当前 Manifest 中的对象和 `publish-manifest.json` 设置为对象级 `public-read`，不会读取或修改 Bucket Policy，也不会关闭 Public Access Block。目标服务禁用对象 ACL、启用了 Bucket Owner Enforced 或拒绝 `PutObjectAcl` 时，发布会返回明确错误；此时应改用 CDN 源站鉴权或由管理员配置访问边界，不能由发布工具绕过。

恢复对象私有 ACL 可显式使用 `--access private --yes`。默认 `--access preserve` 不修改既有 ACL；`--dry-run` 只显示将处理的 ACL 数量。

CDN 配置完成后，可对同一路径连续发送两次 HEAD 请求并查看缓存 Header：

    s3explorer-cli cdn cache-test --profile "cdn-prod" --path "bucket/deploy/game-survival/config.bytes" --output json --non-interactive

结果逐次返回 `attempt`、HTTP 状态和 `cacheStatus`，可观察 `X-Cache: MISS` 到 `X-Cache: HIT`；若对象此前已经缓存，第一次也可能直接为 HIT。

Contracts DLL 和 CLI 不再要求产品版本完全一致。Unity 插件应先运行 `s3explorer-cli version --output json`，读取 `contractApiVersion`、`minimumSupportedContractApiVersion`、`maximumSupportedContractApiVersion` 以及 Manifest Schema 范围；只要插件使用的 Contract API 与 Manifest Schema 都落在 CLI 返回范围内即可继续。`version` 字段仅用于诊断和展示，不能作为拒绝运行的依据。

当前 Contract API 为 `1`，Manifest Schema 当前版本为 `2`，CLI 仍支持读取 Schema `1`。从 v0.6.7 开始，`S3Explorer.Contracts.dll` 的程序集 ABI 版本固定为 `1.0.0.0`，文件版本仍跟随产品版本；只有破坏兼容性的契约升级才会改变 ABI 主版本。旧插件需要一次性改为上述范围判断；若插件需要读取 Schema 2 新增的对象属性，应更新 v0.7.1 Contracts DLL。
