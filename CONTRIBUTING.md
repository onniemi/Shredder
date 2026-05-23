# 贡献指南

感谢你帮这个项目变得更快、更安全、更可靠。

## 开发环境

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2022 17.12+ 或 Rider

```powershell
cd src
dotnet restore
dotnet build Shredder.sln -c Release
dotnet test Shredder.Tests\Shredder.Tests.csproj -c Release --no-build
```

## 代码原则

- 安全边界优先于功能速度：误删、路径绕过、日志泄露、粉碎失败静默吞掉都视为高优先级问题。
- Core 层必须可单测；GUI/CLI 只做编排和交互。
- 新算法必须有确定的测试策略：内容变化、长度保持、取消、异常恢复、边界长度。
- 所有不可逆操作都要经过 `PathSafetyGuard` 或等价护栏。
- 默认日志不得记录原始敏感路径，除非用户显式开启。

## PR 检查清单

- [ ] `dotnet build Shredder.sln -c Release` 通过。
- [ ] `dotnet test Shredder.Tests\Shredder.Tests.csproj -c Release --no-build` 通过。
- [ ] 涉及文件系统或安全边界时已补测试。
- [ ] 涉及用户可见行为时已更新 README。
- [ ] 没有提交 `bin/`、`obj/`、本地日志、截图、发布包等生成物。
