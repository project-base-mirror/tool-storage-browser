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

1. 下载与所用 `s3explorer-cli.exe` 版本一致的 Contracts ZIP。
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

Contracts DLL 和 CLI 必须使用同一版本。跨版本 DTO 会尽量保持兼容，但发布 Manifest 的 `SchemaVersion` 才是数据格式兼容性的最终依据。
