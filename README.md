# 粉碎一切 (Shredder)

[![build](https://github.com/onniemi/Shredder/actions/workflows/build.yml/badge.svg)](https://github.com/onniemi/Shredder/actions/workflows/build.yml)

Windows 平台的安全文件粉碎工具 · C# / .NET 10 / WPF

> ⚠️ 本工具的操作**不可逆**。正式使用前请务必备份重要数据，并在测试目录验证行为。

## 下载哪个版本

GitHub Release 同时提供简约版、完整版和轻量版，用户可以按自己的环境选择：

| 文件 | 适合谁 | 说明 |
|---|---|---|
| `shredder-app-*-win-x64-simple.zip` | 只想要最简单界面的用户 | WPF 简约界面，只显示文件/文件夹粉碎，自带 .NET 10 桌面运行时 |
| `shredder-app-*-win-x64-full.zip` | 想保留完整图形界面的用户 | WPF 完整界面，内置 .NET 10 桌面运行时，体积较大 |
| `shredder-app-*-win-x64-light.zip` | 已安装 .NET 10 Desktop Runtime 的用户 | WPF 完整界面，体积小，需要本机已有运行时 |
| `shredder-cli-*-win-x64-full.zip` | 脚本/服务器/远程环境 | 命令行版，内置 .NET 10 运行时 |
| `shredder-cli-*-win-x64-light.zip` | 已安装 .NET 10 Runtime 的脚本用户 | 命令行版，体积小，需要本机已有运行时 |

拿不准就下载 `app simple`。想要完整界面就下载 `app full`;想要最小体积，并且已经安装运行时，就下载 `light`。

## 功能

- ✅ 文件 / 文件夹 递归粉碎
- ✅ 多算法：单次随机、DoD 5220.22-M (3/7 Pass)、零填充 + 文件名随机化
- ✅ 拖拽 + 高风险路径确认 + 实时进度
- ✅ 命令行版本 `shredder.exe`(脚本 / CI / 远程会话友好)

## 目录结构

```
粉碎一切文件夹/
├── src/
│   ├── Shredder.sln
│   ├── Shredder.App/          # WPF 入口 (.NET 10)
│   ├── Shredder.Cli/          # 命令行入口 shredder.exe
│   ├── Shredder.Core/         # 算法 + 服务
│   ├── Shredder.Integration/  # 右键菜单集成 API
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
所以**全部安全护栏**(系统目录黑名单、高风险路径确认、SSD 默认 CryptoErase 等)都在 CLI 下同样生效。

```powershell
# 粉碎单个文件(默认按盘类型选算法:SSD→CryptoErase,HDD→Purge-3Pass)
shredder C:\path\to\secret.docx

# 多目标 + 指定算法
shredder D:\dir1 D:\dir2 --algo dod7 -y

# 清空回收站(CLI 可选命令,含粉碎覆写)
shredder --empty-recycle -y

# 擦除盘符空闲空间(CLI 可选命令)
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
| 3 | 用户拒绝高风险路径确认 |
| 4 | 多目标下部分失败 |
| 5 | 收到 Ctrl-C,操作中止 |

### 安全不变量(CLI 与 GUI 一致)

- 普通文件 / 目录会直接执行;只有命中 `Warn` 级别的高风险路径才需要确认。
- `-y / --yes` 只能跳过 `Warn` 级别的确认,**无法**绕过 `Forbidden`(系统目录、盘符根)。
- 终端确认必须输入关键字「粉碎」(可在 `appsettings.json` 的 `Shredder:Ui:ConfirmationKeyword` 改);
  按回车或其它任何输入都视为取消。
- 日志默认把路径脱敏成 SHA-256 短哈希,通过 `Shredder:Logging:RecordRawPaths=true` 才会记录原始路径。

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

## 测试

`Shredder.Tests` 当前 **178 个单元 / 集成测试**全部通过，包含算法、配置注入、目录并发、
FreeSpace SSD 策略、回收站结构化结果、文件系统边界（ADS / Junction / 只读 / 占用文件）、
CLI E2E 与 publish smoke 等场景。本地复现：

```powershell
cd src
dotnet test Shredder.Tests -c Release
```
