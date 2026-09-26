// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

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

BenchmarkRunner.Run<Papuma.Kernel.Benchmarks.DiffEngineBenchmarks>(args: args);
