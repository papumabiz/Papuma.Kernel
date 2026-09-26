// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Hosting;

/// <summary>
/// The projection-schema recipe, run as written: a schema contributor applied after the
/// kernel schema and before the feed workers, idempotent across restarts and safe when
/// several instances start at once.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProjectionSchemaTests
{
    private readonly PostgresFixture _fixture;

    public ProjectionSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record BoardTicket(string Id, string Title);

    /// <summary>The recipe's contributor, verbatim apart from the class name.</summary>
    private sealed class TicketBoardSchema(StartupLog log) : ISchemaContributor
    {
        public async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct)
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT pg_advisory_xact_lock(hashtext('app.schema'));

                CREATE SCHEMA IF NOT EXISTS app;

                CREATE TABLE IF NOT EXISTS app.ticket_board
                (
                    scope     text   NOT NULL,
                    tenant_id text   NOT NULL,
                    id        text   NOT NULL,
                    title     text   NOT NULL,
                    version   bigint NOT NULL,
                    PRIMARY KEY (scope, tenant_id, id)
                );

                ALTER TABLE app.ticket_board ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'open';
                CREATE INDEX IF NOT EXISTS ix_ticket_board_status ON app.ticket_board (scope, tenant_id, status);

                ALTER TABLE app.ticket_board ENABLE ROW LEVEL SECURITY;
                ALTER TABLE app.ticket_board FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS scope_isolation ON app.ticket_board;
                CREATE POLICY scope_isolation ON app.ticket_board
                    USING (papuma.scope_visible(scope, tenant_id))
                    WITH CHECK (papuma.scope_writable(scope, tenant_id));
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            log.Entries.Enqueue("schema");
        }
    }

    private sealed class StartupLog
    {
        public ConcurrentQueue<string> Entries { get; } = new();
    }

    /// <summary>Projects into the contributor's table; records when it first ran.</summary>
    private sealed class TicketBoardProjection(NpgsqlDataSource dataSource, StartupLog log) : IChangeHandler
    {
        public string Name { get; } = $"ticket-board-{Guid.NewGuid():N}";

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            log.Entries.Enqueue("handler");
            if (change.DocumentType != nameof(BoardTicket) || !change.FieldChanged("title"))
            {
                return;
            }

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.SetScopeAsync(tx, change.Scope, ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO app.ticket_board (scope, tenant_id, id, title, version)
                VALUES (@scope, @tenantId, @id, @title, @version)
                ON CONFLICT (scope, tenant_id, id) DO UPDATE
                    SET title = EXCLUDED.title, version = EXCLUDED.version
                    WHERE app.ticket_board.version < EXCLUDED.version
                """;
            cmd.Parameters.AddWithValue("scope", change.Scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", change.Scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("id", change.DocumentId);
            cmd.Parameters.AddWithValue("title", (string?)change.Diff.Entries["title"].New ?? string.Empty);
            cmd.Parameters.AddWithValue("version", change.Version);
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }

    private IHost BuildHost(StartupLog log)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(log);
        builder.Services
            .AddPapumaKernel(o =>
            {
                o.DataSource = _fixture.DataSource; // externally owned (fixture)
                o.Model(m => m.Document<BoardTicket>());
            })
            .AddSchemaContributor<TicketBoardSchema>()
            .AddChangeHandler<TicketBoardProjection>();
        return builder.Build();
    }

    [Fact]
    public async Task Contributor_RunsBeforeTheFeedWorkers_AndIsIdempotentAcrossRestarts()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());

        foreach (var run in new[] { 1, 2 }) // second start: every statement already applied
        {
            var log = new StartupLog();
            using var host = BuildHost(log);
            await host.StartAsync();
            try
            {
                var ticketId = Guid.NewGuid().ToString("N");
                var store = host.Services.GetRequiredService<DocumentStore>();
                await using (var session = store.OpenSession(tenant))
                {
                    await session.SaveAsync(new BoardTicket(ticketId, $"ticket of run {run}"), 0);
                    await session.CommitAsync();
                }

                await WaitForRowAsync(tenant, ticketId);
                Assert.Equal("schema", log.Entries.First());
            }
            finally
            {
                await host.StopAsync();
            }
        }

        Assert.Equal(1, await CountPoliciesAsync());
    }

    [Fact]
    public async Task Contributor_SerializesConcurrentStarts()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource); // the functions the policy uses
        var contributors = Enumerable.Range(0, 4).Select(_ => new TicketBoardSchema(new StartupLog()));

        await Task.WhenAll(contributors.Select(c => c.EnsureSchemaAsync(_fixture.DataSource, CancellationToken.None)));

        Assert.Equal(1, await CountPoliciesAsync());
    }

    private async Task WaitForRowAsync(ScopeContext tenant, string ticketId)
    {
        await WaitUntilAsync(async () =>
        {
            await using var conn = await _fixture.DataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM app.ticket_board WHERE tenant_id = @tenantId AND id = @id";
            cmd.Parameters.AddWithValue("tenantId", tenant.TenantId!);
            cmd.Parameters.AddWithValue("id", ticketId);
            return (long)(await cmd.ExecuteScalarAsync())! == 1;
        });
    }

    private async Task<long> CountPoliciesAsync()
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM pg_policies WHERE schemaname = 'app' AND tablename = 'ticket_board'";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 15 s.");
            await Task.Delay(50);
        }
    }
}
