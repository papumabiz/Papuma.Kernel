// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

if (args is ["feed", ..])
{
    await Papuma.Kernel.Benchmarks.FeedThroughputProbe.RunAsync();
    return;
}

if (args is ["writepath", .. var writePathArgs])
{
    await Papuma.Kernel.Benchmarks.WritePathProbe.RunAsync(writePathArgs);
    return;
}

if (args is ["diffpipeline", .. var pipelineArgs])
{
    // Pinned to one physical P-core (logical CPUs 2+3 of the reference i7-1260P): a hybrid CPU otherwise moves
    // the process between P- and E-cores mid-run, which showed up as ±25–40 % error in the first measurements.
    // In-process, so the pinning applies to the measured code: BenchmarkDotNet's generated project cannot be
    // built here while stale worktree copies of this project exist (".claude/worktrees", ".kilo/worktrees").
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        self.ProcessorAffinity = 0b1100;
        self.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
    }

    var job = Job.Default
        .WithToolchain(BenchmarkDotNet.Toolchains.InProcess.Emit.InProcessEmitToolchain.Instance)
        .WithLaunchCount(1)
        .WithWarmupCount(5)
        .WithIterationCount(12);
    BenchmarkSwitcher
        .FromTypes([typeof(Papuma.Kernel.Benchmarks.DiffPipelineBenchmarks), typeof(Papuma.Kernel.Benchmarks.DiffPhaseBenchmarks)])
        .Run(pipelineArgs, DefaultConfig.Instance.AddJob(job));
    return;
}

if (args is ["diffverify"])
{
    foreach (var item in Enum.GetValues<Papuma.Kernel.Benchmarks.DiffScenario>())
    {
        var (before, after) = Papuma.Kernel.Benchmarks.DiffScenarios.Build(item);
        Papuma.Kernel.Benchmarks.DiffPipelines.EnsureSameSemantics(item, before, after);
        var result = Papuma.Kernel.Benchmarks.DiffPipelines.Current(before, after);
        Console.WriteLine($"{item,-28} before={(before?.Length ?? 0) / 1024.0,7:F1} KB  after={after.Length / 1024.0,7:F1} KB  entries={result.Diff.Entries.Count,5}  feed={result.Feed.Length / 1024.0,7:F1} KB  same");
    }

    return;
}

if (args is ["diffmemory"])
{
    Papuma.Kernel.Benchmarks.DiffMemoryProbe.Run();
    return;
}

if (args is ["diffcold"])
{
    Papuma.Kernel.Benchmarks.DiffColdStartProbe.Run();
    return;
}

if (args is ["diffcold-child", var pipeline, var scenario])
{
    Papuma.Kernel.Benchmarks.DiffColdStartProbe.RunChild(pipeline, Enum.Parse<Papuma.Kernel.Benchmarks.DiffScenario>(scenario));
    return;
}

BenchmarkRunner.Run<Papuma.Kernel.Benchmarks.DiffEngineBenchmarks>(args: args);
