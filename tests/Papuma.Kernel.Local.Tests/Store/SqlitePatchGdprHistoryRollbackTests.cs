// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Stage 3 round-trip tests: Patch/BulkPatch/BulkDelete, GDPR redaction, masked reads,
/// history, and rollback. One or two tests per surface — full parity coverage lands in
/// Stage 5.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqlitePatchGdprHistoryRollbackTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqlitePatchGdprHistoryRollbackTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<Counter>(d => d.ExposeToMcp())
            .Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Counter(string Id, string Name, int Value = 0, string? Note = null);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task PatchAsync_Increment_IsAtomicAndCumulative()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits"), 0);

        await session.PatchAsync<Counter>(id, p => p.Increment(x => x.Value, 3));
        var result = await session.PatchAsync<Counter>(id, p => p.Increment(x => x.Value, 2));

        Assert.Equal(3, result.Version);
        var loaded = await session.LoadAsync<Counter>(id);
        Assert.Equal(5, loaded!.Document.Value);
    }

    [Fact]
    public async Task PatchAsync_GetDocument_ReturnsIncrementedValue()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        var inserted = await session.SaveAsync(new Counter(id, "hits", Value: 41), 0);

        var result = await session.PatchAsync<Counter>(id, p => p.Increment(x => x.Value));

        Assert.Equal(new Counter(id, "hits", Value: 41), inserted.GetDocument<Counter>());
        Assert.Equal(new Counter(id, "hits", Value: 42), result.GetDocument<Counter>());
    }

    [Fact]
    public async Task PatchAsync_SetAndRemove_ApplyBothOperations()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "hits", Note: "old"), 0);

        await session.PatchAsync<Counter>(id, p => p
            .Set(x => x.Name, "renamed")
            .Remove(x => x.Note));

        var loaded = await session.LoadAsync<Counter>(id);
        Assert.Equal("renamed", loaded!.Document.Name);
        Assert.Null(loaded.Document.Note);
    }

    [Fact]
    public async Task PatchManyAsync_AppliesToAllListedIds()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id1 = NewId();
        var id2 = NewId();
        await session.SaveAsync(new Counter(id1, "a"), 0);
        await session.SaveAsync(new Counter(id2, "b"), 0);

        var result = await session.PatchManyAsync<Counter>([id1, id2], p => p.Increment(x => x.Value, 1));

        Assert.Equal(2, result.Count);
        Assert.Equal(1, (await session.LoadAsync<Counter>(id1))!.Document.Value);
        Assert.Equal(1, (await session.LoadAsync<Counter>(id2))!.Document.Value);
    }

    [Fact]
    public async Task DeleteManyAsync_RemovesAllListedIds()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id1 = NewId();
        var id2 = NewId();
        await session.SaveAsync(new Counter(id1, "a"), 0);
        await session.SaveAsync(new Counter(id2, "b"), 0);

        var result = await session.DeleteManyAsync<Counter>([id1, id2]);

        Assert.Equal(2, result.Count);
        Assert.Null(await session.LoadAsync<Counter>(id1));
        Assert.Null(await session.LoadAsync<Counter>(id2));
    }

    [Fact]
    public async Task RedactHistoryAsync_RewritesDiffsToChangedMarker_AndIsIdempotent()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "secret-name"), 0);

        var rewritten = await session.RedactHistoryAsync<Counter>(id, "GDPR erasure request #42", ["name"]);
        Assert.Equal(1, rewritten);

        var history = await session.GetHistoryAsync<Counter>(id);
        Assert.Equal(DiffEntryKind.Redacted, history[0].Diff.Entries["name"].Kind);
        Assert.True(history[0].Metadata.ContainsKey("redaction"));

        // Idempotent: redacting again finds nothing left to change.
        var rewrittenAgain = await session.RedactHistoryAsync<Counter>(id, "second pass", ["name"]);
        Assert.Equal(0, rewrittenAgain);
    }

    [Fact]
    public async Task LoadMaskedAsync_ReturnsProjectedJson()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "Harry", 7), 0);

        var masked = await session.LoadMaskedAsync<Counter>(id);

        Assert.NotNull(masked);
        Assert.Equal(1, masked.Version);
        Assert.Equal("Harry", (string?)masked.Document["name"]);
        Assert.Equal(7, (int?)masked.Document["value"]);
    }

    [Fact]
    public async Task LoadMaskedAsync_ReturnsNull_WhenMissing()
    {
        await using var session = _store.OpenSession(NewTenant());

        Assert.Null(await session.LoadMaskedAsync<Counter>(NewId()));
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsChangesInAscendingVersionOrder()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "v1"), 0);
        await session.SaveAsync(new Counter(id, "v2"), 1);
        await session.SaveAsync(new Counter(id, "v3"), 2);

        var history = await session.GetHistoryAsync<Counter>(id);

        Assert.Equal(3, history.Count);
        Assert.Equal([1L, 2L, 3L], history.Select(h => h.Version));
        Assert.Equal(ChangeOperation.Insert, history[0].Operation);
        Assert.Equal(ChangeOperation.Update, history[2].Operation);
    }

    [Fact]
    public async Task GetHistoryAsync_RespectsVersionBounds()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "v1"), 0);
        await session.SaveAsync(new Counter(id, "v2"), 1);
        await session.SaveAsync(new Counter(id, "v3"), 2);

        var history = await session.GetHistoryAsync<Counter>(id, fromVersion: 2, toVersion: 2);

        Assert.Single(history);
        Assert.Equal(2, history[0].Version);
    }

    [Fact]
    public async Task RollbackAsync_RestoresPriorState_AsAppendOnlyUpdate()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "v1", 1), 0);
        await session.SaveAsync(new Counter(id, "v2", 2), 1);
        await session.SaveAsync(new Counter(id, "v3", 3), 2);

        var result = await session.RollbackAsync<Counter>(id, toVersion: 1, expectedVersion: 3);

        // Rollback is recorded as a normal Update at version 4 — never a 4th operation kind.
        Assert.Equal(4, result.Version);
        Assert.Equal(ChangeOperation.Update, result.Operation);

        var loaded = await session.LoadAsync<Counter>(id);
        Assert.Equal("v1", loaded!.Document.Name);
        Assert.Equal(1, loaded.Document.Value);

        var history = await session.GetHistoryAsync<Counter>(id);
        Assert.True((bool?)history[3].Metadata["isRollback"]);
        Assert.Equal(1, (long?)history[3].Metadata["restoredVersion"]);
    }

    [Fact]
    public async Task RollbackAsync_AcrossRedactedRange_ThrowsRollbackNotPossible()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new Counter(id, "v1"), 0);
        await session.SaveAsync(new Counter(id, "v2"), 1);
        await session.RedactHistoryAsync<Counter>(id, "erasure", ["name"]);

        await Assert.ThrowsAsync<RollbackNotPossibleException>(
            () => session.RollbackAsync<Counter>(id, toVersion: 1, expectedVersion: 2));
    }
}
