// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Serialization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

[Collection(PostgresCollection.Name)]
public sealed class SchemaManagerTests
{
    private readonly PostgresFixture _fixture;

    public SchemaManagerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnsureSchemaAsync_IsIdempotent()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'papuma'
              AND table_name IN ('document', 'change', 'checkpoint', 'failure')
            """;

        var tableCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(4, tableCount);
    }

    [Fact]
    public async Task EnsureSchemaAsync_EnablesForcedRowLevelSecurity_OnTenantTables()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);

        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT relname
            FROM pg_class
            JOIN pg_namespace ON pg_namespace.oid = pg_class.relnamespace
            WHERE nspname = 'papuma'
              AND relname IN ('document', 'change')
              AND relrowsecurity
              AND relforcerowsecurity
            """;

        var secured = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            secured.Add(reader.GetString(0));
        }

        Assert.Equal(["change", "document"], secured.Order().ToArray());
    }

    private sealed record Widget(string Id, string Color, string Size);

    private sealed record Gadget(string Id, string Serial);

    private sealed record Dashed(string Id, [property: UniqueKey, JsonPropertyName("a-b")] string Value);

    private static KernelModel WidgetModel() => new KernelModelBuilder()
        .Document<Widget>(d => d.LookupKey(x => x.Color))
        .Build();

    private static KernelModel GadgetModel() => new KernelModelBuilder()
        .Document<Gadget>(d => d.UniqueKey(x => x.Serial))
        .Build();

    private static async Task<bool> IndexExistsAsync(ScratchPostgresDatabase db, string name) =>
        await db.ScalarAsync<bool>($"SELECT to_regclass('papuma.{name}') IS NOT NULL");

    private static string IndexNameOf(KernelModel model) =>
        model.DocumentTypes.Single().Keys.Single().IndexName;

    // Review S-11: parallel first starts on an empty database used to race on CREATE ... IF NOT EXISTS.
    [Fact]
    public async Task EnsureSchemaAsync_ParallelFirstStartsOnAnEmptyDatabase_AllSucceed_AndApplyTheDdlOnce()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var model = WidgetModel();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SchemaManager.EnsureSchemaAsync(db.DataSource, model)));

        Assert.True(await IndexExistsAsync(db, IndexNameOf(model)));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.schema_state WHERE component = 'kernel'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.schema_state WHERE component = 'keys'"));
    }

    // Review N-8: every start used to take ACCESS EXCLUSIVE on the kernel tables, queueing behind any
    // open transaction (and, behind that queued lock, all traffic).
    [Fact]
    public async Task EnsureSchemaAsync_AnUnchangedRestart_DoesNotWaitForOpenTransactionsOnTheKernelTables()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var model = WidgetModel();
        await SchemaManager.EnsureSchemaAsync(db.DataSource, model);

        await using var reader = await db.DataSource.OpenConnectionAsync();
        await using var readerTx = await reader.BeginTransactionAsync();
        await using (var hold = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM papuma.document, papuma.change, papuma.event", reader, readerTx))
        {
            await hold.ExecuteScalarAsync(); // ACCESS SHARE on all three, held until the transaction ends
        }

        await SchemaManager.EnsureSchemaAsync(db.DataSource, model).WaitAsync(TimeSpan.FromSeconds(15));

        await readerTx.CommitAsync();
    }

    [Fact]
    public async Task EnsureSchemaAsync_SkipsTheDdl_WhenSchemaAndModelAreUnchanged()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var model = WidgetModel();
        await SchemaManager.EnsureSchemaAsync(db.DataSource, model);
        await db.ExecuteAsync("DROP INDEX papuma.ix_papuma_change_txid");
        await db.ExecuteAsync($"DROP INDEX papuma.{IndexNameOf(model)}");

        await SchemaManager.EnsureSchemaAsync(db.DataSource, model);

        // The recorded DDL is not re-run — neither the kernel script nor the key indexes.
        Assert.False(await IndexExistsAsync(db, "ix_papuma_change_txid"));
        Assert.False(await IndexExistsAsync(db, IndexNameOf(model)));
    }

    [Fact]
    public async Task EnsureSchemaAsync_ReappliesTheDdl_AfterTheRecordIsDeleted()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var model = WidgetModel();
        await SchemaManager.EnsureSchemaAsync(db.DataSource, model);
        await db.ExecuteAsync("DROP INDEX papuma.ix_papuma_change_txid");
        await db.ExecuteAsync($"DROP INDEX papuma.{IndexNameOf(model)}");
        await db.ExecuteAsync("DELETE FROM papuma.schema_state");

        await SchemaManager.EnsureSchemaAsync(db.DataSource, model);

        Assert.True(await IndexExistsAsync(db, "ix_papuma_change_txid"));
        Assert.True(await IndexExistsAsync(db, IndexNameOf(model)));
    }

    // Several models share one database (tests, modular apps): a recorded model never masks another one's DDL.
    [Fact]
    public async Task EnsureSchemaAsync_ADifferentModel_AppliesItsOwnKeyDdl_AlsoAfterAnotherModelWasRecorded()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var widgets = WidgetModel();
        var gadgets = GadgetModel();

        await SchemaManager.EnsureSchemaAsync(db.DataSource, widgets);
        Assert.False(await IndexExistsAsync(db, IndexNameOf(gadgets)));

        await SchemaManager.EnsureSchemaAsync(db.DataSource, gadgets);
        Assert.True(await IndexExistsAsync(db, IndexNameOf(gadgets)));

        await db.ExecuteAsync($"DROP INDEX papuma.{IndexNameOf(widgets)}");
        await SchemaManager.EnsureSchemaAsync(db.DataSource, widgets); // recorded: skipped, gadgets' index untouched
        await SchemaManager.EnsureSchemaAsync(db.DataSource, new KernelModelBuilder().Build()); // no keys at all

        Assert.True(await IndexExistsAsync(db, IndexNameOf(gadgets)));
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.schema_state WHERE component = 'keys'"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.schema_state WHERE component = 'kernel'"));
    }

    // A model with a new key on an existing, recorded database gets the new index.
    [Fact]
    public async Task EnsureSchemaAsync_AModelThatGainedAKey_AppliesTheNewIndex()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource, new KernelModelBuilder().Document<Widget>().Build());

        var withKey = WidgetModel();
        await SchemaManager.EnsureSchemaAsync(db.DataSource, withKey);

        Assert.True(await IndexExistsAsync(db, IndexNameOf(withKey)));
    }

    // A database created before schema_state existed: the kernel script runs once and gets recorded.
    [Fact]
    public async Task EnsureSchemaAsync_ADatabaseWithoutTheRecord_RunsTheScriptAndRecordsIt()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        await SchemaManager.EnsureSchemaAsync(db.DataSource);
        await db.ExecuteAsync("DROP TABLE papuma.schema_state; DROP INDEX papuma.ix_papuma_change_txid");

        await SchemaManager.EnsureSchemaAsync(db.DataSource);

        Assert.True(await IndexExistsAsync(db, "ix_papuma_change_txid"));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM papuma.schema_state"));
    }

    [Fact]
    public async Task EnsureSchemaAsync_AnInvalidModel_FailsBeforeAnyDdl()
    {
        await using var db = await ScratchPostgresDatabase.CreateAsync(_fixture.Database);
        var bad = new KernelModelBuilder().Document<Dashed>().Build(); // 'a-b' cannot be quoted into the index DDL

        await Assert.ThrowsAsync<ArgumentException>(() => SchemaManager.EnsureSchemaAsync(db.DataSource, bad));

        Assert.False(await db.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'papuma')"));
    }

    [Fact]
    public void EnsureMinimumServerVersion_AcceptsPostgres18()
    {
        SchemaManager.EnsureMinimumServerVersion(180000);
        SchemaManager.EnsureMinimumServerVersion(190001);
    }

    [Fact]
    public void EnsureMinimumServerVersion_RejectsOlderServers()
    {
        var ex = Assert.Throws<PostgresVersionNotSupportedException>(
            () => SchemaManager.EnsureMinimumServerVersion(170004));

        Assert.Equal(170004, ex.ServerVersionNum);
        Assert.Contains("RETURNING OLD/NEW", ex.Message, StringComparison.Ordinal);
    }
}
