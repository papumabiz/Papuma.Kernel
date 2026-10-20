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

    private sealed record BacklogA(string Note);

    private sealed record BacklogB(string Note);

    private sealed record BacklogKept(string Note);

    [Fact]
    public async Task PurgeExpiredAsync_DeletesABacklogInBatches_AndKeepsEverythingElse()
    {
        var model = BacklogModel<BacklogA>();
        await _fixture.CreateStoreAsync(model);
        await SeedAsync(nameof(BacklogA), count: 2500, age: TimeSpan.FromHours(2));
        await SeedAsync(nameof(BacklogA), count: 3, age: TimeSpan.Zero);       // not expired yet
        await SeedAsync(nameof(BacklogKept), count: 2, age: TimeSpan.FromHours(5)); // no retention

        var batches = new List<int>();
        var deleted = await SqliteEventRetention.PurgeExpiredAsync(
            _fixture.ConnectionString, model, SqliteEventRetention.DefaultBatchSize, batches.Add, default);

        Assert.Equal(2500, deleted);
        Assert.Equal([1000, 1000, 500], batches);
        Assert.Equal(3, await CountByTypeAsync(nameof(BacklogA)));
        Assert.Equal(2, await CountByTypeAsync(nameof(BacklogKept)));
    }

    [Fact]
    public async Task PurgeExpiredAsync_CancelledAfterTheFirstBatch_KeepsTheCommittedBatch()
    {
        var model = BacklogModel<BacklogB>();
        await _fixture.CreateStoreAsync(model);
        await SeedAsync(nameof(BacklogB), count: 2500, age: TimeSpan.FromHours(2));

        using var cts = new CancellationTokenSource();
        var batches = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqliteEventRetention.PurgeExpiredAsync(
                _fixture.ConnectionString, model, SqliteEventRetention.DefaultBatchSize,
                deleted =>
                {
                    batches.Add(deleted);
                    cts.Cancel();
                },
                cts.Token));

        Assert.Equal([1000], batches);
        Assert.Equal(1500, await CountByTypeAsync(nameof(BacklogB)));
    }

    [Fact]
    public async Task PurgeExpiredAsync_RejectsBatchSizeBelowOne()
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            SqliteEventRetention.PurgeExpiredAsync(_fixture.ConnectionString, _model, 0, null, default));

        Assert.Equal("batchSize", ex.ParamName);
    }

    // One purgeable type per test: the purge works across the whole shared database.
    private static KernelModel BacklogModel<TPurged>()
        where TPurged : class => new KernelModelBuilder()
        .Event<TPurged>(e => e.Retention(TimeSpan.FromHours(1)))
        .Event<BacklogKept>()
        .Build();

    private async Task SeedAsync(string eventType, int count, TimeSpan age)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO event (scope, tenant_id, event_type, payload, occurred_at)
            VALUES ('Tenant', @tenantId, @type, '{"note":"x"}', @at)
            """;
        var tenant = cmd.Parameters.Add("tenantId", SqliteType.Text);
        tenant.Value = $"t{Guid.NewGuid():N}";
        cmd.Parameters.AddWithValue("type", eventType);
        cmd.Parameters.AddWithValue("at", (DateTimeOffset.UtcNow - age).ToString("O"));
        for (var i = 0; i < count; i++)
        {
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private async Task<int> CountByTypeAsync(string eventType)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM event WHERE event_type = @type";
        cmd.Parameters.AddWithValue("type", eventType);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
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
