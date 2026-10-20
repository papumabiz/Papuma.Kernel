// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using BenchmarkDotNet.Attributes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Benchmarks;

/// <summary>What one pipeline hands back: the public diff, the feed as UTF-8, and what backs <c>SaveResult.Stored</c>.</summary>
public sealed record DiffPipelineResult(DocumentDiff Diff, byte[] Feed, object Stored);

/// <summary>
/// The three write-path pipelines under test, with identical output requirements: a materialized
/// <see cref="DocumentDiff"/> (the public API object a <c>SaveResult</c> carries), the tracked wire form as UTF-8
/// (what goes to the <c>diff</c> column; Npgsql would encode the current pipeline's string the same way), and the
/// stored new state. No policies (the model has none, so <c>PolicyApplier</c> is a no-op for both).
/// </summary>
public static class DiffPipelines
{
    /// <summary>Today: <c>old.data::text</c>/<c>new.data::text</c> → JsonNode.Parse ×2 → Diff → ToJson → text.</summary>
    public static DiffPipelineResult Current(string? before, string after)
    {
        var beforeState = before is null ? null : (JsonObject)JsonNode.Parse(before)!;
        var afterState = (JsonObject)JsonNode.Parse(after)!;
        var diff = JsonDiffEngine.Diff(beforeState, afterState);
        return new DiffPipelineResult(diff, Encoding.UTF8.GetBytes(diff.ToJson().ToJsonString()), afterState);
    }

    /// <summary>The prototype on the same text the sessions read today (UTF-16 strings).</summary>
    public static DiffPipelineResult PrototypeFromText(string? before, string after)
    {
        var diff = Utf8JsonDiffEngine.Diff(before, after);
        return new DiffPipelineResult(diff.ToDocumentDiff(), diff.ToUtf8Json(), after);
    }

    /// <summary>The prototype on UTF-8 bytes (a session that would read <c>jsonb</c> as bytes; owned arrays).</summary>
    public static DiffPipelineResult PrototypeFromUtf8(byte[]? before, byte[] after)
    {
        var diff = before is null ? Utf8JsonDiffEngine.Insert(after) : Utf8JsonDiffEngine.Diff(before.AsMemory(), after.AsMemory());
        return new DiffPipelineResult(diff.ToDocumentDiff(), diff.ToUtf8Json(), after);
    }

    /// <summary>Throws unless all three pipelines produce the same entries and the same feed (as JSON values).</summary>
    public static void EnsureSameSemantics(DiffScenario scenario, string? before, string after)
    {
        var current = Current(before, after);
        foreach (var (name, candidate) in new[]
                 {
                     ("text", PrototypeFromText(before, after)),
                     ("utf8", PrototypeFromUtf8(before is null ? null : Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))),
                 })
        {
            var same = current.Diff.Paths.SequenceEqual(candidate.Diff.Paths, StringComparer.Ordinal)
                && current.Diff.Entries.All(e =>
                    candidate.Diff.Entries[e.Key] is var other
                    && e.Value.HasOld == other.HasOld && e.Value.HasNew == other.HasNew
                    && JsonNode.DeepEquals(e.Value.Old, other.Old) && JsonNode.DeepEquals(e.Value.New, other.New))
                && JsonNode.DeepEquals(JsonNode.Parse(current.Feed), JsonNode.Parse(candidate.Feed));
            if (!same)
            {
                throw new InvalidOperationException($"{scenario}: the {name} prototype disagrees with the current pipeline.");
            }
        }
    }
}

/// <summary>Whether every invocation reads the same input (cache-warm) or the next of many copies (cache-cold).</summary>
public enum DataTemperature
{
    Warm,
    Cold,
}

/// <summary>
/// End-to-end write-path diff: parse, diff, materialize, feed — same inputs, same outputs, same correctness rules
/// (verified in <see cref="Setup"/>). <c>Cold</c> rotates through copies of the inputs totalling more than the
/// last-level cache, so every invocation starts from memory instead of cache; code and JIT stay warm.
/// </summary>
[MemoryDiagnoser]
public class DiffPipelineBenchmarks
{
    private const long ColdBytesPerSide = 24L * 1024 * 1024; // > the 18 MB L3 of the reference i7-1260P

    private string?[] _before = [];
    private string[] _after = [];
    private byte[]?[] _beforeUtf8 = [];
    private byte[][] _afterUtf8 = [];
    private int _next;

    [ParamsAllValues]
    public DiffScenario Scenario { get; set; }

    [ParamsAllValues]
    public DataTemperature Data { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (before, after) = DiffScenarios.Build(Scenario);
        DiffPipelines.EnsureSameSemantics(Scenario, before, after);

        var copies = Data == DataTemperature.Warm
            ? 1
            : (int)Math.Clamp(ColdBytesPerSide / Encoding.UTF8.GetByteCount(after), 8, 50_000);
        _before = new string?[copies];
        _after = new string[copies];
        _beforeUtf8 = new byte[]?[copies];
        _afterUtf8 = new byte[copies][];
        for (var i = 0; i < copies; i++)
        {
            _before[i] = before is null ? null : new string(before.AsSpan()); // distinct instances, distinct memory
            _after[i] = new string(after.AsSpan());
            _beforeUtf8[i] = before is null ? null : Encoding.UTF8.GetBytes(before);
            _afterUtf8[i] = Encoding.UTF8.GetBytes(after);
        }
    }

    [Benchmark(Baseline = true, Description = "current (text)")]
    public DiffPipelineResult Current()
    {
        var i = Next();
        return DiffPipelines.Current(_before[i], _after[i]);
    }

    [Benchmark(Description = "prototype (text)")]
    public DiffPipelineResult PrototypeFromText()
    {
        var i = Next();
        return DiffPipelines.PrototypeFromText(_before[i], _after[i]);
    }

    [Benchmark(Description = "prototype (utf8)")]
    public DiffPipelineResult PrototypeFromUtf8()
    {
        var i = Next();
        return DiffPipelines.PrototypeFromUtf8(_beforeUtf8[i], _afterUtf8[i]);
    }

    private int Next()
    {
        var i = _next;
        _next = i + 1 == _after.Length ? 0 : i + 1;
        return i;
    }
}

/// <summary>
/// The pipelines taken apart, warm only. Current: <c>JsonNode.Parse</c> is lazy — the node tree is built by the first
/// full traversal, which in the real pipeline is the engine's own depth check — so C1 is the lazy parse, C2 adds that
/// traversal, C3 diffs already built trees, C4 writes the feed. Prototype: P1 parses and validates (root type, empty
/// keys), P2 diffs parsed documents including the copy into the owned buffer, P3 writes the feed, P4 materializes
/// the public <see cref="DocumentDiff"/>. Phases overlap at the edges; the end-to-end class is the authority on totals.
/// </summary>
[MemoryDiagnoser]
public class DiffPhaseBenchmarks
{
    private string? _beforeText;
    private string _afterText = string.Empty;
    private byte[]? _beforeUtf8;
    private byte[] _afterUtf8 = [];
    private JsonObject? _beforeTree;
    private JsonObject _afterTree = new();
    private DocumentDiff _currentDiff = DocumentDiff.Empty;
    private JsonDocument? _beforeDocument;
    private JsonDocument _afterDocument = JsonDocument.Parse("{}");
    private Utf8Diff _prototypeDiff = Utf8JsonDiffEngine.Diff("{}", "{}");

    [ParamsAllValues]
    public DiffScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        (_beforeText, _afterText) = DiffScenarios.Build(Scenario);
        DiffPipelines.EnsureSameSemantics(Scenario, _beforeText, _afterText);
        _beforeUtf8 = _beforeText is null ? null : Encoding.UTF8.GetBytes(_beforeText);
        _afterUtf8 = Encoding.UTF8.GetBytes(_afterText);

        _beforeTree = _beforeText is null ? null : (JsonObject)JsonNode.Parse(_beforeText)!;
        _afterTree = (JsonObject)JsonNode.Parse(_afterText)!;
        _currentDiff = JsonDiffEngine.Diff(_beforeTree, _afterTree); // also builds both trees

        _beforeDocument = _beforeUtf8 is null ? null : Utf8JsonDiffEngine.Parse(_beforeUtf8, "before");
        _afterDocument = Utf8JsonDiffEngine.Parse(_afterUtf8, "after");
        _prototypeDiff = Utf8JsonDiffEngine.Diff(_beforeDocument, _afterDocument);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _beforeDocument?.Dispose();
        _afterDocument.Dispose();
    }

    [Benchmark(Description = "C1 parse (lazy)")]
    public object CurrentParse() => (_beforeText is null ? null : JsonNode.Parse(_beforeText), JsonNode.Parse(_afterText));

    [Benchmark(Description = "C2 parse + build trees")]
    public bool CurrentParseAndBuild()
    {
        var before = _beforeText is null ? null : JsonNode.Parse(_beforeText);
        var after = JsonNode.Parse(_afterText);
        return JsonLimits.ExceedsMaxDepth(before) | JsonLimits.ExceedsMaxDepth(after);
    }

    [Benchmark(Description = "C3 diff (built trees)")]
    public DocumentDiff CurrentDiff() => JsonDiffEngine.Diff(_beforeTree, _afterTree);

    [Benchmark(Description = "C4 feed")]
    public byte[] CurrentFeed() => Encoding.UTF8.GetBytes(_currentDiff.ToJson().ToJsonString());

    [Benchmark(Description = "P1 parse + validate")]
    public int PrototypeParse()
    {
        using var before = _beforeUtf8 is null ? null : Utf8JsonDiffEngine.Parse(_beforeUtf8, "before");
        using var after = Utf8JsonDiffEngine.Parse(_afterUtf8, "after");
        return after.RootElement.GetPropertyCount();
    }

    [Benchmark(Description = "P2 diff (parsed docs)")]
    public int PrototypeDiff() => Utf8JsonDiffEngine.Diff(_beforeDocument, _afterDocument).Entries.Count;

    [Benchmark(Description = "P3 feed")]
    public byte[] PrototypeFeed() => _prototypeDiff.ToUtf8Json();

    [Benchmark(Description = "P4 materialize DocumentDiff")]
    public DocumentDiff PrototypeMaterialize() => _prototypeDiff.ToDocumentDiff();
}
