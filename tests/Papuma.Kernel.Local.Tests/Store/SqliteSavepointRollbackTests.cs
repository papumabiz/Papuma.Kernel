// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// A rejected write rolls back to its savepoint (review Q-2, L-6). Unlike PostgreSQL, a
/// SQLite transaction survives an error: if the rollback to the savepoint is skipped, a later
/// commit persists the half-done write. The rollback must therefore not depend on the
/// caller's token.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteSavepointRollbackTests
{
    private readonly SqliteFixture _fixture;

    public SqliteSavepointRollbackTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SavepointGuardDoc(string Id, string Name);

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task RejectedBulkPatch_WithACancelledToken_LeavesNothingForALaterCommit()
    {
        using var cts = new CancellationTokenSource();
        var validated = 0;
        var model = new KernelModelBuilder()
            .Document<SavepointGuardDoc>(d => d.Validate(_ =>
            {
                // The first document passes (and is updated), the second is rejected while the
                // request is being aborted: the savepoint holds one finished UPDATE.
                if (Interlocked.Increment(ref validated) == 2)
                {
                    cts.Cancel();
                    throw new InvalidOperationException("rejected by the validator");
                }
            }))
            .Build();
        var store = await _fixture.CreateStoreAsync(model);

        var scope = NewTenant();
        var ids = new[] { NewId(), NewId() };
        await using (var seed = store.OpenSession(scope))
        {
            foreach (var id in ids)
            {
                await seed.SaveAsync(new SavepointGuardDoc(id, "original"), 0);
            }

            await seed.CommitAsync();
        }

        await using (var session = store.OpenSession(scope))
        {
            // The caller's own exception, not the OperationCanceledException the rollback raised.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.PatchManyAsync<SavepointGuardDoc>(ids, p => p.Set(x => x.Name, "patched"), cts.Token));
            Assert.Equal("rejected by the validator", ex.Message);

            await session.CommitAsync(CancellationToken.None); // an application that commits regardless
        }

        // Before the fix the first UPDATE stayed in the transaction and this commit persisted
        // it: bulk is atomic (ADR-014), and a document changed without its change record
        // diverges every projection for good.
        await using var check = store.OpenSession(scope);
        foreach (var id in ids)
        {
            var loaded = await check.LoadAsync<SavepointGuardDoc>(id);
            Assert.Equal("original", loaded!.Document.Name);
            Assert.Equal(1L, loaded.Version);
        }
    }
}
