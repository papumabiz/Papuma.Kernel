// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Robustness;

/// <summary>
/// Property tests of the diff, policy and canonical engines over thousands of generated documents
/// (seeded, so a failure is reproduced by its seed). Each test states something that must hold for
/// <i>every</i> document — the opposite of the hand-picked vectors, which pin what we thought of.
/// </summary>
public sealed class EngineRobustnessTests
{
    private const int Cases = 3000;

    // The path grammar of docs/spec/schemas/diff.schema.json.
    private static readonly Regex PathGrammar = new(
        @"^(?:[^.\\]|\\[.\\])+(?:\.(?:[^.\\]|\\[.\\])+)*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static void ForEachCase(int count, int firstSeed, Action<JsonGenerator> body)
    {
        for (var i = 0; i < count; i++)
        {
            var seed = firstSeed + i;
            var generator = new JsonGenerator(seed);
            try
            {
                body(generator);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"Failed for seed {seed}: {ex.Message}", ex);
            }
        }
    }

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";

    // ── The diff ───────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_AppliedForwardAndBackward_RestoresTheDocuments()
    {
        ForEachCase(Cases, firstSeed: 1000, g =>
        {
            var before = g.Object(3);
            var after = g.Chance(0.85) ? g.Mutate(before) : g.Object(3);

            var diff = JsonDiffEngine.Diff(before, after);

            Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, diff)),
                $"Apply(before, diff) != after.\nbefore={Show(before)}\nafter={Show(after)}\ndiff={Show(diff.ToJson())}");
            Assert.True(JsonNode.DeepEquals(before, JsonDiffEngine.ApplyReverse(after, diff)),
                $"ApplyReverse(after, diff) != before.\nbefore={Show(before)}\nafter={Show(after)}\ndiff={Show(diff.ToJson())}");
        });
    }

    [Fact]
    public void Diff_SurvivesTheWireFormat_AndTheRestoredDiffBehavesTheSame()
    {
        ForEachCase(Cases, firstSeed: 20_000, g =>
        {
            var before = g.Object(3);
            var after = g.Mutate(before);
            var diff = JsonDiffEngine.Diff(before, after);

            var wire = diff.ToJson();
            var restored = DocumentDiff.FromJson(wire);

            Assert.True(JsonNode.DeepEquals(wire, restored.ToJson()), $"wire round trip changed: {Show(wire)}");
            Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, restored)));
        });
    }

    [Fact]
    public void Diff_ProducesWellFormedOrderedPaths_ThatSplitAndJoinBack()
    {
        ForEachCase(Cases, firstSeed: 40_000, g =>
        {
            var diff = JsonDiffEngine.Diff(g.Object(3), g.Object(3));

            string? previous = null;
            foreach (var path in diff.Paths)
            {
                Assert.Matches(PathGrammar, path);
                Assert.Equal(path, DiffPath.Join(DiffPath.Split(path)));
                if (previous is not null)
                {
                    Assert.True(string.CompareOrdinal(previous, path) < 0, $"'{previous}' !< '{path}'");
                }

                previous = path;
            }
        });
    }

    [Fact]
    public void Diff_OfADocumentWithItself_IsEmpty_AndSwappingTheSidesSwapsOldAndNew()
    {
        ForEachCase(Cases, firstSeed: 60_000, g =>
        {
            var a = g.Object(3);
            var b = g.Mutate(a);

            Assert.True(JsonDiffEngine.Diff(a, a).IsEmpty);

            var forward = JsonDiffEngine.Diff(a, b);
            var backward = JsonDiffEngine.Diff(b, a);
            Assert.Equal(forward.Paths, backward.Paths);
            foreach (var (path, entry) in forward.Entries)
            {
                var other = backward.Entries[path];
                Assert.Equal(entry.HasOld, other.HasNew);
                Assert.Equal(entry.HasNew, other.HasOld);
                Assert.True(JsonNode.DeepEquals(entry.Old, other.New));
                Assert.True(JsonNode.DeepEquals(entry.New, other.Old));
            }
        });
    }

    [Fact]
    public void Diff_IgnoresKeyOrderAndNumberSpelling()
    {
        ForEachCase(Cases, firstSeed: 80_000, g =>
        {
            var document = g.Object(3);
            var respelled = (JsonObject)g.Equivalent(document)!;

            var diff = JsonDiffEngine.Diff(document, respelled);

            Assert.True(diff.IsEmpty, $"equivalent documents differ.\na={Show(document)}\nb={Show(respelled)}\ndiff={Show(diff.ToJson())}");
        });
    }

    // ── Canonical JSON ─────────────────────────────────────────────────────────

    [Fact]
    public void Canonical_IsStable_UnderKeyOrderNumberSpellingAndReparsing()
    {
        ForEachCase(Cases, firstSeed: 100_000, g =>
        {
            var document = g.Object(3);

            var canonical = CanonicalJson.Serialize(document);

            Assert.Equal(canonical, CanonicalJson.Serialize(g.Equivalent(document)));
            Assert.Equal(canonical, CanonicalJson.Serialize(JsonNode.Parse(canonical)));
            Assert.Equal(canonical, CanonicalJson.Serialize(JsonNode.Parse(document.ToJsonString())));
        });
    }

    [Fact]
    public void NumberEquality_IsTheSameForTheDiffAndForTheCanonicalForm()
    {
        ForEachCase(Cases * 2, firstSeed: 120_000, g =>
        {
            var (negative, digits, exponent) = g.RandomNumber();
            var first = g.Spell(negative, digits, exponent);
            var same = g.Spell(negative, digits, exponent);
            var different = g.Chance(0.5)
                ? g.Spell(negative, digits, exponent + 1)
                : g.Spell(negative, digits + "1", exponent);

            Assert.Equal(CanonicalJson.NormalizeNumber(first), CanonicalJson.NormalizeNumber(same));
            Assert.NotEqual(CanonicalJson.NormalizeNumber(first), CanonicalJson.NormalizeNumber(different));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(first), JsonNode.Parse(same)), $"'{first}' != '{same}' to the diff");
            Assert.False(JsonNode.DeepEquals(JsonNode.Parse(first), JsonNode.Parse(different)), $"'{first}' == '{different}' to the diff");
        });
    }

    // ── Policies ───────────────────────────────────────────────────────────────

    [Fact]
    public void Policies_NeverLetAProtectedValueThrough_AndKeepTheRest()
    {
        var provider = new MasterKeyHashKeyProvider(
            Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), "k1");
        var scope = ScopeContext.Tenant("acme");

        ForEachCase(Cases, firstSeed: 140_000, g =>
        {
            // A random diff whose every value is a recognisable secret.
            var paths = Enumerable.Range(0, g.Next(1, 7)).Select(_ => RandomPath(g)).Distinct().ToList();
            var wire = new JsonObject();
            var n = 0;
            foreach (var path in paths)
            {
                wire[path] = new JsonObject { ["old"] = $"SECRET_{n++}", ["new"] = $"SECRET_{n++}" };
            }

            // Random policies on random paths — some of them ancestors of diff paths.
            var policies = new Dictionary<string, FieldPolicy>(StringComparer.Ordinal);
            foreach (var path in paths.Concat(paths.Select(Ancestor).OfType<string>()))
            {
                if (g.Chance(0.4))
                {
                    policies[path] = g.Pick(new[] { FieldPolicy.Redact, FieldPolicy.Reference, FieldPolicy.Hash, FieldPolicy.DoNotTrack, FieldPolicy.Track });
                }
            }

            var metadata = new DocumentTypeMetadata(
                "Doc", typeof(object), policies, keys: [], idGetter: _ => "1", upcasters: [], validator: null, exposedToMcp: false);

            var applied = PolicyApplier.Apply(DocumentDiff.FromJson(wire), metadata, "1", provider, scope);

            foreach (var path in paths)
            {
                var policy = IndependentlyResolved(policies, path);
                switch (policy)
                {
                    case FieldPolicy.Track:
                        Assert.True(JsonNode.DeepEquals(wire[path], applied.ToJson()[path]), $"tracked '{path}' changed");
                        break;
                    case FieldPolicy.DoNotTrack:
                        Assert.DoesNotContain(path, applied.Paths);
                        break;
                    case FieldPolicy.Redact:
                        Assert.Equal(DiffEntryKind.Redacted, applied.Entries[path].Kind);
                        break;
                    case FieldPolicy.Reference:
                        Assert.Equal($"Doc/1/{path}", applied.Entries[path].Reference);
                        break;
                    case FieldPolicy.Hash:
                        Assert.Matches(@"^v2:k1:[0-9a-f]{64}$", applied.Entries[path].Hash);
                        break;
                }
            }

            // Nothing protected may appear anywhere in what is stored.
            foreach (var (path, entry) in applied.Entries)
            {
                if (entry.Kind != DiffEntryKind.Tracked)
                {
                    Assert.DoesNotContain("SECRET_", Show(applied.ToJson()[path]));
                }
            }

            Assert.All(applied.Paths, p => Assert.Contains(p, paths));
        });
    }

    // The nearest declared ancestor wins — computed here from segments, not with the engine's own helper.
    private static FieldPolicy IndependentlyResolved(Dictionary<string, FieldPolicy> policies, string path)
    {
        var target = DiffPath.Split(path);
        var best = -1;
        var result = FieldPolicy.Track;
        foreach (var (declared, policy) in policies)
        {
            var segments = DiffPath.Split(declared);
            if (segments.Length <= target.Length && segments.Length > best && segments.SequenceEqual(target.Take(segments.Length)))
            {
                best = segments.Length;
                result = policy;
            }
        }

        return result;
    }

    private static string RandomPath(JsonGenerator g) =>
        DiffPath.Join(Enumerable.Range(0, g.Next(1, 4)).Select(_ => g.Key()));

    private static string? Ancestor(string path) => DiffPath.TryGetParent(path, out var parent) ? parent : null;

    // ── Structural limits ──────────────────────────────────────────────────────

    private static JsonObject Nested(int levels)
    {
        var root = new JsonObject();
        var current = root;
        for (var i = 1; i < levels; i++)
        {
            var child = new JsonObject();
            current["n"] = child;
            current = child;
        }

        return root;
    }

    [Fact]
    public void AnAbsurdlyDeepDocument_IsRefusedCleanly_NotWithAStackOverflow()
    {
        var tooDeep = Nested(JsonLimits.MaxDepth + 1);

        Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(tooDeep, new JsonObject()));
        Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(new JsonObject(), tooDeep));
        Assert.Equal("too-deep", Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Serialize(tooDeep)).Code);

        // Far past the limit is refused without recursing into it. (Much deeper than this cannot be tested:
        // building a JsonNode chain of ~100,000 levels overflows the stack inside System.Text.Json itself,
        // before any engine code runs — which is also why the serializer's own limit of 64 matters.)
        Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(Nested(10_000), new JsonObject()));
    }

    [Fact]
    public void ADocumentAtTheLimit_StillWorks()
    {
        var atLimit = Nested(JsonLimits.MaxDepth);

        var diff = JsonDiffEngine.Diff(new JsonObject(), atLimit);

        Assert.False(diff.IsEmpty);
        Assert.NotEmpty(CanonicalJson.Serialize(atLimit));
    }

    [Fact]
    public void DeepNestingInsideAnArray_CountsToo()
    {
        JsonNode inner = new JsonArray();
        for (var i = 0; i < JsonLimits.MaxDepth + 5; i++)
        {
            inner = new JsonArray(inner);
        }

        var document = new JsonObject { ["a"] = inner };

        Assert.Throws<NotSupportedException>(() => JsonDiffEngine.Diff(new JsonObject(), document));
    }

    [Fact]
    public void AVeryWideDocument_IsDiffedCompletely()
    {
        var before = new JsonObject();
        var after = new JsonObject();
        for (var i = 0; i < 20_000; i++)
        {
            before[$"k{i}"] = i;
            after[$"k{i}"] = i % 1000 == 0 ? i + 1 : i;
        }

        var diff = JsonDiffEngine.Diff(before, after);

        Assert.Equal(20, diff.Paths.Count);
        Assert.True(JsonNode.DeepEquals(after, JsonDiffEngine.Apply(before, diff)));
    }
}
