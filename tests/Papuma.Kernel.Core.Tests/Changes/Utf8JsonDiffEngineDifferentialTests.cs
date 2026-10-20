// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Buffers;
using System.Text;
using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Core.Tests.Robustness;

namespace Papuma.Kernel.Core.Tests.Changes;

/// <summary>
/// The UTF-8 prototype held against <see cref="JsonDiffEngine"/> as the oracle over generated documents (seeded:
/// a failure names its seed). Every case varies the change, the text spelling of each side and the kind of input
/// buffer, and compares the full semantics — paths, presence of each side, values — of the materialized diff and of
/// the diff read back from the prototype's wire form, plus both reconstructions.
/// </summary>
public sealed class Utf8JsonDiffEngineDifferentialTests
{
    private enum Change
    {
        Few,
        Many,
        Unrelated,
        Identical,
        Insert,
        Delete,
        Deep,
    }

    private enum Buffer
    {
        ByteArray,
        SliceOfALargerArray,
        Utf16String,
        PooledAndOverwritten,
    }

    private sealed record Outcome(string? Failure, Change Change, JsonSpelling BeforeSpelling, JsonSpelling AfterSpelling, Buffer Buffer);

    [Fact]
    public void BothEngines_AgreeOnEveryEntry_AndThePrototypeReconstructsBothStates()
    {
        var cases = DiffComparison.Cases(ciDefault: 4000);
        var failures = new List<string>();
        var seen = new HashSet<(Change, Buffer)>();

        for (var seed = 1; seed <= cases; seed++)
        {
            var outcome = RunCase(seed);
            seen.Add((outcome.Change, outcome.Buffer));
            if (outcome.Failure is not null)
            {
                failures.Add($"seed {seed} ({outcome.Change}, {outcome.BeforeSpelling} -> {outcome.AfterSpelling}, {outcome.Buffer}): {outcome.Failure}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases failed:\n{string.Join("\n", failures.Take(5))}");
        Assert.Equal(Enum.GetValues<Change>().Length * Enum.GetValues<Buffer>().Length, seen.Count); // every combination ran
    }

    private static Outcome RunCase(int seed)
    {
        var g = new JsonGenerator(seed);
        var change = (Change)(seed % Enum.GetValues<Change>().Length);
        var buffer = (Buffer)(seed / Enum.GetValues<Change>().Length % Enum.GetValues<Buffer>().Length);
        var beforeSpelling = g.Pick(Enum.GetValues<JsonSpelling>());
        var afterSpelling = g.Pick(Enum.GetValues<JsonSpelling>());

        var (before, after) = Documents(g, change);
        var beforeText = before is null ? null : DiffComparison.Spell(before, beforeSpelling, g);
        var afterText = after is null ? null : DiffComparison.Spell(after, afterSpelling, g);

        try
        {
            var beforeState = Parse(beforeText);
            var afterState = Parse(afterText);
            var expected = JsonDiffEngine.Diff(beforeState, afterState);
            var actual = Run(beforeText, afterText, buffer);

            var failure = DiffComparison.Difference(expected, actual.ToDocumentDiff())
                ?? Prefixed("wire form: ", DiffComparison.Difference(expected, DiffComparison.FromWire(actual.ToUtf8Json())))
                ?? DiffComparison.Reconstruction(beforeState, afterState, actual.ToDocumentDiff())
                ?? (change == Change.Identical && !actual.IsEmpty ? "identical documents produced entries" : null);
            return new Outcome(failure is null ? null : $"{failure}\n  before={beforeText}\n  after={afterText}", change, beforeSpelling, afterSpelling, buffer);
        }
        catch (Exception ex)
        {
            return new Outcome($"{ex.GetType().Name}: {ex.Message}\n  before={beforeText}\n  after={afterText}", change, beforeSpelling, afterSpelling, buffer);
        }
    }

    private static (JsonObject? Before, JsonObject? After) Documents(JsonGenerator g, Change change)
    {
        switch (change)
        {
            case Change.Few:
            {
                var before = g.Object(3);
                return (before, g.Mutate(before));
            }

            case Change.Many:
            {
                var before = g.Object(3);
                var after = before;
                for (var i = 0; i < 8; i++)
                {
                    after = g.Mutate(after);
                }

                return (before, after);
            }

            case Change.Unrelated:
                return (g.Object(3), g.Object(3));
            case Change.Identical:
            {
                var document = g.Object(3);
                return (document, (JsonObject)document.DeepClone());
            }

            case Change.Insert:
                return (null, g.Object(3));
            case Change.Delete:
                return (g.Object(3), null);
            default:
            {
                var before = g.Object(6);
                return (before, g.Mutate(before));
            }
        }
    }

    // Feeds the texts to the prototype through the requested kind of buffer. A pooled buffer is overwritten and
    // handed back before the result is used, so a result that still pointed into it would be caught.
    private static Utf8Diff Run(string? beforeText, string? afterText, Buffer buffer)
    {
        switch (buffer)
        {
            case Buffer.Utf16String:
                return Utf8JsonDiffEngine.Diff(beforeText, afterText);
            case Buffer.SliceOfALargerArray:
                return DiffComparison.DiffUtf8(Padded(beforeText), Padded(afterText));
            case Buffer.PooledAndOverwritten:
            {
                var (beforeArray, beforeMemory) = Pooled(beforeText);
                var (afterArray, afterMemory) = Pooled(afterText);
                var result = DiffComparison.DiffUtf8(beforeMemory, afterMemory);
                foreach (var array in new[] { beforeArray, afterArray })
                {
                    if (array is not null)
                    {
                        array.AsSpan().Fill((byte)'#');
                        ArrayPool<byte>.Shared.Return(array);
                    }
                }

                return result;
            }

            default:
                return DiffComparison.DiffUtf8(DiffComparison.Utf8(beforeText), DiffComparison.Utf8(afterText));
        }
    }

    private static ReadOnlyMemory<byte>? Padded(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        var padded = new byte[utf8.Length + 64];
        padded.AsSpan().Fill((byte)'!');
        utf8.CopyTo(padded, 32);
        return padded.AsMemory(32, utf8.Length);
    }

    private static (byte[]? Array, ReadOnlyMemory<byte>? Memory) Pooled(string? text)
    {
        if (text is null)
        {
            return (null, null);
        }

        var length = Encoding.UTF8.GetByteCount(text);
        var array = ArrayPool<byte>.Shared.Rent(length);
        Encoding.UTF8.GetBytes(text, array);
        return (array, array.AsMemory(0, length));
    }

    private static JsonObject? Parse(string? text) => text is null ? null : (JsonObject)JsonNode.Parse(text)!;

    private static string? Prefixed(string prefix, string? failure) => failure is null ? null : prefix + failure;
}
