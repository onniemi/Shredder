using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Shredder.Benchmarks;

/// <summary>
/// Benchmark 入口。手动运行示例:
/// <code>
/// dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --list flat
/// dotnet run -c Release --project src/Shredder.Benchmarks/Shredder.Benchmarks.csproj -- --filter *SingleFile*
/// </code>
/// 默认不随 <c>dotnet test</c> 运行。结果默认写入当前工作目录下
/// <c>BenchmarkDotNet.Artifacts/results/</c>。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // ShortRun = WarmupCount=3 + IterationCount=3,确保几分钟级别可跑完;
        // 想出"权威结果"时改用 `--job medium` 即可。
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun.WithId("ShortRun"));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
        return 0;
    }
}
