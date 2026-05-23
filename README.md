# 粉碎一切 (Shredder)

[![build](https://github.com/onniemi/Shredder/actions/workflows/build.yml/badge.svg)](https://github.com/onniemi/Shredder/actions/workflows/build.yml)

Windows 平台的安全文件粉碎工具 · C# / .NET 10 / WPF

> ⚠️ 本工具的操作**不可逆**。生产使用前请务必备份重要数据，并在测试目录验证行为。

## 功能

- ✅ 文件 / 文件夹 递归粉碎
- ✅ 多算法：单次随机、DoD 5220.22-M (3/7 Pass)、零填充 + 文件名随机化
- ✅ 资源管理器右键菜单集成（HKCU,无需管理员）
- ✅ 一键彻底清空回收站
- ✅ 驱动器空闲空间擦除
- ✅ 拖拽 + 二次确认 + 实时进度
- ✅ 命令行版本 `shredder.exe`(脚本 / CI / 远程会话友好)

## 目录结构

```
粉碎一切文件夹/
├── docs/设计文档.md
├── src/
│   ├── Shredder.sln
│   ├── Shredder.App/          # WPF 入口 (.NET 10)
│   ├── Shredder.Cli/          # 命令行入口 shredder.exe
│   ├── Shredder.Core/         # 算法 + 服务
│   ├── Shredder.Integration/  # 资源管理器右键集成
│   └── Shredder.Tests/        # xUnit 单元测试
└── README.md
```

## 编译运行

需要 Visual Studio 2022 (17.12+) 或 .NET 10 SDK。

```powershell
cd src
dotnet restore
dotnet build -c Release
dotnet run --project Shredder.App     # WPF GUI
dotnet run --project Shredder.Cli -- --help   # CLI
dotnet test  Shredder.Tests
```

## 命令行版本 (shredder.exe)

GUI 之外的等价入口,适合脚本、定时任务、远程 SSH/RDP 会话。共享同一份 `Shredder.Core` 服务,
所以**全部安全护栏**(系统目录黑名单、二次确认、SSD 默认 CryptoErase 等)都在 CLI 下同样生效。

```powershell
# 粉碎单个文件(默认按盘类型选算法:SSD→CryptoErase,HDD→Purge-3Pass)
shredder C:\path\to\secret.docx

# 多目标 + 指定算法 + 跳过 -y 二次确认
shredder D:\dir1 D:\dir2 --algo dod7 -y

# 清空回收站(含粉碎覆写)
shredder --empty-recycle -y

# 擦除盘符空闲空间
shredder --free-space D:\ -y

# 帮助 / 版本
shredder --help
shredder --version
```

### 算法别名 (`--algo` / `-a`)

| 别名 | 算法 ID | 用途 |
|---|---|---|
| `dod3` | `Purge-3Pass` | DoD 5220.22-M 3 趟 (HDD 默认) |
| `dod7` | `Purge-7Pass` | DoD 5220.22-M 7 趟 (高敏感) |
| `single` / `random` / `clear` | `Clear` | 单趟随机覆写 |
| `zero` / `zerofill` | `ZeroFill` | 单趟全零 (TRIM 友好) |
| `crypto` / `cryptoerase` | `CryptoErase` | 加密擦除 (SSD/NVMe 默认) |

也可直接传 ID;省略 `--algo` 时由 `ShredService` 按盘类型自动选择。

### 退出码

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 用法错误 / 路径不存在 / 通用失败 |
| 2 | 命中安全黑名单 (Forbidden) — `-y` **不能跳过** |
| 3 | 用户拒绝二次确认 |
| 4 | 多目标下部分失败 |
| 5 | 收到 Ctrl-C,操作中止 |

### 安全不变量(CLI 与 GUI 一致)

- `-y / --yes` 只能跳过 `Warn` 级别的二次确认,**无法**绕过 `Forbidden`(系统目录、盘符根)。
- 终端二次确认必须输入关键字「粉碎」(可在 `appsettings.json` 的 `Shredder:Ui:ConfirmationKeyword` 改);
  按回车或其它任何输入都视为取消。
- 日志默认把路径脱敏成 SHA-256 短哈希,通过 `Shredder:Logging:RecordRawPaths=true` 才会记录原始路径。

## 安装右键菜单

GUI 中打开 **设置 → 资源管理器右键菜单**，即可一键 **安装 / 卸载 / 刷新**，
状态栏会显示当前注册路径与实际 exe 是否一致。注册写入 `HKCU`，**不需要管理员权限**，
卸载会清理同一键。

如果需要在命令行/部署脚本中调用，对应 API 是 `Shredder.Integration.ShellMenuInstaller`
的 `Install` / `Uninstall` / `IsInstalled` / `GetInstalledExePath`。

## 算法对照

| 算法 | Pass | 用途 |
|---|---|---|
| 单次随机 | 1 | 现代大容量磁盘，速度优先 |
| DoD 5220.22-M (3) | 3 | 一般敏感数据，平衡 |
| DoD 5220.22-M (7) | 7 | 高敏感数据，机械硬盘 |
| 零填充 + 改名 | 1 | 应急/与 BitLocker 配合 |

## 重要限制

- **SSD**：由于磨损均衡和 FTL 映射，覆写**不保证**物理擦除。建议结合 BitLocker / Secure Erase。
- **云盘/网络盘**：本工具仅处理本地视图，云端副本不在保护范围内。
- **系统目录**：`%WINDIR%`、`Program Files`、驱动器根目录被硬编码禁止粉碎。

## 路线图

- [ ] MSIX 安装包
- [x] 命令行版本 `shredder.exe --algo dod7 <path>`
- [x] Serilog 操作日志 (路径默认脱敏为 SHA-256 短哈希,可通过 `Shredder:Logging:RecordRawPaths` 开关)
- [ ] 国际化 (i18n)
- [ ] SSD ATA Secure Erase 支持

## 基准 / 性能

仓库内置 [BenchmarkDotNet](https://benchmarkdotnet.org/) 工程 `src/Shredder.Benchmarks`，
覆盖单文件覆写算法对比和目录并发对比；复现方法、注意事项与结果模板见
[`docs/benchmarks/`](docs/benchmarks/README.md)。

注：本仓库不发布"比 XX 软件快多少"之类的横向比较结论。benchmark 仅用于在本机
横向对比本仓库内不同算法 / 不同配置，便于发现算法回归。

## 测试

`Shredder.Tests` 当前 **169 个单元 / 集成测试**全部通过，包含算法、配置注入、目录并发、
FreeSpace SSD 策略、回收站结构化结果、文件系统边界（ADS / Junction / 只读 / 占用文件）、
CLI E2E 与 publish smoke 等场景。本地复现：

```powershell
cd src
dotnet test Shredder.Tests -c Release
```
