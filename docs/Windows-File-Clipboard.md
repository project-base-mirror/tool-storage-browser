# Windows 双向文件剪贴板

此文档描述文件剪贴板工作分支的实现与验收边界，不代表已经发布。2026-11-04 的实现环境为 Linux，未安装 .NET SDK；新增测试尚未编译或运行，真实 Explorer 验收仍待 Windows 测试环境完成。

## 使用方式

- 在对象列表选择一个或多个文件、目录，按 **Ctrl+C**。状态栏显示“可在资源管理器 Ctrl+V”后，在本地文件夹按 **Ctrl+V**。
- Ctrl+C 会递归发现目录并读取对象属性，冻结清单、大小、ETag 和可用的固定版本；不会立即下载文件内容。
- Windows 消费者请求文件内容时才开始下载。通常是 Explorer 粘贴，也可能是剪贴板管理器主动读取，不能承诺只在用户按 Ctrl+V 时下载。
- 在资源管理器复制本地文件或目录，再回到对象列表按 **Ctrl+V**，文件进入现有上传队列并使用原有冲突选择。空目录创建零字节 S3 目录标记，使用禁止覆盖的上传条件；已存在的 marker 保留，兼容服务不支持安全条件上传时会报告失败。
- 本地文件即使来自 Ctrl+X，也只复制上传，绝不自动删除本地源文件。
- 应用内同连接复制、剪切继续使用原有服务端复制/移动队列及其服务商安全限制。Ctrl+X 只用于应用内，不向 Explorer 暴露“移动远端文件”的语义。
- 文字、URL、另一程序的虚拟文件不自动解释成文件上传。外部剪贴板一旦变化，不再使用此前的应用内对象选择。
- **编辑 → 取消文件剪贴板 / 下载** 会取消准备与本应用仍存活的剪贴板下载；只在剪贴板仍属于本应用时清空，不会删除另一程序后来复制的内容。

## 下载、生命周期与限制

- 文件通过 `FileGroupDescriptorW` + 索引 `FileContents`/`IStream` 提供，不使用尚未完成的 `CF_HDROP` 路径，也不把大文件读入内存。
- 空文件不设置“已知大小为零”的描述符标志，以免 Shell 仅凭描述符跳过内容请求；其 `FileContents` 请求也会验证源对象，即使消费者随后只调用 `Stat`。此行为仍须在真实 Explorer 验收。
- 每个文件先通过现有 S3 下载流水线完成长度、对象身份和可用校验和检查，再从磁盘提供内容。校验失败、断网、磁盘满、取消都返回错误，不伪装成 EOF 或成功的半文件。
- 这需要本机缓存磁盘空间，约为已请求文件的总大小。首个文件读取须等待该文件完整下载；不是边下载边向 Explorer 提供不完整内容。
- 剪贴板下载复用应用带宽限制和存储下载校验，不保存成可恢复队列任务，也不在传输队列计数中显示。进度显示在主窗口状态栏；退出应用会取消这类下载。
- 保持应用运行，包括托盘驻留。退出时不执行 `OleFlushClipboard`，不会为保存剪贴板强制提前下载全部文件。重启后需重新复制。
- 替换系统剪贴板只释放应用持有的旧对象，不中断已拿到旧 `IStream` 的粘贴。旧消费者释放后，缓存随对象回收清理；这依赖 COM 引用和 GC，不是即时删除保证。显式取消与退出会清理已完成/取消的下载。崩溃或文件被其他程序锁定可能留下 `%LOCALAPPDATA%\S3Explorer\ClipboardCache` 下的随机目录，程序不会把它当成可信剪贴板来源。
- 清单最多 10,000 项（含目录及中间目录），并限制分页发现。Windows 描述符相对路径最多 259 个 UTF-16 单元、单段最多 255；Explorer 最终目标路径还受 Windows 自身限制。
- 拒绝非法 Windows 字符、绝对路径、点路径、空路径段、尾部点/空格、保留设备名、无效 UTF-16、大小写碰撞和文件/目录碰撞，不静默改名。含内容的尾斜杠对象也不能伪装为空目录。无法导出时保留同连接内复制，并提示使用应用内下载。
- 清单是逐对象读取到的快照，不是整个 bucket 的事务快照。固定版本优先按版本下载；无固定版本时使用 ETag 和长度检查。ETag 不保证检测内容相同的 Metadata/ACL 变更。

## 实现要点

- `WindowsFileClipboardManifest` 负责精确 Key、递归清单、空目录和 Windows 路径映射。
- `WindowsClipboardDownloadSession` 懒下载、去重、只读独立游标、身份校验和私有缓存生命周期。
- `ShellFileDataObject` 直接实现 COM `IDataObject` 与 `IDataObjectAsyncCapability`，使用可移动 `HGLOBAL` 和有引用所有权的 `IStream`，在独立 STA 发布，不经过 WinForms 的数据对象包装。
- `MainForm.FileClipboard` 冻结来源连接/目录、识别剪贴板所有权、取消旧准备、条件发布新清单，避免旧异步完成覆盖新剪贴板。
- `CF_HDROP` 只用于本地→S3 输入。虚拟文件到 Explorer 一律首选 `DROPEFFECT_COPY`。

## 自动测试与真实 Explorer 验收

已有 Windows CI 会构建并运行新增测试，**但本分支未推送，因此没有本次代码的 CI 结果**：

    dotnet restore S3Explorer.sln --locked-mode
    dotnet build S3Explorer.sln -c Release --no-restore -warnaserror
    dotnet test S3Explorer.sln -c Release --no-build --no-restore

新增测试覆盖纯清单/路径、延迟下载/校验、HGLOBAL/COM vtable、真实 `OleSetClipboard → OleGetClipboard → IStream.Read` 跨线程往返、重复粘贴与旧所有权。OLE 往返测试不是 Explorer 端到端验收，不能代替以下步骤：

1. 使用 Windows 10/11 x64 的隔离测试账户及本地 MinIO 或假的存储服务，不使用生产凭据。在独立本地目标目录粘贴单文件、多文件、中文/空格文件名、零字节文件、嵌套目录与空目录，逐字节/哈希比较。
2. 重复粘贴到两个目录；在首次粘贴中途复制文本或另一组文件，确认旧粘贴继续且新粘贴使用新内容。复制后立即换连接/导航，确认来源不漂移。
3. 测试 >4 GiB 文件，分别使用单流、并行 Range 与不支持 Range 的服务；观察进程内存保持有界且缓存实际落盘。测试目标重名，确认 Explorer 自己的覆盖/跳过/取消选项生效。
4. 准备期间更换系统剪贴板、重复 Ctrl+C、取消；暂停下载后取消、断网、删除或替换源对象、模拟磁盘不足，确认有明确失败且没有成功的半文件。取消整个 Explorer 粘贴后，若存储读取仍在进行，可用应用的取消入口停止。
5. 本地文件/目录 Ctrl+C 或 Ctrl+X 后在 S3 当前目录 Ctrl+V，确认文件上传、空目录 marker、冲突策略、来源文件保留及目录联接拒绝。
6. 保持 Explorer 已打开一个流后更换剪贴板；释放流后检查缓存可回收。退出应用后粘贴应失败，不应触发提前下载或使用不存在的缓存。

只有上述 Explorer 步骤在真实 Windows 会话完成，才能声明资源管理器交互验收通过。

## 官方协议参考

- [Shell Clipboard Formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard)
- [Transferring Shell Data](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios)
- [IDataObjectAsyncCapability](https://learn.microsoft.com/en-us/windows/win32/api/shldisp/nn-shldisp-idataobjectasynccapability)
- [ReleaseStgMedium](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-releasestgmedium)
- [.NET COM Callable Wrapper](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/com-callable-wrapper)
