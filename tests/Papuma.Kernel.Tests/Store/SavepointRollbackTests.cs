// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// A rejected write rolls back to its savepoint (review Q-2, S-6, S-7). The rollback must
/// not depend on the caller's token — a request that was cancelled at the moment a write
/// failed must not leave the half-done write in the transaction for a later commit — and a
/// rollback that itself fails must make the session unusable instead of committable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SavepointRollbackTests
{
    private readonly PostgresFixture _fixture;

    public SavepointRollbackTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SavepointGuardDoc(string Id, string Name);

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static async Task<DocumentStore> StoreAsync(NpgsqlDataSource dataSource, Action<SavepointGuardDoc> validator)
    {
        var model = new KernelModelBuilder()
            .Document<SavepointGuardDoc>(d => d.Validate(validator))
            .Build();
        await SchemaManager.EnsureSchemaAsync(dataSource, model);
        return new DocumentStore(dataSource, model);
    }

    private static async Task<(string Name, long Version)?> ReadAsync(DocumentStore store, ScopeContext scope, string id)
    {
        await using var session = store.OpenSession(scope);
        var loaded = await session.LoadAsync<SavepointGuardDoc>(id);
        return loaded is null ? null : (loaded.Document.Name, loaded.Version);
    }

    private static async Task<long> CountChangesAsync(NpgsqlDataSource dataSource, string id)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM papuma.change WHERE document_id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task RejectedWrite_WithACancelledToken_IsStillRolledBack_AndTheOriginalExceptionSurfaces()
    {
        using var cts = new CancellationTokenSource();
        var store = await StoreAsync(_fixture.DataSource, doc =>
        {
            if (doc.Name == "poison")
            {
                // The request is aborted exactly while the write is being rejected — after the
                // UPDATE ran, before the savepoint rollback.
                cts.Cancel();
                throw new InvalidOperationException("rejected by the validator");
            }
        });
        var scope = NewTenant();
        var id = NewId();
        await using (var seed = store.OpenSession(scope))
        {
            await seed.SaveAsync(new SavepointGuardDoc(id, "original"), 0);
            await seed.CommitAsync();
        }

        await using (var session = store.OpenSession(scope))
        {
            // The caller's own exception, not the OperationCanceledException the rollback raised.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.PatchAsync<SavepointGuardDoc>(id, p => p.Set(x => x.Name, "poison"), ct: cts.Token));
            Assert.Equal("rejected by the validator", ex.Message);

            await session.CommitAsync(CancellationToken.None); // an application that commits regardless
        }

        // Before the fix the UPDATE stayed in the transaction and this commit persisted it —
        // a document at version 2 without a change record.
        Assert.Equal(("original", 1L), await ReadAsync(store, scope, id));
        Assert.Equal(1L, await CountChangesAsync(_fixture.DataSource, id));
    }

    [Fact]
    public async Task FailedSavepointRollback_MakesTheSessionUnusable_ButKeepsTheOriginalException()
    {
        var appName = $"savepoint-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_fixture.Database.OwnerConnectionString)
            {
                ApplicationName = appName,
                Pooling = false,
            }.ConnectionString);

        var store = await StoreAsync(dataSource, doc =>
        {
            if (doc.Name == "poison")
            {
                TerminateBackendsOf(appName); // the connection dies while the write is rejected
                throw new InvalidOperationException("rejected by the validator");
            }
        });
        var scope = NewTenant();
        var id = NewId();
        await using (var seed = store.OpenSession(scope))
        {
            await seed.SaveAsync(new SavepointGuardDoc(id, "original"), 0);
            await seed.CommitAsync();
        }

        var session = store.OpenSession(scope);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PatchAsync<SavepointGuardDoc>(id, p => p.Set(x => x.Name, "poison")));

        Assert.Equal("rejected by the validator", ex.Message);
        Assert.True(ex.Data.Contains("papuma.savepoint_rollback_failure"), "the rollback failure is attached to the original exception");

        var commit = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync());
        Assert.Contains("unusable", commit.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PatchAsync<SavepointGuardDoc>(id, p => p.Set(x => x.Name, "again")));

        await session.DisposeAsync(); // does not throw on the dead connection
    }

    [Fact]
    public async Task DisposeAfterTheConnectionWasLost_DoesNotThrow()
    {
        var appName = $"dispose-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_fixture.Database.OwnerConnectionString)
            {
                ApplicationName = appName,
                Pooling = false,
            }.ConnectionString);
        var store = await StoreAsync(dataSource, _ => { });

        var session = store.OpenSession(NewTenant());
        await session.SaveAsync(new SavepointGuardDoc(NewId(), "x"), 0); // transaction is open
        TerminateBackendsOf(appName);

        await session.DisposeAsync();
        await session.DisposeAsync(); // idempotent
    }

    private void TerminateBackendsOf(string applicationName)
    {
        // Sync on purpose: runs inside a validator. Wait until the backend is really gone, or
        // the following rollback could still reach it and the test would be flaky.
        using var kill = _fixture.DataSource.CreateCommand(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @a");
        kill.Parameters.AddWithValue("a", applicationName);
        kill.ExecuteScalar();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var count = _fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @a");
            count.Parameters.AddWithValue("a", applicationName);
            if ((long)count.ExecuteScalar()! == 0)
            {
                return;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException("The backend did not terminate.");
    }
}
