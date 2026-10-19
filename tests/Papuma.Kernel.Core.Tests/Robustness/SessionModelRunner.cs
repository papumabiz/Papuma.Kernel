// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Robustness;

/// <summary>
/// A document with free-form content next to typed fields and every kind of policy. The three policy
/// fields are omitted when null, so a document that never sets them has a history of values only. The free-form
/// <see cref="Data"/> carries the hostile keys and values of <see cref="JsonGenerator"/> through a real
/// session.
/// </summary>
internal sealed record FreeDoc(
    string Id,
    JsonObject Data,
    string? Title = null,
    long Counter = 0,
    [property: SensitiveData, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Secret = null,
    [property: TrackHash, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pin = null,
    [property: DoNotTrack, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scratch = null);

/// <summary>One patch the model understands (a typed patch on <see cref="FreeDoc"/>).</summary>
internal readonly record struct PatchSpec(PatchKind Kind, string? Text, long By);

internal enum PatchKind
{
    SetTitle,
    RemoveTitle,
    IncrementCounter,
}

/// <summary>One session of an engine under test, with the operations the model drives.</summary>
internal interface ISessionAdapter : IAsyncDisposable
{
    Task<SaveResult> SaveAsync(FreeDoc document, long expectedVersion);

    Task<DocumentResult<FreeDoc>?> LoadAsync(string id);

    Task<SaveResult> DeleteAsync(string id, long expectedVersion);

    Task<SaveResult> PatchAsync(string id, PatchSpec patch, long? expectedVersion);

    Task<SaveResult> RollbackAsync(string id, long toVersion, long expectedVersion);

    Task<IReadOnlyList<ChangeRecord>> HistoryAsync(string id);

    Task CommitAsync();
}

/// <summary>An engine under test: opens sessions in a scope.</summary>
internal interface IStoreAdapter
{
    ISessionAdapter Open(ScopeContext scope);
}

/// <summary>
/// Drives random sequences of operations — save, patch, delete, roll back, load, grouped into sessions that
/// commit or are abandoned, with right and wrong expected versions — against a real engine and an in-memory
/// reference model at once, and fails with the seed and the operation log on the first disagreement. After
/// every sequence it checks the stored state, the version numbers and the whole change history of every
/// document against the model, and replays the history to reproduce the states.
/// </summary>
internal sealed class SessionModelRunner(KernelModel model, IHashKeyProvider hashKeys)
{
    private static readonly string[] Ids = ["d1", "d2", "d3"];

    private sealed record HistoryEntry(long Version, ChangeOperation Operation, JsonObject? Before, JsonObject? After, DocumentDiff Diff, bool IsRollback);

    private sealed class Tracked
    {
        public JsonObject? Current { get; set; }

        public long LastVersion { get; set; }

        public List<HistoryEntry> History { get; } = [];

        public Tracked Copy()
        {
            var copy = new Tracked { Current = (JsonObject?)Current?.DeepClone(), LastVersion = LastVersion };
            copy.History.AddRange(History);
            return copy;
        }
    }

    private DocumentTypeMetadata Metadata => model.GetRequired<FreeDoc>();

    public async Task RunAsync(int seed, IStoreAdapter store, int groups = 12)
    {
        var g = new JsonGenerator(seed);
        var scope = ScopeContext.Tenant($"m{seed}x{Guid.NewGuid():N}");
        _scope = scope;
        var committed = Ids.ToDictionary(id => id, _ => new Tracked());
        var log = new StringBuilder();
        var plain = g.Chance(0.3); // plain documents have no policy-protected fields: full replay checks apply

        try
        {
            for (var group = 0; group < groups; group++)
            {
                var scratch = committed.ToDictionary(p => p.Key, p => p.Value.Copy());
                var commit = g.Chance(0.75);
                log.AppendLine($"-- session {group} ({(commit ? "commit" : "abandon")})");

                await using (var session = store.Open(scope))
                {
                    for (var step = g.Next(1, 5); step > 0; step--)
                    {
                        await StepAsync(session, scratch, g, plain, log);
                    }

                    if (commit)
                    {
                        await session.CommitAsync();
                    }
                }

                if (commit)
                {
                    committed = scratch;
                }

                await VerifyAsync(store, scope, committed, log, plain);
            }
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            throw new Xunit.Sdk.XunitException($"Seed {seed}: {ex.GetType().Name}: {ex.Message}\n{log}", ex);
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            throw new Xunit.Sdk.XunitException($"Seed {seed}: {ex.Message}\n{log}", ex);
        }
    }

    // ── One operation ──────────────────────────────────────────────────────────

    private async Task StepAsync(
        ISessionAdapter session, Dictionary<string, Tracked> state, JsonGenerator g, bool plain, StringBuilder log)
    {
        var id = g.Pick(Ids);
        var tracked = state[id];
        switch (g.Next(100))
        {
            case < 40:
                await SaveStepAsync(session, tracked, id, g, plain, log);
                break;
            case < 60:
                await PatchStepAsync(session, tracked, id, g, log);
                break;
            case < 72:
                await DeleteStepAsync(session, tracked, id, g, log);
                break;
            case < 82:
                await RollbackStepAsync(session, tracked, id, g, log);
                break;
            default:
                await LoadStepAsync(session, tracked, id, log);
                break;
        }
    }

    private async Task SaveStepAsync(ISessionAdapter session, Tracked tracked, string id, JsonGenerator g, bool plain, StringBuilder log)
    {
        var document = NewDocument(g, id, plain);
        var exists = tracked.Current is not null;
        var expected = g.Chance(0.7)
            ? (exists ? tracked.LastVersion : 0)
            : g.Pick(exists ? new long[] { 0, tracked.LastVersion + 1, Math.Max(1, tracked.LastVersion - 1) } : [1, 3]);
        log.AppendLine($"save {id} expected={expected} (model: {(exists ? "v" + tracked.LastVersion : "absent")})");
        var after = Serialize(document);

        if (!exists && expected > 0)
        {
            await Assert.ThrowsAsync<DocumentNotFoundException>(() => session.SaveAsync(document, expected));
            return;
        }

        if (exists && expected != tracked.LastVersion)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => session.SaveAsync(document, expected));
            return;
        }

        var result = await session.SaveAsync(document, expected);
        Record(tracked, result, exists ? ChangeOperation.Update : ChangeOperation.Insert, tracked.Current, after, isRollback: false);
        tracked.Current = after;
    }

    private async Task PatchStepAsync(ISessionAdapter session, Tracked tracked, string id, JsonGenerator g, StringBuilder log)
    {
        var spec = g.Next(3) switch
        {
            0 => new PatchSpec(PatchKind.SetTitle, g.Chance(0.2) ? null : g.Pick(new[] { "", "x", "é", "a.b", "😀" }), 0),
            1 => new PatchSpec(PatchKind.RemoveTitle, null, 0),
            _ => new PatchSpec(PatchKind.IncrementCounter, null, g.Next(-5, 6)),
        };
        var exists = tracked.Current is not null;
        long? expected = g.Next(10) switch
        {
            < 5 => null,
            < 8 => exists ? tracked.LastVersion : 1,
            _ => tracked.LastVersion + 1,
        };

        log.AppendLine($"patch {id} {spec.Kind} text={spec.Text ?? "<null>"} by={spec.By} expected={(expected?.ToString() ?? "-")} (model: {(exists ? "v" + tracked.LastVersion : "absent")})");

        if (!exists)
        {
            await Assert.ThrowsAsync<DocumentNotFoundException>(() => session.PatchAsync(id, spec, expected));
            return;
        }

        if (expected is not null && expected != tracked.LastVersion)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => session.PatchAsync(id, spec, expected));
            return;
        }

        var after = (JsonObject)tracked.Current!.DeepClone();
        JsonPatchApplier.Apply(after, [ToOperation(spec)], Metadata.Name, id);

        var result = await session.PatchAsync(id, spec, expected);
        Record(tracked, result, ChangeOperation.Update, tracked.Current, after, isRollback: false);
        tracked.Current = after;
    }

    private async Task DeleteStepAsync(ISessionAdapter session, Tracked tracked, string id, JsonGenerator g, StringBuilder log)
    {
        var exists = tracked.Current is not null;
        var expected = g.Chance(0.7)
            ? (exists ? tracked.LastVersion : 1)
            : g.Pick(new long[] { 1, tracked.LastVersion + 1, Math.Max(1, tracked.LastVersion - 1) });

        log.AppendLine($"delete {id} expected={expected} (model: {(exists ? "v" + tracked.LastVersion : "absent")})");

        if (!exists)
        {
            await Assert.ThrowsAsync<DocumentNotFoundException>(() => session.DeleteAsync(id, expected));
            return;
        }

        if (expected != tracked.LastVersion)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => session.DeleteAsync(id, expected));
            return;
        }

        var result = await session.DeleteAsync(id, expected);
        Record(tracked, result, ChangeOperation.Delete, tracked.Current, null, isRollback: false);
        tracked.Current = null;
    }

    private async Task RollbackStepAsync(ISessionAdapter session, Tracked tracked, string id, JsonGenerator g, StringBuilder log)
    {
        if (tracked.Current is null || tracked.LastVersion < 2)
        {
            await LoadStepAsync(session, tracked, id, log);
            return;
        }

        // Candidate targets: versions whose state exists and which are reached from the current one through
        // updates only (a delete or re-insert on the way is outside what this test models).
        var candidates = new List<long>();
        for (var v = tracked.LastVersion - 1; v >= 1; v--)
        {
            var entry = tracked.History.FirstOrDefault(h => h.Version == v);
            if (entry?.After is null)
            {
                break;
            }

            candidates.Add(v);
            if (entry.Operation == ChangeOperation.Insert)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            await LoadStepAsync(session, tracked, id, log);
            return;
        }

        var toVersion = g.Pick(candidates);
        var wrongExpected = g.Chance(0.2);
        var expected = wrongExpected ? tracked.LastVersion + 1 : tracked.LastVersion;
        log.AppendLine($"rollback {id} to={toVersion} expected={expected} (model: v{tracked.LastVersion})");

        if (wrongExpected)
        {
            await Assert.ThrowsAsync<ConcurrencyException>(() => session.RollbackAsync(id, toVersion, expected));
            return;
        }

        var onTheWay = tracked.History.Where(h => h.Version > toVersion && h.Version <= tracked.LastVersion).ToList();
        if (onTheWay.Any(h => h.Diff.Entries.Values.Any(e => e.Kind != DiffEntryKind.Tracked)))
        {
            await Assert.ThrowsAsync<RollbackNotPossibleException>(() => session.RollbackAsync(id, toVersion, expected));
            return;
        }

        // The restored state is the old state — except for a field that is never in the feed
        // (DoNotTrack), which a diff cannot restore and which therefore keeps its current value.
        var restored = (JsonObject)tracked.History.First(h => h.Version == toVersion).After!.DeepClone();
        restored.Remove("scratch");
        if (tracked.Current.TryGetPropertyValue("scratch", out var scratch))
        {
            restored["scratch"] = scratch?.DeepClone();
        }

        var result = await session.RollbackAsync(id, toVersion, expected);
        Record(tracked, result, ChangeOperation.Update, tracked.Current, restored, isRollback: true);
        tracked.Current = restored;
    }

    private async Task LoadStepAsync(ISessionAdapter session, Tracked tracked, string id, StringBuilder log)
    {
        log.AppendLine($"load {id} (model: {(tracked.Current is null ? "absent" : "v" + tracked.LastVersion)})");
        var loaded = await session.LoadAsync(id);
        AssertSame(tracked, loaded);
    }

    // ── Checks ─────────────────────────────────────────────────────────────────

    private void Record(Tracked tracked, SaveResult result, ChangeOperation operation, JsonObject? before, JsonObject? after, bool isRollback)
    {
        var expectedDiff = Expected(before, after);
        Assert.Equal(tracked.LastVersion + 1, result.Version);
        Assert.Equal(operation, result.Operation);
        Assert.True(
            JsonNode.DeepEquals(expectedDiff.ToJson(), result.Diff.ToJson()),
            $"result diff differs.\n{Difference(expectedDiff, result.Diff)}");

        tracked.LastVersion = result.Version;
        tracked.History.Add(new HistoryEntry(result.Version, operation, before is null ? null : (JsonObject)before.DeepClone(),
            after is null ? null : (JsonObject)after.DeepClone(), expectedDiff, isRollback));
    }

    // The diff the kernel must record: the raw diff of the stored states, with the field policies applied.
    private DocumentDiff Expected(JsonObject? before, JsonObject? after) =>
        PolicyApplier.Apply(JsonDiffEngine.Diff(before, after), Metadata, IdOf(before ?? after), hashKeys, _scope);

    private ScopeContext _scope = ScopeContext.Platform();

    private static string IdOf(JsonObject? document) => (string)document!["id"]!;

    private void AssertSame(Tracked tracked, DocumentResult<FreeDoc>? loaded)
    {
        if (tracked.Current is null)
        {
            Assert.Null(loaded);
            return;
        }

        Assert.NotNull(loaded);
        Assert.Equal(tracked.LastVersion, loaded.Version);

        // Compare through the CLR type so that "absent" and "null" for an optional property read the same.
        var expected = Serialize(tracked.Current.Deserialize<FreeDoc>(KernelJson.Options)!);
        var actual = Serialize(loaded.Document);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"stored state differs.\nexpected={expected.ToJsonString()}\nactual  ={actual.ToJsonString()}");
    }

    private async Task VerifyAsync(IStoreAdapter store, ScopeContext scope, Dictionary<string, Tracked> committed, StringBuilder log, bool plain)
    {
        await using var session = store.Open(scope);
        foreach (var (id, tracked) in committed)
        {
            AssertSame(tracked, await session.LoadAsync(id));

            var history = await session.HistoryAsync(id);
            Assert.Equal(tracked.History.Count, history.Count);
            for (var i = 0; i < history.Count; i++)
            {
                var expected = tracked.History[i];
                var actual = history[i];
                Assert.Equal(expected.Version, actual.Version);
                Assert.Equal(expected.Operation, actual.Operation);
                Assert.True(
                    JsonNode.DeepEquals(expected.Diff.ToJson(), actual.Diff.ToJson()),
                    $"history diff of {id} v{expected.Version} differs.\n{Difference(expected.Diff, actual.Diff)}");
                Assert.Equal(expected.IsRollback, actual.Metadata["isRollback"]?.GetValue<bool>() ?? false);
            }

            // Replay: a history of values only reproduces every state it passed through.
            if (tracked.History.All(h => h.Diff.Entries.Values.All(e => e.Kind == DiffEntryKind.Tracked)))
            {
                JsonObject? replayed = null;
                foreach (var entry in history)
                {
                    replayed = entry.Operation == ChangeOperation.Delete
                        ? null
                        : JsonDiffEngine.Apply(replayed, entry.Diff);
                    // A DoNotTrack field is never in the feed, so no replay can bring it back.
                    var expected = (JsonObject?)tracked.History.First(h => h.Version == entry.Version).After?.DeepClone();
                    expected?.Remove("scratch");
                    Assert.True(
                        JsonNode.DeepEquals(expected, replayed),
                        $"replaying {id} up to v{entry.Version} gives {replayed?.ToJsonString()}, expected {expected?.ToJsonString()}");
                }
            }
        }

        log.AppendLine("   verified");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Names only the paths whose entries differ, so a failure points at the cause instead of two long diffs.
    private static string Difference(DocumentDiff expected, DocumentDiff actual)
    {
        var expectedJson = expected.ToJson();
        var actualJson = actual.ToJson();
        var lines = new List<string>();
        foreach (var path in expectedJson.Select(p => p.Key).Union(actualJson.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            expectedJson.TryGetPropertyValue(path, out var e);
            actualJson.TryGetPropertyValue(path, out var a);
            if (!JsonNode.DeepEquals(e, a))
            {
                lines.Add($"  path '{path}'\n    expected {e?.ToJsonString() ?? "(absent)"}\n    actual   {a?.ToJsonString() ?? "(absent)"}");
            }
        }

        return string.Join("\n", lines);
    }

    private static JsonObject Serialize(FreeDoc document) =>
        (JsonObject)JsonSerializer.SerializeToNode(document, KernelJson.Options)!;

    private static FreeDoc NewDocument(JsonGenerator g, string id, bool plain)
    {
        string? Maybe() => g.Chance(0.35) ? null : g.Pick(new[] { "", "x", "é", "secret-1", "secret-2", "a.b", "😀" });
        return new FreeDoc(
            id,
            g.Object(2),
            Title: Maybe(),
            Counter: g.Next(-100, 100),
            Secret: plain ? null : Maybe(),
            Pin: plain ? null : Maybe(),
            Scratch: plain ? null : Maybe());
    }

    private static PatchOperation ToOperation(PatchSpec spec) => spec.Kind switch
    {
        PatchKind.SetTitle => new PatchOperation(PatchOperationKind.Set, ["title"], spec.Text is null ? null : JsonValue.Create(spec.Text), IncrementBy: 0),
        PatchKind.RemoveTitle => new PatchOperation(PatchOperationKind.Remove, ["title"], Value: null, IncrementBy: 0),
        _ => new PatchOperation(PatchOperationKind.Increment, ["counter"], Value: null, spec.By),
    };
}
