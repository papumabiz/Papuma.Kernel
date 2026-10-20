// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

namespace Papuma.Kernel.Benchmarks;

/// <summary>
/// What BenchmarkDotNet's <c>MemoryDiagnoser</c> cannot see, per scenario and pipeline:
/// <list type="bullet">
/// <item><b>allocated</b> — managed bytes allocated by one call (same quantity as the diagnoser, as a cross-check);</item>
/// <item><b>pooled</b> — bytes rented from <c>ArrayPool</c> during one call (<c>JsonDocument</c> metadata, transcoding
/// buffers): reused memory, so not allocated, but memory the call needs while it runs;</item>
/// <item><b>retained</b> — managed bytes still reachable through the result after a full GC: what a
/// <c>SaveResult</c> keeps alive (the current pipeline's stored state is the node tree of the new document; the
/// prototype's is the input text, which is not counted because it existed before the call).</item>
/// </list>
/// Run: <c>dotnet run -c Release -- diffmemory</c>.
/// </summary>
public static class DiffMemoryProbe
{
    private delegate DiffPipelineResult Pipeline(string? beforeText, string afterText, byte[]? beforeUtf8, byte[] afterUtf8);

    private static readonly (string Name, Pipeline Run)[] Pipelines =
    [
        ("current (text)", (b, a, _, _) => DiffPipelines.Current(b, a)),
        ("prototype (text)", (b, a, _, _) => DiffPipelines.PrototypeFromText(b, a)),
        ("prototype (utf8)", (_, _, b, a) => DiffPipelines.PrototypeFromUtf8(b, a)),
    ];

    public static void Run()
    {
        using var pool = new ArrayPoolListener();
        Console.WriteLine("| Scenario | Pipeline | allocated KB | pooled KB | retained KB | stored text KB |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|");
        foreach (var scenario in Enum.GetValues<DiffScenario>())
        {
            var (before, after) = DiffScenarios.Build(scenario);
            var beforeUtf8 = before is null ? null : Encoding.UTF8.GetBytes(before);
            var afterUtf8 = Encoding.UTF8.GetBytes(after);
            foreach (var (name, run) in Pipelines)
            {
                for (var i = 0; i < 5; i++)
                {
                    run(before, after, beforeUtf8, afterUtf8); // warm up: JIT, pools
                }

                const int calls = 10;
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < calls; i++)
                {
                    run(before, after, beforeUtf8, afterUtf8);
                }

                var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / calls;

                // Listening allocates per event, so it is switched on for this one call only.
                pool.Start();
                run(before, after, beforeUtf8, afterUtf8);
                var pooled = pool.Stop();

                // Heap with 20 results held, minus heap once they are dropped, per result: immune to garbage of
                // earlier calls and far above the heap counter's granularity.
                const int held = 20;
                var holder = new List<DiffPipelineResult>(held);
                for (var i = 0; i < held; i++)
                {
                    holder.Add(run(before, after, beforeUtf8, afterUtf8));
                }

                var withResults = FullGcHeap();
                holder.Clear();
                var retained = (withResults - FullGcHeap()) / held;
                GC.KeepAlive(holder);

                var storedText = name.StartsWith("prototype", StringComparison.Ordinal)
                    ? (name.EndsWith("(text)", StringComparison.Ordinal) ? after.Length * 2 : afterUtf8.Length)
                    : 0;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {scenario} | {name} | {allocated / 1024.0:F1} | {pooled / 1024.0:F1} | {retained / 1024.0:F1} | {storedText / 1024.0:F1} |"));
            }
        }
    }

    private static long FullGcHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    // ArrayPoolEventSource reports every rental with its buffer size (BufferRented, event id 1, payload index 1).
    private sealed class ArrayPoolListener : EventListener
    {
        private EventSource? _source;
        private long _rentedBytes;

        public void Start()
        {
            Interlocked.Exchange(ref _rentedBytes, 0);
            EnableEvents(_source ?? throw new InvalidOperationException("ArrayPoolEventSource not found."), EventLevel.Verbose);
        }

        public long Stop()
        {
            DisableEvents(_source!);
            return Interlocked.Read(ref _rentedBytes);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            {
                _source = eventSource;
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "BufferRented" && eventData.Payload is { Count: > 1 } payload && payload[1] is int size)
            {
                Interlocked.Add(ref _rentedBytes, size);
            }
        }
    }
}

/// <summary>
/// The JIT-cold first call: each measurement is a fresh process that runs one pipeline once (first call: JIT,
/// type loading, pool creation, cold caches) and once more (warm). Run: <c>dotnet run -c Release -- diffcold</c>.
/// </summary>
public static class DiffColdStartProbe
{
    private const int Processes = 7;
    private static readonly string[] PipelineNames = ["current", "prototype-text", "prototype-utf8"];
    private static readonly DiffScenario[] Scenarios = [DiffScenario.Small1Change, DiffScenario.Medium2Changes, DiffScenario.LargeFewChanges];

    public static void Run()
    {
        Console.WriteLine("| Scenario | Pipeline | first call µs (median of 7 processes) | second call µs |");
        Console.WriteLine("|---|---|---:|---:|");
        foreach (var scenario in Scenarios)
        {
            foreach (var pipeline in PipelineNames)
            {
                var samples = Enumerable.Range(0, Processes).Select(_ => MeasureInChild(pipeline, scenario)).ToList();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {scenario} | {pipeline} | {Median(samples.Select(s => s.First)):F0} | {Median(samples.Select(s => s.Second)):F0} |"));
            }
        }
    }

    /// <summary>The child side: build the inputs, then time the first and the second call.</summary>
    public static void RunChild(string pipeline, DiffScenario scenario)
    {
        var (before, after) = DiffScenarios.Build(scenario);
        var beforeUtf8 = before is null ? null : Encoding.UTF8.GetBytes(before);
        var afterUtf8 = Encoding.UTF8.GetBytes(after);
        Func<DiffPipelineResult> call = pipeline switch
        {
            "current" => () => DiffPipelines.Current(before, after),
            "prototype-text" => () => DiffPipelines.PrototypeFromText(before, after),
            _ => () => DiffPipelines.PrototypeFromUtf8(beforeUtf8, afterUtf8),
        };

        var first = Stopwatch.StartNew();
        call();
        var firstMicroseconds = first.Elapsed.TotalMicroseconds;
        var second = Stopwatch.StartNew();
        call();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{firstMicroseconds:F1} {second.Elapsed.TotalMicroseconds:F1}"));
    }

    private static (double First, double Second) MeasureInChild(string pipeline, DiffScenario scenario)
    {
        var self = Environment.ProcessPath!;
        var arguments = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{typeof(DiffColdStartProbe).Assembly.Location}\" diffcold-child {pipeline} {scenario}"
            : $"diffcold-child {pipeline} {scenario}";
        using var process = Process.Start(new ProcessStartInfo(self, arguments) { RedirectStandardOutput = true })!;
        var output = process.StandardOutput.ReadToEnd().Trim().Split(' ');
        process.WaitForExit();
        return (double.Parse(output[0], CultureInfo.InvariantCulture), double.Parse(output[1], CultureInfo.InvariantCulture));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
