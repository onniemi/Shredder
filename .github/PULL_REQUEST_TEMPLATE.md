<!--
  谢谢贡献!提交前请按 CONTRIBUTING.md 走一遍检查清单。
  破坏性 / 不可逆 / 安全相关改动请单独说明。
-->

## 改动摘要

<!-- 一两句话说清:做了什么、为什么。链接相关 issue:Closes #123 -->

## 类型

<!-- 勾选所有适用项 -->

- [ ] Bug 修复(不破坏现有 API)
- [ ] 新功能(不破坏现有 API)
- [ ] 破坏性变更(请在下方说明影响面)
- [ ] 文档 / 注释
- [ ] 构建 / CI / 工具链
- [ ] 重构(无行为变化)

## 检查清单

- [ ] `dotnet build src/Shredder.sln -c Release` 通过(`TreatWarningsAsErrors` 下无新增警告)
- [ ] `dotnet test src/Shredder.Tests/Shredder.Tests.csproj -c Release --no-build` 通过
- [ ] 涉及文件系统 / 安全边界的改动已补单元测试
- [ ] 涉及用户可见行为的改动已更新 `README.md`
- [ ] 未提交 `bin/`、`obj/`、本地日志、截图、发布包

## 安全相关(如适用)

<!-- 勾选所有适用项;不适用可整段删除 -->

- [ ] 新增的不可逆操作仍走 `PathSafetyGuard`(系统目录 / 盘符根仍被 `Forbidden` 拒绝)
- [ ] `--yes` / `-y` **不能**绕过 `Forbidden` 级别的拦截
- [ ] 二次确认仍要求关键字「粉碎」(或通过 `Shredder:Ui:ConfirmationKeyword` 配置)
- [ ] 日志默认仍只记录路径 SHA-256 短哈希,仅在 `Shredder:Logging:RecordRawPaths=true` 时记录原始路径
- [ ] SSD/NVMe 相关限制在 UI 或文档中清晰可见

## 备注

<!-- 已知问题、后续工作、Reviewer 需要重点看的地方 -->
