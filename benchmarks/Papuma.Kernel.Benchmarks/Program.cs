// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using BenchmarkDotNet.Running;

if (args is ["feed", ..])
{
    await Papuma.Kernel.Benchmarks.FeedThroughputProbe.RunAsync();
    return;
}

BenchmarkRunner.Run<Papuma.Kernel.Benchmarks.DiffEngineBenchmarks>(args: args);
