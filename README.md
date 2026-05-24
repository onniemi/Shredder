# 粉碎一切 (Shredder)

[![build](https://github.com/onniemi/Shredder/actions/workflows/build.yml/badge.svg)](https://github.com/onniemi/Shredder/actions/workflows/build.yml)

Windows 平台的安全文件粉碎工具 · C# / .NET 10 / WPF

> ⚠️ 本工具的操作**不可逆**。正式使用前请务必备份重要数据，并在测试目录验证行为。

## 下载哪个版本

GitHub Release 提供简约界面和完整图形界面,每种界面各有 full / light 两个包：

- `shredder-simple-ui-*-win-x64-full.zip`:简约界面,自带 .NET 10,体积大
- `shredder-simple-ui-*-win-x64-light.zip`:简约界面,需要已安装 .NET 10 Desktop Runtime,体积小
- `shredder-full-ui-*-win-x64-full.zip`:完整图形界面,自带 .NET 10,体积大
- `shredder-full-ui-*-win-x64-light.zip`:完整图形界面,需要已安装 .NET 10 Desktop Runtime,体积小

拿不准就下载 `simple-ui full`。已经安装 .NET 10 Desktop Runtime 的用户可下载 `light` 小体积版。

## 功能

- ✅ 文件 / 文件夹 递归粉碎
- ✅ 多算法：单次随机、DoD 5220.22-M (3/7 Pass)、零填充 + 文件名随机化
- ✅ 拖拽 + 高风险路径确认 + 实时进度

## 目录结构

```
粉碎一切文件夹/
├── src/
│   ├── Shredder.sln
│   ├── Shredder.App/          # WPF 入口 (.NET 10)
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
dotnet test  Shredder.Tests
```

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
GUI publish smoke 等场景。本地复现：

```powershell
cd src
dotnet test Shredder.Tests -c Release
```
