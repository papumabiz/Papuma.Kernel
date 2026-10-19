// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// <c>LoadAllByKeyAsync</c> on SQLite: the same semantics as on PostgreSQL — paged, ordinal id
/// order, scope-bound, transactional — backed by the declared key's <c>json_extract</c> index.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteLoadAllByKeyTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private KernelModel _model = null!;
    private SqliteDocumentStore _store = null!;

    public SqliteLoadAllByKeyTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record KeyedNote(string Id, string ThreadId, string Text);

    private sealed record KeyedPart(string Id, string ThreadId, int Part);

    public async Task InitializeAsync()
    {
        _model = new KernelModelBuilder()
            .Document<KeyedNote>(d => d.LookupKey(x => x.ThreadId))
            .Document<KeyedPart>(d => d.LookupKey(x => new { x.ThreadId, x.Part }))
            .Build();
        _store = await _fixture.CreateStoreAsync(_model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ScopeContext NewTenant() => ScopeContext.Tenant(Guid.NewGuid());

    private static string[] Ids(IReadOnlyList<DocumentResult<KeyedNote>> page) =>
        [.. page.Select(r => r.Document.Id)];

    [Fact]
    public async Task ReturnsOnlyTheMatchingDocuments_InIdOrder()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new KeyedNote("n3", "t1", "c"), 0);
        await session.SaveAsync(new KeyedNote("n1", "t1", "a"), 0);
        await session.SaveAsync(new KeyedNote("n2", "t2", "other thread"), 0);
        await session.SaveAsync(new KeyedNote("n4", "t1", "d"), 0);

        var page = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 10);

        Assert.Equal(["n1", "n3", "n4"], Ids(page));
        Assert.Empty(await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "nobody", limit: 10));
    }

    [Fact]
    public async Task Pages_WithAfterId_UntilExhausted()
    {
        await using var session = _store.OpenSession(NewTenant());
        for (var i = 1; i <= 5; i++)
        {
            await session.SaveAsync(new KeyedNote($"n{i}", "t1", "x"), 0);
        }

        var first = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 2);
        var second = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 2, afterId: first[^1].Document.Id);
        var third = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 2, afterId: second[^1].Document.Id);
        var fourth = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 2, afterId: third[^1].Document.Id);

        Assert.Equal(["n1", "n2"], Ids(first));
        Assert.Equal(["n3", "n4"], Ids(second));
        Assert.Equal(["n5"], Ids(third));
        Assert.Empty(fourth);
    }

    [Fact]
    public async Task IdOrder_IsOrdinal_NotLocaleDependent()
    {
        await using var session = _store.OpenSession(NewTenant());
        // Ordinal: 'B' (0x42) < 'a' (0x61) < 'b' (0x62) — the same order PostgreSQL gives with COLLATE "C".
        foreach (var id in new[] { "b2", "a1", "B1" })
        {
            await session.SaveAsync(new KeyedNote(id, "t1", "x"), 0);
        }

        var all = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 10);
        var afterB = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 10, afterId: "B1");

        Assert.Equal(["B1", "a1", "b2"], Ids(all));
        Assert.Equal(["a1", "b2"], Ids(afterB));
    }

    [Fact]
    public async Task IsScopeBound_AndSeesTheSessionsOwnUncommittedWrites()
    {
        var tenant = NewTenant();
        await using (var committed = _store.OpenSession(tenant))
        {
            await committed.SaveAsync(new KeyedNote("n1", "t1", "committed"), 0);
            await committed.CommitAsync();
        }

        await using (var foreign = _store.OpenSession(NewTenant()))
        {
            await foreign.SaveAsync(new KeyedNote("n9", "t1", "other tenant"), 0);
            await foreign.CommitAsync();
        }

        await using var session = _store.OpenSession(tenant);
        await session.SaveAsync(new KeyedNote("n2", "t1", "uncommitted"), 0);

        var page = await session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 10);

        Assert.Equal(["n1", "n2"], Ids(page));
    }

    [Fact]
    public async Task CompositeKey_MatchesOneValuePerComponent()
    {
        await using var session = _store.OpenSession(NewTenant());
        await session.SaveAsync(new KeyedPart("p1", "t1", 1), 0);
        await session.SaveAsync(new KeyedPart("p2", "t1", 1), 0);
        await session.SaveAsync(new KeyedPart("p3", "t1", 2), 0);

        var page = await session.LoadAllByKeyAsync<KeyedPart>(x => new { x.ThreadId, x.Part }, ["t1", 1], limit: 10);

        Assert.Equal(["p1", "p2"], page.Select(r => r.Document.Id));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadAllByKeyAsync<KeyedPart>(x => new { x.ThreadId, x.Part }, ["t1"], limit: 10));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadAllByKeyAsync<KeyedPart>(x => new { x.ThreadId, x.Part }, (object)"t1", limit: 10));
    }

    [Fact]
    public async Task RejectsUndeclaredKey_AndOutOfRangeLimit_AndInvalidAfterId()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadAllByKeyAsync<KeyedNote>(x => x.Text, "x", limit: 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: KeyMetadata.MaxLoadAllLimit + 1));
        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, "t1", limit: 10, afterId: ""));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => session.LoadAllByKeyAsync<KeyedNote>(x => x.ThreadId, null!, limit: 10));
    }

    [Fact]
    public async Task QueryShape_UsesTheDeclaredKeyIndex()
    {
        var indexName = _model.GetRequired<KeyedNote>().Keys.Single().IndexName;

        await using var connection = await SqliteConnectionFactory.OpenAsync(_fixture.ConnectionString);
        await using var cmd = connection.CreateCommand();
        // The SQL text the session runs, with parameters bound.
        cmd.Parameters.AddWithValue("scope", "Tenant");
        cmd.Parameters.AddWithValue("tenantId", "t");
        cmd.Parameters.AddWithValue("value0", "t1");
        cmd.Parameters.AddWithValue("afterId", "a");
        cmd.Parameters.AddWithValue("limit", 10);

        foreach (var hasAfterId in new[] { false, true })
        {
            cmd.CommandText = "EXPLAIN QUERY PLAN "
                + SqliteDocumentSession.BuildLoadAllByKeySql(nameof(KeyedNote), ["$.threadId"], hasAfterId);

            var plan = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(3));
                }
            }

            Assert.True(
                plan.Any(line => line.Contains(indexName, StringComparison.Ordinal)),
                $"afterId={hasAfterId}: {string.Join(" | ", plan)}");
        }
    }
}
