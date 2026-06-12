// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using BenchmarkDotNet.Attributes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Benchmarks;

/// <summary>
/// Closes risk #4 of the implementation plan: how does <see cref="JsonDiffEngine.Diff"/>
/// behave on large documents? The diff runs once per write inside the session — its
/// cost adds directly to every save/patch latency.
/// </summary>
/// <remarks>
/// Document shapes: ~60% scalar leaves, ~20% nested objects (recursed), ~20% arrays
/// (atomic per ADR-004 — compared via DeepEquals as a whole). <c>FieldCount</c> counts
/// leaf fields. Scenarios cover the write spectrum: an unchanged save (pure compare
/// cost), the typical single-field change, a 10% bulk-ish change, and an insert
/// (everything is new).
/// </remarks>
[MemoryDiagnoser]
[ShortRunJob]
public class DiffEngineBenchmarks
{
    private JsonObject _before = null!;
    private JsonObject _identical = null!;
    private JsonObject _oneFieldChanged = null!;
    private JsonObject _tenPercentChanged = null!;

    [Params(10, 100, 1_000, 10_000)]
    public int FieldCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _before = DocumentFactory.Create(FieldCount, seed: 42);
        // Separate instances: DeepEquals must do real structural work, not reference checks.
        _identical = DocumentFactory.Create(FieldCount, seed: 42);
        _oneFieldChanged = DocumentFactory.Mutate(DocumentFactory.Create(FieldCount, seed: 42), changedLeaves: 1);
        _tenPercentChanged = DocumentFactory.Mutate(
            DocumentFactory.Create(FieldCount, seed: 42), changedLeaves: Math.Max(1, FieldCount / 10));
    }

    [Benchmark(Description = "no change (save of identical state)")]
    public DocumentDiff NoChange() => JsonDiffEngine.Diff(_before, _identical);

    [Benchmark(Description = "1 leaf changed (typical update)")]
    public DocumentDiff OneFieldChanged() => JsonDiffEngine.Diff(_before, _oneFieldChanged);

    [Benchmark(Description = "10% of leaves changed")]
    public DocumentDiff TenPercentChanged() => JsonDiffEngine.Diff(_before, _tenPercentChanged);

    [Benchmark(Description = "insert (before = null)")]
    public DocumentDiff Insert() => JsonDiffEngine.Diff(null, _before);
}

/// <summary>Deterministic synthetic documents with a realistic shape mix.</summary>
internal static class DocumentFactory
{
    /// <summary>
    /// Builds a document with <paramref name="leafCount"/> scalar leaves distributed
    /// over flat fields, nested objects (3 levels) and atomic arrays of 10 elements.
    /// </summary>
    public static JsonObject Create(int leafCount, int seed)
    {
        var random = new Random(seed);
        var root = new JsonObject();
        var flat = (int)(leafCount * 0.6);
        var nested = (int)(leafCount * 0.2);
        var inArrays = leafCount - flat - nested;

        for (var i = 0; i < flat; i++)
        {
            root[$"field{i}"] = Scalar(random, i);
        }

        // Nested objects, 3 levels deep, ~5 leaves each.
        var nestedObjects = Math.Max(1, nested / 5);
        for (var i = 0; i < nestedObjects; i++)
        {
            var level1 = new JsonObject();
            var level2 = new JsonObject();
            var level3 = new JsonObject();
            for (var j = 0; j < 2; j++)
            {
                level1[$"a{j}"] = Scalar(random, j);
                level2[$"b{j}"] = Scalar(random, j);
            }

            level3["c0"] = Scalar(random, i);
            level2["deep"] = level3;
            level1["inner"] = level2;
            root[$"nested{i}"] = level1;
        }

        // Atomic arrays (ADR-004): each counts as one comparison unit of 10 elements.
        var arrays = Math.Max(1, inArrays / 10);
        for (var i = 0; i < arrays; i++)
        {
            var array = new JsonArray();
            for (var j = 0; j < 10; j++)
            {
                array.Add(Scalar(random, j));
            }

            root[$"items{i}"] = array;
        }

        return root;
    }

    /// <summary>Changes the first <paramref name="changedLeaves"/> flat leaves in place.</summary>
    public static JsonObject Mutate(JsonObject document, int changedLeaves)
    {
        var changed = 0;
        foreach (var key in document.Select(p => p.Key).Where(k => k.StartsWith("field", StringComparison.Ordinal)).ToList())
        {
            if (changed++ >= changedLeaves)
            {
                break;
            }

            document[key] = $"changed-{key}";
        }

        return document;
    }

    private static JsonNode Scalar(Random random, int salt) => (salt % 3) switch
    {
        0 => JsonValue.Create($"value-{random.Next(1_000_000)}"),
        1 => JsonValue.Create(random.Next(1_000_000)),
        _ => JsonValue.Create(random.NextDouble() > 0.5),
    };
}
