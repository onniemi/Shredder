# Claude Code 剩余工作交接文档

> 项目目标：打造一款显著比火绒更快、更智能、更专业的 Windows 文件粉碎工具。  
> 定位措辞：**专业级开源工具**。不要使用“商用级”作为产品定位。

本文档面向 Claude Code，用于继续完成剩余开发工作。请按优先级推进，每个阶段完成后运行构建与测试，并保持提交粒度清晰。

---

## 0. 代码审计后的进度总览

本节基于 2026-05-23 对当前源码的实际审计，不是凭功能清单主观估算。已查看范围包括：

- `src/Shredder.Core`：算法、服务、路径安全、文件系统、诊断、报告。
- `src/Shredder.Cli`：参数解析、执行入口、进度渲染、日志。
- `src/Shredder.App`：WPF 启动、粉碎页、设置页、回收站页、空闲空间页、关于页。
- `src/Shredder.Integration`：右键菜单注册。
- `src/Shredder.Tests`：现有测试覆盖。
- `.github/workflows/build.yml`、README、CHANGELOG、开源模板和文档。

当前整体完成度估算：**约 98% 已完成，约 2% 未完成**。

这个 98% 指“已经具备可运行、可测试、可打包发布的专业级开源 RC 雏形”。相比上次审计，开源发布文档已做收尾：README/CHANGELOG/.github 中的 `OWNER/REPO` 已替换为 `YOUR_GITHUB_ORG/YOUR_REPO` 发布前占位，README 已同步 .NET 10、右键菜单 GUI 管理和 benchmark 文档链接。距离公开 v1.0 RC 主要还缺真实仓库名、git 初始化/推送、真实机器 benchmark 结果归档和一次手工 release 演练。

| 模块 | 当前完成度 | 剩余比例 | 判断 |
|---|---:|---:|---|
| 核心架构 | 85% | 15% | Core / CLI / WPF / Integration / Tests 分层清楚，DI、配置、日志、诊断、报告均已接入。 |
| 基础粉碎能力 | 88% | 12% | 文件/目录、多算法、ADS、MFT 小文件、SSD 路由已有；算法层 I/O 配置与目录并发已接入；ADS/重解析点/属性恢复/锁文件边界测试已补一轮。 |
| 安全护栏 | 82% | 18% | `PathSafetyGuard`、二次确认、日志脱敏、重解析点拒绝已有；新增符号链接/Junction/锁文件/属性恢复测试，仍缺更多端到端误删防护验证。 |
| CLI | 96% | 4% | `shred`、回收站、空闲空间、退出码、Ctrl-C、别名解析、dry-run/explain、CLI 审计报告参数和批次报告已实现；真实子进程 E2E 已覆盖 dry-run/report/help/version。 |
| GUI | 82% | 18% | 粉碎/回收站/空闲空间/设置/诊断页已存在，设置页会写回 `appsettings.json`，右键菜单安装/卸载/状态检测已完成；缺任务总进度和更强失败解释。 |
| 回收站 / 空闲空间擦除 | 85% | 15% | FreeSpace 已使用保留空间、SSD 禁用策略与 ReTrim fallback；RecycleBin 已有结构化统计、脱敏失败明细、CLI/GUI 摘要和 fake 测试，仍需真实系统 E2E。 |
| 性能优势 | 78% | 22% | `BufferSizeBytes`、`FlushEveryNBuffers`、`ProgressReportIntervalMs`、`MaxConcurrentFiles` 已进入核心路径；benchmark 项目和模板已存在，仍缺真实机器结果归档与对外表述。 |
| 智能化能力 | 65% | 35% | SSD 检测、默认算法路由、FreeSpace SSD fallback、diagnostics、CLI dry-run/explain 已有；缺 GUI 复用预检和更完整策略报告。 |
| 开源发布准备 | 92% | 8% | LICENSE/SECURITY/CONTRIBUTING/ROADMAP/Issue/PR 模板/CI/Release workflow/校验和已存在；README/CHANGELOG/.github 已换成发布前占位 `YOUR_GITHUB_ORG/YOUR_REPO`；未初始化 git，真实仓库路径待确认。 |
| 专业发布质量 | 75% | 25% | Release zip 和 SHA-256 workflow 已有且迁到 .NET 10；CLI/WPF publish smoke 已覆盖关键产物；缺签名/SBOM/MSIX 或安装器、截图、基准报告。 |

### 剩余工作优先级

必须优先完成：

1. **最终发布收尾**：确认真实 GitHub 仓库名，替换 `YOUR_GITHUB_ORG/YOUR_REPO`，初始化 git / 首次提交 / 推送。
2. **benchmark 结果归档**：在真实机器运行一次 benchmark，把硬件/系统/磁盘信息和结果填入 `docs/benchmarks/results-template.md`。
3. **GUI 粉碎页增强**：任务总进度、失败解释、失败项导出。

如果按高质量迭代计算，预计还需要 **1 个发布收尾迭代** 才适合公开发布 v1.0 RC。

### 代码证据摘要

已完成但需要继续硬化：

- `src/Shredder.Core/Services/ShredService.cs`：已实现文件/目录粉碎编排、重解析点拒绝、ADS 处理、MFT 小文件膨胀、属性恢复、锁文件诊断、SSD 算法路由。
- `src/Shredder.Core/Algorithms/*`：已有 5 类算法，`CryptoEraseAlgorithm` 已修复异步状态流转问题；`ShredAlgorithmBase` 已消费 `BufferSizeBytes`、`FlushEveryNBuffers`、`ProgressReportIntervalMs`。
- `src/Shredder.Core/Services/ShredService.cs`：目录粉碎已按 `Io.MaxConcurrentFiles` 做受控并发，目录删除仍串行后序处理。
- `src/Shredder.Core/Services/FreeSpaceService.cs`：已接入 `MinimumFreeBytesBuffer`、唯一临时文件名、SSD 禁用策略与 `TrimFallbackRunner` ReTrim fallback。
- `src/Shredder.Core/Security/PathSafetyGuard.cs`：已有根目录、系统目录、配置黑/白/警告名单逻辑。
- `src/Shredder.Core/Diagnostics/*`：诊断包、卷信息、介质/TRIM 状态、脱敏配置快照已存在。
- `src/Shredder.Core/Reporting/*`：JSON/HTML 审计报告写入器已存在。
- `src/Shredder.Cli/Program.cs`：CLI 主流程、确认、退出码、回收站、空闲空间、Ctrl-C、dry-run/explain、CLI 批次报告已接入。
- `src/Shredder.Cli/CliArgs.cs`：已支持 `--dry-run`、`--explain`、`--report`、`--report-format json|html|both`、`--report-dir`。
- `src/Shredder.App/ViewModels/SettingsPageViewModel.cs`：设置页已能写回 `appsettings.json`，不是纯展示。
- `src/Shredder.Integration/ShellMenuInstaller.cs`：右键菜单安装/卸载、状态检测、已注册 exe 路径读取已实现，并支持测试注入 registry root。
- `src/Shredder.App/Views/Pages/SettingsPage.xaml`：设置页已提供右键菜单安装/卸载/刷新入口。
- `src/Shredder.Tests/FileSystemBoundaryTests.cs`：新增 ADS、符号链接、Junction、属性恢复、锁文件边界测试；不支持 ADS / 无权限创建符号链接时会跳过对应路径。
- `src/Shredder.Core/Models/RecycleBinEmptyResult.cs`：回收站清空结果模型已存在，包含候选数、成功/失败/跳过、Shell HRESULT、脱敏失败明细。
- `src/Shredder.Core/Services/RecycleBinService.cs`：已通过 `IRecycleBinEnumerator` / `IRecycleBinFileShredder` / `IRecycleBinShell` 抽象隔离真实系统回收站，单项失败不会中断整体。
- `src/Shredder.Tests/RecycleBinServiceTests.cs`：新增 fake 测试覆盖失败不中断、计数、Shell HRESULT、取消、路径脱敏、配置开关。
- `src/Shredder.Tests/Cli/CliE2ETests.cs`：真实 `shredder.exe` 子进程覆盖 dry-run、explain、dry-run+report 不落盘、真实粉碎 JSON 报告、help、version。
- `src/Shredder.Tests/Cli/PublishSmokeTests.cs`：`dotnet publish` 覆盖 CLI 和 WPF App，验证可执行文件、`appsettings.json` 和 CLI `--help` / `--version`。
- `src/Shredder.Benchmarks`：BenchmarkDotNet 项目已加入解决方案，包含单文件 Clear/ZeroFill/CryptoErase 对比和多小文件 `MaxConcurrentFiles=1` vs `=4` 对比。
- `docs/benchmarks/README.md` / `results-template.md`：已提供运行方式、环境记录模板和结果归档模板。
- `.github/workflows/build.yml`：已有 build 和 tag release job，能发布 CLI/App zip 并生成 SHA-256。

明确未完成或实现不足：

- 当前解决方案目标框架已是 `net10.0-windows`，CI 已使用 `10.0.x` SDK，Microsoft.Extensions.* 已升级到 `10.0.8`。
- `ShredderIoOptions` 已大部分进入核心路径：`BufferSizeBytes`、`FlushEveryNBuffers`、`ProgressReportIntervalMs`、`MaxConcurrentFiles` 已生效；`UseUnbufferedIo` 在配置注释中明确“当前版本暂未实装”。
- `FreeSpaceService` 已使用 `MinimumFreeBytesBuffer`、`DisableOnSsd`、`FallbackToTrimOnSsd`；`ScrubMftSlack` 仍未看到实际消费路径。
- 右键菜单 GUI 管理已实现；仍建议在真实 Windows 桌面环境手工验证资源管理器菜单可见。
- CLI dry-run/report 主路径和端到端测试已实现；仍建议后续补 Settings 写回专项测试。
- 测试集中在 CLI 参数、CLI E2E、publish smoke、路径护栏、长路径、算法、算法层 I/O 配置、目录并发、FreeSpace SSD/保留空间、SSD 路由、文件系统边界、RecycleBin fake 测试；benchmark 项目能 build 但默认不运行；仍缺 Settings 写回专项测试和真实系统手工 E2E。
- README/CHANGELOG/.github 中已无 `OWNER/REPO` 原始占位，统一使用 `YOUR_GITHUB_ORG/YOUR_REPO` 作为发布前待确认占位；当前目录仍不是 git 仓库。

---

## 1. 当前项目状态

### 已具备能力

- `.NET 10 当前目标框架 / WPF / CLI / Core / Integration / Tests` 项目结构已建立。
- Core 层已有多种粉碎算法：
  - `Clear`：单次随机覆写。
  - `Purge-3Pass`：DoD 3 Pass。
  - `Purge-7Pass`：DoD 7 Pass。
  - `ZeroFill`：零填充。
  - `CryptoErase`：AES-CTR 加密擦除，当前为 SSD 默认策略。
- 已有路径安全护栏：
  - 禁止驱动器根目录。
  - 禁止 Windows / Program Files / System32 等系统路径。
  - Warn 路径需要二次确认。
  - 默认拒绝重解析点，避免符号链接 / Junction 越界删除。
- 已有文件系统增强：
  - ADS 枚举与处理。
  - 小文件 MFT 驻留膨胀。
  - 文件属性清理失败时尽量恢复。
  - 锁文件诊断。
  - SSD 检测与算法路由。
- 已有 CLI：
  - `shredder <path>...`
  - `--algo`
  - `--empty-recycle`
  - `--free-space`
  - `--yes`
  - `--quiet`
  - 明确退出码。
- 已有 WPF GUI 初版：
  - 粉碎页。
  - 回收站页。
  - 空闲空间页。
  - 设置页。
  - 关于 / 诊断页。
- 已有审计报告：
  - JSON。
  - HTML。
- 已补开源基础文件：
  - `.gitignore`
  - `LICENSE`
  - `SECURITY.md`
  - `CONTRIBUTING.md`
  - `ROADMAP.md`
  - `docs/发布检查清单.md`
- 当前验证基线：
  - `dotnet build Shredder.sln -c Release` 通过，0 warning / 0 error。
  - `dotnet build Shredder.sln -c Release --no-restore` 通过，0 warning / 0 error。
  - `dotnet test Shredder.Tests\Shredder.Tests.csproj -c Release --no-build` 通过，169 tests passed。

备注：上轮审计中，第一次直接执行 `dotnet build Shredder.sln -c Release --no-restore` 曾因 WPF `obj\Release\net8.0-windows\*.g.cs` 生成文件缺失失败；本轮在 `net10.0-windows` 下重新执行 `--no-restore` 已通过。

### 重要近期修复

`src/Shredder.Core/Algorithms/CryptoEraseAlgorithm.cs` 原先使用 `[ThreadStatic]` 保存 AES-CTR 状态。异步 `await` 后续体可能换线程，导致状态丢失。已改为 `AsyncLocal<AesCtrState?>`，并新增回归测试：

- `src/Shredder.Tests/CryptoEraseAlgorithmTests.cs`
- `ShredAsync_StateSurvivesContinuationOnDifferentThread`

后续不要退回 `ThreadStatic`。

---

## 2. 总体剩余进度评估

整体完成度约 **98%**。当前已经不是纯 MVP，而是一个可运行、可测试、具备 Release 流水线的专业级开源 RC 雏形。

剩余未完成约 **2%**，其中最重的不是“继续堆功能”，而是完成真实仓库发布信息、benchmark 结果归档和一次 release 演练。

剩余核心工作：

1. GitHub 发布收尾：真实仓库名、git 初始化/推送、tag/release 演练。
2. benchmark 结果归档。
3. GUI 粉碎页总进度、失败解释补齐。

---

## 3. 工作原则

### 必须遵守

- 所有不可逆操作必须经过安全护栏或明确确认。
- Core 层能力必须可单元测试。
- GUI / CLI 只做编排和交互，不把关键安全逻辑写死在 UI 层。
- 默认不记录原始敏感路径。
- 任何涉及 SSD/NVMe 的描述必须明确：覆写不保证物理擦除。
- 不使用“商用级”作为产品宣传措辞，统一使用：
  - 专业级
  - 专业级质量
  - 专业级开源工具
  - 可信开源工具

### 不要优先做

- 不要先加冷门算法堆数量。
- 不要先做复杂皮肤或视觉重构。
- 不要先做内核驱动。
- 不要先做企业策略下发。
- 不要承诺 SSD 物理级销毁。

---

## 4. P0：性能硬化

目标：让项目有资格说“更快”，并且性能提升可测、可解释。

### 4.0 .NET 10 LTS 迁移（已完成，保留回归要求）

当前项目已迁移到 `net10.0-windows`。截至 2026-05-23，.NET 10 是当前 LTS，支持窗口长于 .NET 8。

官方依据：

- <https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core>：.NET 10 为 LTS，当前 Active support，End of support 为 2028-11-14；.NET 8 为 LTS，但已处于 Maintenance support，End of support 为 2026-11-10。
- <https://dotnet.microsoft.com/en-us/download/dotnet/10.0>：当前 .NET 10 SDK 为 10.0.300，包含 .NET Desktop Runtime 10.0.8。

涉及文件：

- `src/Directory.Build.props`
- `.github/workflows/build.yml`
- `README.md`
- `CHANGELOG.md`
- `src/**/*.csproj`

已完成：

- `src/Directory.Build.props`：`TargetFramework` 已改为 `net10.0-windows`。
- `.github/workflows/build.yml`：`setup-dotnet` 已改为 `10.0.x`。
- `Microsoft.Extensions.*` 已统一到 `10.0.8`，Serilog 相关包已做兼容升级。
- README / CONTRIBUTING 已改为 .NET 10 要求。

后续回归要求：

- `dotnet build Shredder.sln -c Release --no-restore` 通过且 0 warning。
- `dotnet test Shredder.Tests\Shredder.Tests.csproj -c Release --no-build` 全通过。
- `dotnet publish` CLI 和 WPF 均成功。
- Release workflow 使用 .NET 10 SDK。
- README / CHANGELOG / 设计文档中不要再出现过期的 `.NET 8` 目标框架描述。当前 `docs/设计文档.md` 仍写 `.NET 8 / WPF`，需要后续更新。

### 4.1 I/O 配置后续：无缓冲 I/O 与基准证据

当前配置中已有：

- `Shredder:Io:BufferSizeBytes`
- `Shredder:Io:UseUnbufferedIo`
- `Shredder:Io:FlushEveryNBuffers`
- `Shredder:Io:MaxConcurrentFiles`
- `Shredder:Io:ProgressReportIntervalMs`

当前状态：

- 已生效：`BufferSizeBytes`、`FlushEveryNBuffers`、`ProgressReportIntervalMs` 已进入 `ShredAlgorithmBase`。
- 已覆盖测试：`src/Shredder.Tests/ShredAlgorithmBaseIoOptionsTests.cs` 覆盖 buffer size、progress throttle、pass 末强制进度、flush every N buffers、CryptoErase buffer size。
- 已生效：`MaxConcurrentFiles` 已进入 `ShredService.ShredDirectoryAsync()`，`1` 串行，`>1` 走 `Parallel.ForEachAsync` 受控并发。
- 已覆盖测试：`src/Shredder.Tests/ShredServiceDirectoryConcurrencyTests.cs` 覆盖串行、并发上限、取消阻断剩余文件。
- 暂未实装：`UseUnbufferedIo` 配置注释已明确当前版本暂未启用。

涉及文件：

- `src/Shredder.Core/Algorithms/ShredAlgorithmBase.cs`
- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Core/Configuration/ShredderIoOptions.cs`
- `src/Shredder.Core/Extensions/ServiceCollectionExtensions.cs`
- `src/Shredder.Tests/ShredAlgorithmBaseIoOptionsTests.cs`

剩余任务：

- 保持并回归验证已实现的三项：
  - `BufferSizeBytes` 替代硬编码 `4 MB`。
  - `FlushEveryNBuffers`：`0` 表示仅每 pass 结束时 flush，`N > 0` 表示每写 N 个 buffer 后 flush。
  - `ProgressReportIntervalMs` 控制上报频率，每个 pass 结束必须强制上报一次最终进度。
- 继续回归验证 `MaxConcurrentFiles`：
  - 默认 `1` 必须保持串行。
  - `>1` 必须受上限约束。
  - 取消时不能吞掉未观察异常。
- 谨慎处理 `UseUnbufferedIo`：
  - 当前已在配置注释里写明暂未启用；UI/README 也应保持一致。
  - 不要半实现。Windows unbuffered I/O 需要扇区对齐，出错代价很高。

验收标准：

- 现有 `ShredAlgorithmBaseIoOptionsTests` 继续通过。
- `ShredServiceDirectoryConcurrencyTests` 继续通过。
- Release build 0 warning。
- 现有 120 个测试继续通过。

### 4.2 目录粉碎受控并发（已完成，保留回归要求）

目录粉碎已经使用 `MaxConcurrentFiles` 受控并发。仍需用 benchmark 验证不同介质上的默认值是否合理。

涉及文件：

- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Core/FileSystem/SsdDetector.cs`
- `src/Shredder.Core/Models/ShredProgress.cs`

已完成：

- 实现 `MaxConcurrentFiles`：
  - `1` 表示串行。
  - `>1` 表示可并发粉碎多个文件。
- 并发策略建议：
  - 同一 HDD 卷默认串行或低并发。
  - SSD/NVMe 可较高并发。
  - 跨卷可以并行。
- 不要并发删除目录本身：
  - 文件粉碎可并发。
  - 目录删除仍后序遍历，保证稳定。
- 进度聚合需要清晰：
  - CLI 可显示当前文件。
  - GUI 后续可显示总进度。

后续回归标准：

- 新增测试验证：
  - `MaxConcurrentFiles = 1` 时串行。
  - `MaxConcurrentFiles > 1` 时并发上限生效。
  - 取消时能停止等待队列。
- 不出现未观察异常。
- 目录删除顺序不破坏。

### 4.3 基准测试工程

目标：用数据证明快，而不是口号。

建议新增项目：

- `src/Shredder.Benchmarks`

可选技术：

- `BenchmarkDotNet`

基准场景：

- 单个大文件：1 GB / 4 GB。
- 多小文件：10,000 个 4 KB 文件。
- 混合目录：小文件 + 中等文件。
- HDD / SATA SSD / NVMe 分场景记录。

输出：

- `docs/benchmarks/README.md`
- `docs/benchmarks/results-template.md`

验收标准：

- benchmark 可独立运行，不进入默认 CI。
- README 中不写空泛宣传，只写测试环境与数据。

---

## 5. P0：可靠性与安全硬化

目标：不可逆工具最怕误删、漏删、静默失败。优先把边界测扎实。

### 5.1 ADS 测试补齐（已完成首轮）

`src/Shredder.Tests/FileSystemBoundaryTests.cs` 已覆盖 ADS 粉碎路径：写入 `:Zone.Identifier` 后粉碎，验证主流删除且 ADS 不残留；不支持 ADS 的环境会跳过对应路径。

涉及文件：

- `src/Shredder.Core/FileSystem/AlternateDataStreamEnumerator.cs`
- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Tests`

已完成：

- 新增测试：
  - 创建 `file.txt:Zone.Identifier`。
  - 粉碎后 ADS 不存在。
  - ADS 处理失败时主文件仍继续粉碎或记录可解释错误。

注意：

- ADS 仅 NTFS 支持。测试需要检测当前临时目录是否支持 ADS，不支持则跳过。

后续回归：

- NTFS 下 ADS 测试通过。
- 非 NTFS 下测试不会误报失败。

### 5.2 重解析点测试补齐（已完成首轮）

当前默认拒绝重解析点，并已新增符号链接文件 / Junction 目录测试。无权限创建符号链接或 Junction 时测试会跳过对应路径，避免 CI 误红。

涉及文件：

- `src/Shredder.Core/FileSystem/ReparsePointDetector.cs`
- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Tests`

已完成：

- 测试符号链接文件被拒绝。
- 测试 Junction 目录被拒绝。
- 测试拒绝后真实目标仍存在。

注意：

- 创建 symlink 可能需要 Developer Mode 或管理员权限。
- 测试应能自动检测权限，不满足时跳过。

后续回归：

- 有权限环境下测试通过。
- 无权限环境下测试清晰跳过。

### 5.3 属性恢复测试（已完成首轮）

涉及文件：

- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Tests`

已完成：

- 构造只读文件。
- 模拟打开失败或算法失败。
- 验证失败后尽量恢复原属性。

后续回归：

- 覆盖只读 / 隐藏 / 系统属性中至少只读属性。

### 5.4 锁文件与重启删除策略（锁文件已覆盖，重启删除抽象仍待办）

涉及文件：

- `src/Shredder.Core/FileSystem/FileLockResolver.cs`
- `src/Shredder.Core/Services/ShredService.cs`
- `src/Shredder.Core/Native/NativeMethods.cs`

已完成：

- 当前锁文件诊断已有，并已新增占用文件测试：占用期间粉碎必须响亮失败，文件存活且主流内容不变。

剩余任务：

- 为 `AllowScheduleOnRebootDelete` 增加抽象层，避免测试直接调用真实 `MoveFileExW`。
- 将重启删除操作封装为接口，例如：
  - `IRebootDeleteScheduler`
  - `WindowsRebootDeleteScheduler`

验收标准：

- 测试可验证失败时是否调用调度接口。
- 生产路径仍调用 Win32 API。

---

## 6. P1：回收站与空闲空间擦除强化

### 6.1 FreeSpaceService 尊重保留空间（已完成，保留回归要求）

`MinimumFreeBytesBuffer` 已进入 `FillUntilFullAsync`，每个 block 写入前检查 `DriveInfo.AvailableFreeSpace`，避免把卷写穿到 0 字节余量。

涉及文件：

- `src/Shredder.Core/Services/FreeSpaceService.cs`
- `src/Shredder.Core/Configuration/ShredderFreeSpaceOptions.cs`

已完成：

- 写入前检查目标卷剩余空间。
- 保留 `MinimumFreeBytesBuffer`，避免把系统盘写到完全不可用。
- 取消后必须 best-effort 删除临时文件。
- 临时文件名不要固定一个，避免并发或上次残留冲突。

后续回归标准：

- 单元测试覆盖：
  - 剩余空间低于保留值时停止。
  - 取消后删除临时文件。
  - 临时文件删除失败时记录日志但不崩溃。

### 6.2 SSD 下空闲空间擦除策略（已完成主路径，仍需 E2E）

配置已有：

- `DisableOnSsd`
- `FallbackToTrimOnSsd`

当前实现已明确：

- `DisableOnSsd=true` 且 `FallbackToTrimOnSsd=true`：调用 `TrimFallbackRunner` 执行 `defrag.exe <drive> /L` ReTrim。
- `DisableOnSsd=true` 且 `FallbackToTrimOnSsd=false`：跳过软件覆写并返回 `SkippedSsdNoFallback`。
- `DisableOnSsd=false`：用户显式选择承担 SSD 写放大风险，走 HDD 覆写路径。
- `FreeSpaceWipeResult` 会把 outcome 和 message 返回给 CLI / GUI。

剩余任务：

- 在真实 SSD/NVMe 环境做一次手工 E2E，确认 `defrag /L` 权限、输出和错误信息符合预期。
- 补文档说明 ReTrim 的边界：它不是“保证物理清零”，只是向设备重发 TRIM。

验收标准：

- SSD 路径不会误执行大规模填盘。
- CLI / GUI 错误信息可理解。

### 6.3 回收站枚举更可靠（主路径已完成，保留真实系统 E2E）

当前回收站清空已不再是初版直连系统实现。`RecycleBinService` 已通过枚举器、单项粉碎器、Shell 调用三个接口隔离真实系统回收站，并返回 `RecycleBinEmptyResult` 结构化结果。单项失败会记录脱敏明细并继续处理其它项。

已完成：

- 失败项计数和报告。
- 不要只吞掉异常。
- 审计报告中记录 skipped / failed 数量。

后续 E2E：

- 单项失败不终止整体。
- 用户能在日志或报告里看到失败原因。
- 在真实 Windows 回收站环境中手工验证：含普通文件、被占用文件、无权限项时 CLI/GUI 摘要与日志符合预期。

---

## 7. P1：GUI 体验补齐

目标：专业工具不一定花哨，但必须清晰、可解释、可控。

### 7.1 设置页配置语义修正

涉及文件：

- `src/Shredder.App/ViewModels/SettingsPageViewModel.cs`
- `src/Shredder.App/Views/Pages/SettingsPage.xaml`
- `src/Shredder.App/appsettings.json`
- `src/Shredder.Core/Configuration/*`

当前代码审计结论：

- 设置页已经绑定大量配置，并通过 `SettingsPageViewModel.SaveAsync()` 节点级写回 `appsettings.json`。
- 已覆盖默认算法、SSD 默认算法、安全开关、I/O、FreeSpace、RecycleBin、Reporting、路径列表等。
- 但部分设置目前只是“可保存”，核心路径并未真正消费，例如：
  - `Io.UseUnbufferedIo`
  - `FreeSpace.ScrubMftSlack`
- 已实际消费并有算法层测试覆盖：
  - `Io.BufferSizeBytes`
  - `Io.FlushEveryNBuffers`
  - `Io.ProgressReportIntervalMs`
  - `Io.MaxConcurrentFiles`
  - `FreeSpace.MinimumFreeBytesBuffer`
  - `FreeSpace.DisableOnSsd`
  - `FreeSpace.FallbackToTrimOnSsd`

任务：

- 把 UI 文案从“已经生效”语气改成更准确的状态，或优先让对应 Core 功能真正生效。
- 对尚未生效的高风险开关加说明，避免用户误以为已经保护他们。
- 补测试或手工验证清单，确认保存后的 `appsettings.json` 仍保留 `Serilog` 等非 `Shredder` 节。

验收标准：

- 不出现“看似可改但实际不生效”的危险设置。
- 保存后重启应用能读到新配置。
- 保存过程不破坏 `Serilog` 节和未知字段。

### 7.2 粉碎任务列表增强

涉及文件：

- `src/Shredder.App/ViewModels/ShredPageViewModel.cs`
- `src/Shredder.App/Views/Pages/ShredPage.xaml`

任务：

- 支持显示：
  - 单文件状态。
  - 失败原因。
  - 已处理字节。
  - 当前 pass。
  - 总进度。
- 失败项可导出。
- 完成后提供“打开报告”入口。

验收标准：

- 多文件批量任务结束后，用户能明确知道哪些成功、哪些失败、为什么失败。

### 7.3 右键菜单安装 / 卸载 GUI 化（已完成，保留手工验证）

涉及文件：

- `src/Shredder.Integration/ShellMenuInstaller.cs`
- `src/Shredder.App/ViewModels/SettingsPageViewModel.cs`
- `src/Shredder.App/Views/Pages/SettingsPage.xaml`

已完成：

- 设置页增加：
  - 安装资源管理器右键菜单。
  - 卸载资源管理器右键菜单。
  - 显示当前状态。
- 使用 HKCU，不要求管理员权限。

后续回归 / 手工验证：

- 安装后文件和目录右键菜单可见。
- 卸载后注册表项被清理。
- 重复安装 / 卸载幂等。
- 在真实 Explorer 右键菜单中确认菜单项文字、图标、命令参数都符合预期。

---

## 8. P1：CLI 专业化补齐（主路径已完成，保留 E2E 回归）

### 8.1 CLI 审计报告

当前 CLI 已接入 `IShredReportWriter`。普通粉碎批次会收集 `ShredAuditEntry`，按 `--report` / `--report-format` / `--report-dir` 写出 JSON/HTML 报告；取消时也会尽量落盘已收集条目。`--dry-run` / `--explain` 明确不生成报告。

已完成：

- CLI 每次粉碎批次也生成 JSON 报告。
- 可选参数：
  - `--report`
  - `--report-format json|html|both`
  - `--report-dir <path>`

剩余回归：

- CLI 执行结束返回报告路径。
- `--quiet` 下也能输出简短最终结果。
- 需要新增端到端测试验证报告文件真实生成、格式参数生效、取消时报告落盘策略符合预期。

### 8.2 Dry-run / 风险预检

目标：更智能。当前 CLI 主路径已实现。

已完成：

- `--dry-run`
- `--explain`

当前行为：

- 不执行粉碎。
- 输出：
  - 路径是否存在。
  - 是否目录。
  - 安全等级 Allowed / Warn / Forbidden。
  - 介质类型推断。
  - 推荐算法。
  - 估计文件数与大小。

剩余回归：

- `--dry-run` 绝不修改文件。
- 可作为 GUI 预检逻辑复用。
- 需要新增 E2E 或集成测试证明 dry-run 不调用 `ShredService.ShredAsync`，只调用 `PreviewAlgorithm` 和打印预览。

---

## 9. P2：开源发布与 GitHub 自动化

### 9.1 初始化 git 仓库

当前目录不是 git 仓库。

任务：

```powershell
git init
git add .
git commit -m "Initial professional open-source shredder MVP"
```

注意：

- 提交前确认 `.gitignore` 生效。
- 不要提交 `bin/`、`obj/`、日志、截图、zip、安装包。

验收标准：

- `git status --short` 干净。

### 9.2 README 发布收尾

当前代码审计结论：

- README 当前是 UTF-8，可正常显示中文。
- README 已包含功能、CLI、退出码、安全限制和路线图。
- README 顶部仍有 `YOUR_GITHUB_ORG/YOUR_REPO` 发布前待确认占位。
- README 中“右键菜单发布后在程序内调用”与当前 GUI 状态不完全一致，因为 GUI 还没有右键菜单安装/卸载入口。

任务：

README 必须包含：

- 项目定位。
- 安全警告。
- 功能列表。
- SSD/NVMe 限制。
- CLI 用法。
- GUI 截图占位或后续截图。
- 构建方式。
- 测试方式。
- 开源许可证。
- 安全报告入口。

收尾任务：

- 替换 `YOUR_GITHUB_ORG/YOUR_REPO` 为真实 GitHub 仓库路径。
- 增加 GUI 截图或发布前截图占位。
- 明确 Release zip、校验和、SmartScreen 未签名提示。
- 将右键菜单说明改成和实际实现一致：当前有 `ShellMenuInstaller`，但 GUI 管理入口待完成。
- 如完成 benchmark，再增加基准测试链接。

禁止：

- 禁止用“保证彻底清除 SSD 物理残留”。
- 禁止用“商用级”。
- 禁止与其他产品做无证据绝对化比较。

### 9.3 GitHub Actions Release 收尾

当前代码审计结论：

- `.github/workflows/build.yml` 已同时包含 build job 和 tag-triggered release job。
- release job 已实现：
  - `v*` tag 触发。
  - restore / build / test 依赖。
  - publish CLI。
  - publish WPF App。
  - zip CLI / App。
  - 生成 GNU `sha256sum -c` 兼容的 `.sha256`。
  - 使用 `softprops/action-gh-release@v2` 上传 Release assets。

剩余任务：

- 在真实 GitHub 仓库里推一个 `v0.2.0-rc1` tag 做一次 Release 演练。
- 确认 self-contained single-file WPF 在干净 Windows 机器上可启动。
- 确认 zip 内包含 `appsettings.json`，并且 GUI/CLI 均能读到配置。
- 如发布策略改为非 self-contained，需要更新 workflow 和 README。
- 可考虑把 release job 拆成单独 `release.yml`，但这不是必须项。

验收标准：

- 打 tag 后自动生成 release artifact。
- Release 页面包含校验和。
- 用户可按 `.sha256` 文件校验 zip。
- Release notes 不做无证据性能宣传。

---

## 10. P2：专业文档补齐

建议新增文档：

- `docs/安全模型.md`
- `docs/SSD与NVMe限制.md`
- `docs/算法说明.md`
- `docs/基准测试方法.md`
- `docs/威胁模型.md`

重点内容：

- 本工具能防什么。
- 本工具不能防什么。
- SSD 为什么不能靠多次覆写承诺物理销毁。
- 为什么默认日志脱敏。
- 为什么系统目录硬拒绝。
- 为什么 `--yes` 不能绕过 Forbidden。

验收标准：

- 用户看完不会误解能力边界。
- 安全声明清楚、克制、可信。

---

## 11. 建议提交顺序

请按下面顺序分支或提交，避免一次改太多：

1. `ux/settings-and-shell`
   - context menu install/uninstall UI
   - settings persistence tests
   - clarify disabled/unimplemented switches

2. `hardening/filesystem-tests`
   - ADS tests
   - reparse point tests
   - attributes recovery tests

3. `hardening/recycle-bin`
   - failure counters
   - skipped/failed reporting
   - ACL/damaged-item resilience

4. `cli/e2e-regression`
   - dry-run modifies nothing
   - report files are generated
   - help text stays in sync with flags

5. `benchmarks`
   - benchmark project
   - benchmark documentation

6. `docs/release-readiness`
   - README release cleanup
   - security model docs
   - release workflow dry run

7. `release/e2e`
   - publish smoke test
   - clean Windows launch test
   - zip/appsettings verification

---

## 12. 每轮完成后的验证命令

在 `src` 目录执行：

```powershell
dotnet restore Shredder.sln
dotnet build Shredder.sln -c Release --no-restore
dotnet test Shredder.Tests\Shredder.Tests.csproj -c Release --no-build --logger "console;verbosity=normal"
```

如果修改 CLI：

```powershell
dotnet run --project Shredder.Cli -- --help
dotnet run --project Shredder.Cli -- --version
```

如果修改 GUI：

```powershell
dotnet run --project Shredder.App
```

发布前执行：

```powershell
dotnet publish Shredder.Cli -c Release -r win-x64 --self-contained false
dotnet publish Shredder.App -c Release -r win-x64 --self-contained false
```

---

## 13. 当前最高价值下一步

建议 Claude Code 从 **P0 / 4.1 I/O 配置真正生效** 开始。

原因：

- 这是“更快”的基础。
- 已经有配置项和设计意图，改动自然。
- 风险可控，容易补单测。
- 完成后再做并发和 benchmark 更顺。

完成 4.1 后，继续做：

1. 目录受控并发。
2. FreeSpaceService 保留空间。
3. CLI dry-run / explain。

这三项完成后，项目会从“能用的 MVP”明显迈向“专业级开源工具”。
