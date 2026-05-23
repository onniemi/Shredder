# Shredder Benchmarks

本目录承载 `src/Shredder.Benchmarks`(基于 [BenchmarkDotNet](https://benchmarkdotnet.org/))的运行指引与结果归档。

> **结论先行**:这里不发布"比 XX 软件快多少"之类的横向比较结论。本项目的 benchmark
> 仅用来在 **本机** 横向对比 **本仓库内不同算法 / 不同配置**,以便在算法实现演进时
> 能够发现回归。任何跨机器、跨产品、跨场景的"更快"结论都需要单独的方法学评估,
> 不在本目录范围内。

---

## 设计原则

1. **benchmark 默认不进 CI**,也不会被 `dotnet test` 触发。
   - `Shredder.Benchmarks` 在 `.csproj` 中设置了 `<IsTestProject>false</IsTestProject>`。
   - 必须通过 `dotnet run -c Release --project ...` 显式启动。
2. **只使用临时目录**(`Path.GetTempPath()` 下的随机子目录),`[GlobalCleanup]` 负责清理。
   不会污染仓库工作区,也不会触碰用户已有数据。
3. **默认配置在几分钟内跑完**(`ShortRun` = 3 warmup + 3 iteration),避免 Benchmark 半小时起步。
4. **目标是相对差异**,不是绝对吞吐。所有结果都依赖具体硬件 / 文件系统 / 是否开启 Defender,
   请把硬件信息一同记录到 [results-template.md](results-template.md)。

---

## 如何运行

> 前提:仓库已经 `dotnet restore`,SDK ≥ .NET 10。

列出所有 benchmark:

```bash
dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --list flat
```

只跑单文件覆写场景:

```bash
dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --filter *SingleFileOverwriteBenchmark*
```

只跑目录并发场景:

```bash
dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --filter *SmallFilesDirectoryBenchmark*
```

跑全部 benchmark:

```bash
dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --filter *
```

如果需要"正式跑分"而非快速回归验证,把 `Program.cs` 中的 `Job.ShortRun` 改成
`Job.MediumRun` 或 `Job.Default`,代价是耗时显著增加。

运行结果默认写入当前工作目录下的 `BenchmarkDotNet.Artifacts/results/`,包含 `.csv / .md / .html`。
建议把对应的 `.md` 报告复制粘贴到 [results-template.md](results-template.md) 的"原始报告"段。

---

## benchmark 场景

| 场景文件 | 关心的问题 | 默认数据规模 |
| --- | --- | --- |
| `SingleFileOverwriteBenchmark.cs` | Clear / ZeroFill / CryptoErase 三种**单次覆写**算法在大文件上的相对成本 | 单个 64 MB 临时文件 |
| `SmallFilesDirectoryBenchmark.cs` | `MaxConcurrentFiles=1` vs `=4` 在大量小文件目录上的差异 | 1000 个 4 KB 临时文件 |

注意:`SingleFileOverwriteBenchmark` 直接调用 `IShredAlgorithm.ShredAsync`,
跳过了 `ShredService` 的路径校验 / ADS / 属性备份 / 改名删除等编排开销。
这样测的是"算法本体 + 物理 I/O",便于反映算法实现层面的差异。

`SmallFilesDirectoryBenchmark` 走完整的 `ShredService` 流程,以便如实暴露
并发调度 / 删除链路在小文件场景下的真实成本。

---

## 注意事项

- **Windows Defender 实时保护** 会让小文件 benchmark 大幅变慢,如果要观察"算法成本",
  建议临时把临时目录加入排除项(运行后再移除)。请在 results 中标注是否排除。
- **机械硬盘 vs SSD** 上的并发收益完全不同。HDD 上 `MaxConcurrentFiles>1` 可能反而变慢
  (随机寻道开销),这是预期行为,也是本 benchmark 要观察的事实之一。
- **不要把 benchmark 结果当作"算法等级"的依据**。算法选择的核心是**数据无法被恢复**,
  本项目 SSD 默认走 CryptoErase 也是因为这点,而非性能。

---

## 复现一致性 checklist

记录结果时,请把以下信息一起填到 [results-template.md](results-template.md):

- [ ] OS 版本(Windows 11 23H2 / 10 等)
- [ ] CPU 型号
- [ ] 物理内存
- [ ] 文件系统类型(NTFS / ReFS)及所在盘类型(SSD / HDD / NVMe)
- [ ] 是否开启 BitLocker
- [ ] Windows Defender 是否启用 / 临时目录是否在排除列表中
- [ ] .NET SDK 版本(`dotnet --version`)
- [ ] benchmark 启动命令完整 args
- [ ] 是否插电(笔记本)/电源计划
