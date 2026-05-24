# 更新日志

本项目采用 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式,版本号遵循 [SemVer](https://semver.org/lang/zh-CN/)。

`0.x` 阶段任何 minor 版本都可能包含破坏性变更,详见每个版本的 **Changed** / **Removed** 段。

<!--
  发版流程提示:
  1. 把 [Unreleased] 下的条目搬到新版本号下,顶部写日期(YYYY-MM-DD)。
  2. 在 [Unreleased] 留一个空骨架供下一轮使用。
  3. 推 v* tag 触发 .github/workflows/build.yml 的 release job。
-->

## [Unreleased]

### Added

- `IOptions<ShredderOptions>` 注入到所有 `IShredAlgorithm` 实现,`appsettings.json` 中 `Shredder:Io:BufferSizeBytes` / `FlushEveryNBuffers` / `ProgressReportIntervalMs` 现在真正影响粉碎吞吐与进度上报频率。
- 新增 `Shredder.Tests/ShredAlgorithmBaseIoOptionsTests.cs`:6 个单元测试覆盖 buffer 大小、进度节流、强制末段上报、按 N 缓冲块刷盘等行为,并验证 `CryptoEraseAlgorithm` 的 `ShredAsync` override 不破坏配置传递。
- **目录粉碎受控并发**:`Shredder:Io:MaxConcurrentFiles` 落到 `ShredService.ShredDirectoryAsync`。`1`(默认) 保持原串行行为;`>1` 走 `Parallel.ForEachAsync` 并以此为并发上限,任一文件抛异常会让 `ForEachAsync` 自动取消剩余分片。**目录删除阶段**始终串行——并发 unlink 父/子目录会产生竞态,而目录删除并不是性能瓶颈。新增 `Shredder.Tests/ShredServiceDirectoryConcurrencyTests.cs`(3 个测试):串行基线、峰值并发≤上限、取消信号在并发中能及时阻断。`ShredderIoOptions.MaxConcurrentFiles` 注释同步修正:旧版误写 `0=串行`,实际默认值是 `1=串行`,`<= 0` 会被 Options 校验拒绝。
- **空闲空间擦除 SSD 安全策略落地**:`FreeSpaceService.WipeAsync` 返回类型由 `Task` 改为 `Task<FreeSpaceWipeResult>`,在 SSD/NVMe 上按 `Shredder:FreeSpace:DisableOnSsd` / `FallbackToTrimOnSsd` 路由到 `TrimFallbackRunner`(`defrag <drive> /L`,ReTrim)或直接跳过;HDD/未知介质按原来的随机+零两 pass 覆写。每个块写入前主动 polling `DriveInfo.AvailableFreeSpace`,严格保留 `MinimumFreeBytesBuffer`,不再依赖 `ERROR_DISK_FULL` 兜底(那意味着已经写穿到 0 字节余量,仍保留 catch 作为并发兜底)。临时填充文件改名为 `~shred_freespace_{guid}.tmp`,允许并发 `WipeAsync` 同卷不撞名。新增 `Shredder.Tests/FreeSpaceServiceTests.cs`(8 个测试)覆盖 SSD+TRIM-fallback、SSD+无 fallback、SSD+用户禁用保护、HDD、Unknown 五条分支,以及 TRIM 退出码透出、临时文件名唯一性、不存在目录抛错。CLI `--free-space` 与 WPF `FreeSpacePageViewModel` 同步消费新的 `FreeSpaceWipeOutcome`,SSD-skip / TRIM 分支不再误把进度条拉到 100%。**重申**:软件覆写不保证 SSD 物理擦除,本路径仅在 HDD 或用户显式 `DisableOnSsd=false` 时才会真正写盘。
- **DI**:`ServiceCollectionExtensions` 新增 `services.AddSingleton<TrimFallbackRunner>()`,与 `SsdDetector` 同级注册,供 `FreeSpaceService` 注入。

### Changed

- **目标框架升级 .NET 8 → .NET 10 LTS**:`TargetFramework` 改为 `net10.0-windows`,Microsoft.Extensions.* 全部统一到 `10.0.8`,Serilog 主包升到 `4.3.0`(由 `Serilog.Extensions.Hosting 10.0.0` 拉起),`Serilog.Extensions.Hosting` / `Serilog.Settings.Configuration` 升到 `10.0.0`。GitHub Actions `setup-dotnet` 切到 `10.0.x`。WPF、CommunityToolkit.Mvvm、xUnit、FluentAssertions 等版本未动,降低破坏性变更面。CI 构建 0 warning / 0 error,169/169 测试通过。
- `ShredAlgorithmBase.BufferSize` 由编译期常量改为实例属性,默认值仍为 4 MB,可由配置覆盖。进度上报默认每 200 ms 节流一次,每个 pass 末尾强制上报最终进度。
- `DoD522022MAlgorithm` 构造签名扩展为 `(int passes = 3, IOptions<ShredderOptions>? options = null)`,DI 工厂转发 options。其余算法新增可选 `IOptions<ShredderOptions>?` 参数(默认 `null`),保持现有手工 `new` 调用与 169 个测试无需修改。
- 设置页新增资源管理器右键菜单管理:显示安装状态/当前 exe/已注册 exe,支持安装、卸载、刷新;`ShellMenuInstaller` 新增 `IsInstalled()` / `GetInstalledExePath()` 并补 10 个 HKCU 临时注册表测试,确保幂等且不污染真实右键菜单。
- 新增 `FileSystemBoundaryTests`:覆盖 ADS、符号链接文件、Junction 目录、只读/隐藏属性失败恢复、占用文件失败等 5 个边界场景；不支持 ADS 或无权限创建重解析点时对应测试路径会跳过。
- 回收站清空返回 `RecycleBinEmptyResult` 结构化结果,通过 `IRecycleBinEnumerator` / `IRecycleBinFileShredder` / `IRecycleBinShell` 隔离真实系统回收站;CLI/GUI 展示成功/失败/跳过/Shell HRESULT 摘要,失败项只记录脱敏路径哈希。新增 8 个 fake 测试,覆盖单项失败不中断、计数、Shell HRESULT、取消、路径脱敏和配置开关。
- 新增 CLI E2E 与 publish smoke:真实 `shredder.exe` 子进程覆盖 dry-run/explain、dry-run+report 不落盘、真实粉碎 JSON 报告、help/version;`dotnet publish` 覆盖 CLI 与 WPF App,验证可执行文件和 `appsettings.json` 产出。
- 新增 `src/Shredder.Benchmarks` BenchmarkDotNet 项目:覆盖单文件 Clear/ZeroFill/CryptoErase 对比、多小文件目录 `MaxConcurrentFiles=1` vs `=4` 对比;benchmark 默认不进入 CI,需手动 `dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj`。

### Fixed

- `CryptoEraseAlgorithmTests.DedicatedThreadSynchronizationContext.Dispose()` 不再在专用线程内部自行 `Dispose` `BlockingCollection`,避免 `foreach (...GetConsumingEnumerable())` 下一次 `MoveNext()` 抛 `ObjectDisposedException` 把测试宿主炸掉。§4.1 的进度节流改变了 async 续延时机,暴露出这条原本被时序掩盖的潜在竞态。

### Security

-

### Known limitations

- `Shredder:Io:UseUnbufferedIo` 配置项保留但暂未实装(需要扇区对齐 `FILE_FLAG_NO_BUFFERING` 支持),将在后续版本接入。
- Release 同时提供 4 个图形界面 zip:`shredder_simple.zip` / `shredder_full.zip` 为需要用户已安装 .NET 10 Desktop Runtime 的小体积包,`shredder_simple_net10.zip` / `shredder_full_net10.zip` 为自带 .NET 10 的大体积包。zip 内先放同名文件夹,文件夹内分别是 `shredder_simple.exe` 或 `shredder_full.exe`。

## [0.2.0] - 2026-05-24

首个面向外部用户的可分发版本。GUI、CLI、安全护栏、CI/Release 流水线全部落地。

### Added

- **CLI 入口 `shredder.exe`**:与 WPF GUI 共享同一份 `Shredder.Core`,适合脚本、定时任务、远程 SSH/RDP 会话。
- 算法别名:`dod3` / `dod7` / `single` / `random` / `clear` / `zero` / `zerofill` / `crypto` / `cryptoerase`,大小写与前后空白不敏感。
- 命令行子操作:`--empty-recycle`(清空回收站含粉碎覆写)、`--free-space <drive>`(擦除盘符空闲空间)。
- 多目标支持:一次命令传多个文件/目录,部分失败以退出码 4 区分。
- 退出码契约:`0` 成功、`1` 用法错误/通用失败、`2` 命中 Forbidden(`-y` 不能跳过)、`3` 用户拒绝二次确认、`4` 多目标部分失败、`5` 收到 Ctrl-C。
- GitHub Actions CI:`build` job 在 `windows-latest` 上跑 `dotnet restore` + `dotnet build -c Release` + `dotnet test`,任何 warning 因 `TreatWarningsAsErrors` 直接红。
- Tag 触发的 Release 流水线:推 `v*` tag 自动发布 win-x64 的 `shredder_simple.zip`、`shredder_simple_net10.zip`、`shredder_full.zip`、`shredder_full_net10.zip` 四个图形界面包,版本号从 tag 注入。
- `.github/` 仓库脚手架:`PULL_REQUEST_TEMPLATE.md`、`ISSUE_TEMPLATE/`(Bug Form + Feature Form + config redirect 到 SECURITY.md)。
- README 顶部 CI 构建徽章。

### Changed

- `Directory.Build.props` 启用 `TreatWarningsAsErrors` + `AnalysisLevel=latest-recommended`,确保 CI 在分析器告警上立刻失败而不是堆积。
- 默认算法按介质类型自适应:SSD/NVMe 用 `CryptoErase`,HDD 用 `Purge-3Pass`,可通过 `--algo` 覆盖。

### Security

- **路径硬黑名单**:`%WINDIR%`、`Program Files`、`Program Files (x86)`、驱动器根目录走 `PathSafetyGuard` 的 `Forbidden` 级别,**`--yes` / `-y` 无法绕过**。
- **二次确认关键字**:终端确认必须输入「粉碎」(可通过 `Shredder:Ui:ConfirmationKeyword` 改),回车或其它输入一律视为取消。
- **日志默认脱敏**:Serilog `PathRedactingEnricher` 把路径替换为 SHA-256 短哈希,只有显式设置 `Shredder:Logging:RecordRawPaths=true` 才记录原始路径。
- SSD/NVMe 物理擦除限制在 README、UI、SECURITY.md 三处显式声明,默认走 `CryptoErase` 而非软件覆写。

### Known limitations

- SSD/NVMe 因磨损均衡与 FTL 映射,软件覆写**不保证**物理擦除;依赖 BitLocker / ATA Secure Erase 才能逼近物理级别。
- 云盘 / 同步盘 / 卷影复制 / 备份软件中的远程副本不在本工具保护范围内。
- 发布包未做代码签名,首次运行 Windows SmartScreen 会提示警告。

[Unreleased]: https://github.com/onniemi/Shredder/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/onniemi/Shredder/releases/tag/v0.2.0
