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

Unity 项目只保存 Profile ID/名称、Bucket、Prefix 和可选 CDN Profile ID。Access Key、Secret Key、Session Token 与 CDN 密钥仍由 S3 Explorer 在 Windows 用户配置目录中保存并通过 DPAPI 保护，不应写入 Unity 工程、命令行或日志。

Contracts DLL 和 CLI 不再要求产品版本完全一致。Unity 插件应先运行 `s3explorer-cli version --output json`，读取 `contractApiVersion`、`minimumSupportedContractApiVersion`、`maximumSupportedContractApiVersion` 以及 Manifest Schema 范围；只要插件使用的 Contract API 与 Manifest Schema 都落在 CLI 返回范围内即可继续。`version` 字段仅用于诊断和展示，不能作为拒绝运行的依据。

当前 Contract API 和 Manifest Schema 都是 `1`。从 v0.6.7 开始，`S3Explorer.Contracts.dll` 的程序集 ABI 版本固定为 `1.0.0.0`，文件版本仍跟随产品版本；只有破坏兼容性的契约升级才会改变 ABI 主版本。旧插件需要一次性改为上述范围判断，此后普通补丁升级不再要求同步 DLL。
