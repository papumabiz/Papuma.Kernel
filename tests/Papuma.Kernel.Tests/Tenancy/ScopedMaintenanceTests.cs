// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// A row written past the kernel with a tenant id outside the whitelist must not block cross-tenant
/// maintenance for every tenant (review S-14): it is skipped and logged.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopedMaintenanceTests(PostgresFixture fixture)
{
    private const string BadTenant = "bad tenant!";

    private sealed record Expiring(string Note);

    private static KernelModel Model() => new KernelModelBuilder()
        .Event<Expiring>(e => e.Retention(TimeSpan.FromHours(1)))
        .Build();

    [Theory]
    [InlineData("Tenant", "acme-1")]
    [InlineData("Tenant", "AB")]
    public void TryFromRow_AcceptsAValidTenantRow(string scope, string tenantId)
    {
        Assert.True(ScopedMaintenance.TryFromRow(scope, tenantId, out var result));
        Assert.Equal(ScopeContext.Tenant(tenantId), result);
    }

    [Fact]
    public void TryFromRow_ReadsAPlatformRowWhateverItsTenantColumnHolds()
    {
        Assert.True(ScopedMaintenance.TryFromRow("Platform", string.Empty, out var result));
        Assert.Equal(ScopeContext.Platform(), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("bad tenant!")]
    [InlineData("x'; DROP TABLE papuma.event; --")]
    [InlineData("tenant\nline")]
    public void TryFromRow_RejectsATenantRowOutsideTheWhitelist(string tenantId)
    {
        Assert.False(ScopedMaintenance.TryFromRow("Tenant", tenantId, out var result));
        Assert.Null(result);
    }

    private static string InsertEvent(string tenantId, string txid = "pg_current_xact_id()") => $$"""
        INSERT INTO papuma.event (scope, tenant_id, event_type, payload, occurred_at, txid)
        VALUES ('Tenant', '{{tenantId}}', 'Expiring', '{"note":"n"}', now() - interval '2 hours', {{txid}});
        """;

    [Fact]
    public async Task Retention_SkipsARowWithAnInvalidTenant_AndStillPurgesTheOthers()
    {
        await using var scratch = await ScratchPostgresDatabase.CreateAsync(fixture.Database);
        await SchemaManager.EnsureSchemaAsync(scratch.DataSource, Model());
        await scratch.ExecuteAsync(InsertEvent("good-tenant") + InsertEvent(BadTenant) + InsertEvent("other-tenant"));

        var deleted = await EventRetention.PurgeExpiredAsync(
            scratch.DataSource, Model(), EventRetention.DefaultBatchSize, onBatchCommitted: null, default);

        Assert.Equal(2, deleted);
        Assert.Equal(BadTenant, await scratch.ScalarAsync<string>("SELECT tenant_id FROM papuma.event"));
    }

    [Fact]
    public async Task Retention_LogsTheSkippedRow()
    {
        await using var scratch = await ScratchPostgresDatabase.CreateAsync(fixture.Database);
        await SchemaManager.EnsureSchemaAsync(scratch.DataSource, Model());
        await scratch.ExecuteAsync(InsertEvent(BadTenant));
        var logger = new ListLogger();

        await EventRetention.PurgeExpiredAsync(
            scratch.DataSource, Model(), EventRetention.DefaultBatchSize, onBatchCommitted: null, default, logger);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(BadTenant, warning, StringComparison.Ordinal);
        Assert.Contains(nameof(Expiring), warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreRepair_SkipsARowWithAnInvalidTenant_AndStillRepairsTheOthers()
    {
        await using var scratch = await ScratchPostgresDatabase.CreateAsync(fixture.Database);
        await SchemaManager.EnsureSchemaAsync(scratch.DataSource, Model());
        // Transaction ids from a foreign cluster lie beyond this one's counter (ADR-022, point 7).
        const string Foreign = "((pg_snapshot_xmax(pg_current_snapshot())::text::bigint) + 50000000)::text::xid8";
        await scratch.ExecuteAsync(InsertEvent("good-tenant", Foreign) + InsertEvent(BadTenant, Foreign));

        Assert.True(await SchemaManager.RepairFeedAfterLogicalRestoreAsync(scratch.DataSource));

        Assert.Equal("2", await scratch.ScalarAsync<string>(
            "SELECT txid::text FROM papuma.event WHERE tenant_id = 'good-tenant'"));
        Assert.NotEqual("2", await scratch.ScalarAsync<string>(
            $"SELECT txid::text FROM papuma.event WHERE tenant_id = '{BadTenant}'"));
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
