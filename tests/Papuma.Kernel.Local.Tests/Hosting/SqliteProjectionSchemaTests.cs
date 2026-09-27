// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Hosting;

/// <summary>
/// <see cref="ISqliteSchemaContributor"/>: application tables created after the kernel
/// schema and before the feed workers, idempotent across restarts; a projection handler
/// writing into them from its own connection.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteProjectionSchemaTests
{
    private readonly SqliteFixture _fixture;

    public SqliteProjectionSchemaTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record BoardTicket(string Id, string Title);

    private sealed class StartupLog
    {
        public ConcurrentQueue<string> Entries { get; } = new();
    }

    private sealed class TicketBoardSchema(StartupLog log) : ISqliteSchemaContributor
    {
        public async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS ticket_board
                (
                    scope     TEXT    NOT NULL,
                    tenant_id TEXT    NOT NULL,
                    id        TEXT    NOT NULL,
                    title     TEXT    NOT NULL,
                    version   INTEGER NOT NULL,
                    PRIMARY KEY (scope, tenant_id, id)
                );
                CREATE INDEX IF NOT EXISTS ix_ticket_board_title ON ticket_board (scope, tenant_id, title);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            log.Entries.Enqueue("schema");
        }
    }

    private sealed class TicketBoardProjection(string connectionString, StartupLog log) : IChangeHandler
    {
        public string Name { get; } = $"ticket-board-{Guid.NewGuid():N}";

        public async Task HandleAsync(ChangeRecord change, CancellationToken ct)
        {
            log.Entries.Enqueue("handler");
            if (change.DocumentType != nameof(BoardTicket) || !change.FieldChanged("title"))
            {
                return;
            }

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ticket_board (scope, tenant_id, id, title, version)
                VALUES (@scope, @tenantId, @id, @title, @version)
                ON CONFLICT (scope, tenant_id, id) DO UPDATE
                    SET title = excluded.title, version = excluded.version
                    WHERE ticket_board.version < excluded.version
                """;
            cmd.Parameters.AddWithValue("scope", change.Scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", change.Scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("id", change.DocumentId);
            cmd.Parameters.AddWithValue("title", (string?)change.Diff.Entries["title"].New ?? string.Empty);
            cmd.Parameters.AddWithValue("version", change.Version);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task Contributor_RunsBeforeTheFeedWorkers_AndIsIdempotentAcrossRestarts()
    {
        var tenant = ScopeContext.Tenant(Guid.NewGuid());

        foreach (var run in new[] { 1, 2 })
        {
            var log = new StartupLog();
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(log);
            builder.Services
                .AddPapumaKernelLocal(o =>
                {
                    o.ConnectionString = _fixture.ConnectionString;
                    o.Model(m => m.Document<BoardTicket>());
                })
                .AddSchemaContributor<TicketBoardSchema>();
            builder.Services.AddSingleton<IChangeHandler>(
                _ => new TicketBoardProjection(_fixture.ConnectionString, log));

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var ticketId = Guid.NewGuid().ToString("N");
                var store = host.Services.GetRequiredService<SqliteDocumentStore>();
                await using (var session = store.OpenSession(tenant))
                {
                    await session.SaveAsync(new BoardTicket(ticketId, $"ticket of run {run}"), 0);
                    await session.CommitAsync();
                }

                await WaitUntilAsync(() => RowExistsAsync(tenant, ticketId));
                Assert.Equal("schema", log.Entries.First());
            }
            finally
            {
                await host.StopAsync();
            }
        }
    }

    private async Task<bool> RowExistsAsync(ScopeContext tenant, string ticketId)
    {
        await using var conn = new SqliteConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM ticket_board WHERE tenant_id = @tenantId AND id = @id";
        cmd.Parameters.AddWithValue("tenantId", tenant.TenantId!);
        cmd.Parameters.AddWithValue("id", ticketId);
        return (long)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 20 s.");
            await Task.Delay(50);
        }
    }
}
