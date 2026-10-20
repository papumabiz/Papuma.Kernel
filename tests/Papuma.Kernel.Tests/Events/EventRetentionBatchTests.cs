// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Events;

/// <summary>
/// Event retention deletes in batches, one transaction each (review P-8): a backlog
/// never becomes one huge transaction, and cancellation keeps the committed batches.
/// Each test owns its event type — the purge works across tenants on a shared database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EventRetentionBatchTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public EventRetentionBatchTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record BacklogA(string Note);

    private sealed record BacklogB(string Note);

    private sealed record BacklogC(string Note);

    private sealed record NotPurged(string Note);

    public async Task InitializeAsync()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, ModelFor<BacklogA>());
        await _fixture.GrantAppRoleAccessAsync();
    }

    // Leave no backlog behind: other tests read the shared event log from seq 0 with a
    // fresh handler and only see the first batch.
    public async Task DisposeAsync()
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM papuma.event WHERE event_type = ANY(@types)";
        cmd.Parameters.AddWithValue(
            "types", new[] { nameof(BacklogA), nameof(BacklogB), nameof(BacklogC), nameof(NotPurged) });
        await cmd.ExecuteNonQueryAsync();
    }

    // One purgeable type per test: the purge works across the whole shared database, so a
    // backlog left behind by another test must not be purged (and counted) here.
    private static KernelModel ModelFor<TPurged>()
        where TPurged : class => new KernelModelBuilder()
        .Event<TPurged>(e => e.Retention(TimeSpan.FromHours(1)))
        .Event<NotPurged>()
        .Build();

    [Fact]
    public async Task Purge_DeletesABacklogInBatches_AndKeepsEverythingElse()
    {
        var tenantOne = $"t{Guid.NewGuid():N}";
        var tenantTwo = $"t{Guid.NewGuid():N}";
        await InsertAsync(nameof(BacklogA), tenantOne, count: 1700, ageHours: 2);
        await InsertAsync(nameof(BacklogA), tenantTwo, count: 800, ageHours: 2);
        await InsertAsync(nameof(BacklogA), tenantOne, count: 3, ageHours: 0);           // not expired yet
        await InsertAsync(nameof(NotPurged), tenantOne, count: 2, ageHours: 5);           // no retention

        var batches = new List<int>();
        var deleted = await EventRetention.PurgeExpiredAsync(
            _fixture.AppRoleDataSource, ModelFor<BacklogA>(), EventRetention.DefaultBatchSize, batches.Add, default);

        Assert.Equal(2500, deleted);
        Assert.Equal([1000, 1000, 500], batches);
        Assert.Equal(3, await CountAsync(nameof(BacklogA)));
        Assert.Equal(2, await CountAsync(nameof(NotPurged)));
    }

    [Fact]
    public async Task Purge_PublicOverload_DeletesTheWholeBacklog()
    {
        await InsertAsync(nameof(BacklogB), $"t{Guid.NewGuid():N}", count: 2500, ageHours: 2);

        var deleted = await EventRetention.PurgeExpiredAsync(_fixture.AppRoleDataSource, ModelFor<BacklogB>());

        Assert.True(deleted >= 2500);
        Assert.Equal(0, await CountAsync(nameof(BacklogB)));
    }

    [Fact]
    public async Task Purge_CancelledAfterTheFirstBatch_KeepsTheCommittedBatch()
    {
        await InsertAsync(nameof(BacklogC), $"t{Guid.NewGuid():N}", count: 2500, ageHours: 2);

        using var cts = new CancellationTokenSource();
        var batches = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EventRetention.PurgeExpiredAsync(
                _fixture.AppRoleDataSource, ModelFor<BacklogC>(), EventRetention.DefaultBatchSize,
                deleted =>
                {
                    batches.Add(deleted);
                    cts.Cancel();
                },
                cts.Token));

        Assert.Equal([1000], batches);
        Assert.Equal(1500, await CountAsync(nameof(BacklogC)));
    }

    [Fact]
    public async Task Purge_RejectsBatchSizeBelowOne()
    {
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            EventRetention.PurgeExpiredAsync(_fixture.AppRoleDataSource, ModelFor<BacklogA>(), 0, null, default));

        Assert.Equal("batchSize", ex.ParamName);
    }

    private async Task InsertAsync(string eventType, string tenantId, int count, int ageHours)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO papuma.event (scope, tenant_id, event_type, payload, occurred_at)
            SELECT 'Tenant', @tenantId, @type, '{"note":"x"}'::jsonb, now() - make_interval(hours => @age)
            FROM generate_series(1, @count)
            """;
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("type", eventType);
        cmd.Parameters.AddWithValue("age", ageHours);
        cmd.Parameters.AddWithValue("count", count);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string eventType)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM papuma.event WHERE event_type = @type";
        cmd.Parameters.AddWithValue("type", eventType);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
