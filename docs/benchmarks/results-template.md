# Benchmark 结果记录模板

> 每次跑分都复制本文件一份(例如 `results-2026-05-23-thinkpad-x1.md`),
> 把空白处填上即可。不要直接修改本模板。

---

## 测试环境

| 字段 | 值 |
| --- | --- |
| 记录日期 | `YYYY-MM-DD` |
| 记录人 | `name / GitHub handle` |
| 操作系统 | 例如 `Windows 11 Pro 23H2 (Build 22631.xxxx)` |
| CPU | 例如 `Intel Core i7-1260P @ 2.10 GHz` |
| 物理内存 | 例如 `32 GB DDR5-4800` |
| .NET SDK | `dotnet --version` 输出 |
| 测试盘类型 | `NVMe SSD / SATA SSD / HDD` |
| 测试盘文件系统 | `NTFS / ReFS` |
| 临时目录路径 | `Path.GetTempPath()` 实际值 |
| BitLocker | `开 / 关` |
| Defender 实时保护 | `开 / 关`,如开是否排除了临时目录 |
| 电源计划 | `平衡 / 高性能 / 笔记本插电状态` |

---

## 启动命令

```bash
# 实际敲的命令,包含 --filter 参数
dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --filter *
```

---

## 单文件覆写(SingleFileOverwriteBenchmark)

> 64 MB 临时文件,3 种单次覆写算法,Baseline = Clear。

| Method | Mean | Error | StdDev | Ratio | Allocated |
| --- | ---: | ---: | ---: | ---: | ---: |
| Clear (随机覆写) | | | | 1.00 | |
| ZeroFill (0x00 覆写) | | | | | |
| CryptoErase (AES-CTR) | | | | | |

**观察 / 异常 / 解读**:

-

---

## 多小文件目录(SmallFilesDirectoryBenchmark)

> 1000 个 4 KB 临时文件,ZeroFill 算法,Baseline = MaxConcurrentFiles=1。

| Method | Mean | Error | StdDev | Ratio | Allocated |
| --- | ---: | ---: | ---: | ---: | ---: |
| Serial (并发=1) | | | | 1.00 | |
| Parallel4 (并发=4) | | | | | |

**观察 / 异常 / 解读**(例如 HDD 上并发反而更慢、Defender 抽风等):

-

---

## 原始报告

> 把 `BenchmarkDotNet.Artifacts/results/*.md` 的内容粘贴到这里,
> 保留 BDN 自带的环境块(`BenchmarkDotNet=...`、`OS=...`、`Intel ...`),便于追溯。

```text

```

---

## 结论 / 后续行动

- [ ] 本次结果是否触发了某项算法 / 调度参数的调整?如有,链接到对应 PR。
- [ ] 是否需要更新默认配置(例如 SSD 默认并发上调)?
- [ ] 是否有发现需要补充的 benchmark 场景?
