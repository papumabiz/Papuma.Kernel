// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Gdpr;

/// <summary>
/// The export promises one consistent snapshot (review S-2): state and history come from the
/// same moment, even when a writer commits between the export's queries.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GdprExportSnapshotTests(PostgresFixture fixture)
{
    private sealed record Person(string Id, string Name);

    [Fact]
    public async Task Export_SeesStateAndHistoryOfTheSameMoment_WhileAWriterCommitsInBetween()
    {
        await using var scratch = await ScratchPostgresDatabase.CreateAsync(fixture.Database);
        var model = new KernelModelBuilder().Document<Person>().Build();
        await SchemaManager.EnsureSchemaAsync(scratch.DataSource, model);
        var store = new DocumentStore(scratch.DataSource, model);
        var scope = ScopeContext.Tenant("t-snapshot");

        await using (var session = store.OpenSession(scope))
        {
            await session.SaveAsync(new Person("p1", "Harry"), 0);
            await session.CommitAsync();
        }

        // The writer holds a lock on papuma.change, so the export's history query — its second
        // query — has to wait. The writer's change commits while the export is parked there.
        await using var writer = await scratch.DataSource.OpenConnectionAsync();
        await using var writerTx = await writer.BeginTransactionAsync();
        await ExecuteAsync(writer, writerTx, "LOCK TABLE papuma.change IN ACCESS EXCLUSIVE MODE");

        var export = GdprExport.ExportAsync(store, scope, [new DocumentRef(nameof(Person), "p1")]);
        await WaitUntilBlockedOnChangeTableAsync(scratch.DataSource, export);

        await ExecuteAsync(writer, writerTx, """
            UPDATE papuma.document SET data = jsonb_set(data, '{name}', '"Harald"'), version = 2
            WHERE id = 'p1';
            INSERT INTO papuma.change (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff)
            VALUES ('Tenant', 't-snapshot', 'Person', 'p1', 2, 1, 2, '{}');
            """);
        await writerTx.CommitAsync();

        var document = Assert.Single((await export)["documents"]!.AsArray())!;
        var history = document["history"]!.AsArray();
        Assert.Equal((long)document["version"]!, (long)history[^1]!["version"]!);
        Assert.Equal(1, (long)document["version"]!); // the snapshot predates the writer's commit
        Assert.Single(history);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, transaction);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilBlockedOnChangeTableAsync(NpgsqlDataSource dataSource, Task export)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (export.IsCompleted)
            {
                await export; // surfaces a failure instead of waiting for a block that never comes
                throw new InvalidOperationException("The export finished without waiting for the lock.");
            }

            await using var cmd = dataSource.CreateCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock' AND query LIKE '%FROM papuma.change%'
                """);
            if ((long)(await cmd.ExecuteScalarAsync(timeout.Token))! > 0)
            {
                return;
            }

            await Task.Delay(20, timeout.Token);
        }
    }
}
