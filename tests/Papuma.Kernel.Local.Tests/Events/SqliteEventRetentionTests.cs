// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Events;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Events;

/// <summary>
/// Tests for <see cref="SqliteEventRetention"/> — direct port of the Postgres kernel's
/// <c>EventRetention</c>: retention is opt-in per event type, types without it are never
/// touched.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteEventRetentionTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;
    private KernelModel _model = null!;

    public SqliteEventRetentionTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record ShortLived(string Marker);

    private sealed record KeptForever(string Marker);

    public async Task InitializeAsync()
    {
        _model = new KernelModelBuilder()
            .Event<ShortLived>(e => e.Retention(TimeSpan.Zero)) // purgeable immediately
            .Event<KeptForever>()
            .Build();
        _store = await _fixture.CreateStoreAsync(_model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task PurgeExpiredAsync_DeletesOnlyRetentionConfiguredAndExpiredEvents()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using (var session = _store.OpenSession(NewTenant()))
        {
            await session.AppendAsync(new ShortLived(marker));
            await session.AppendAsync(new KeptForever(marker));
            await session.CommitAsync();
        }

        await SqliteEventRetention.PurgeExpiredAsync(_fixture.ConnectionString, _model);

        Assert.Equal(0, await CountEventsAsync(nameof(ShortLived), marker));
        Assert.Equal(1, await CountEventsAsync(nameof(KeptForever), marker));
    }

    private async Task<int> CountEventsAsync(string eventType, string marker)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM event
            WHERE event_type = @type AND json_extract(payload, '$.marker') = @marker
            """;
        cmd.Parameters.AddWithValue("type", eventType);
        cmd.Parameters.AddWithValue("marker", marker);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }
}
