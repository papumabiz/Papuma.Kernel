// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Integration tests for unit-of-work semantics (architecture §5) and
/// <c>RollbackAsync</c> (ADR-008).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionUnitOfWorkTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public SessionUnitOfWorkTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record UowUser(string Id, string Name, [property: SensitiveData] string? Secret = null);

    private sealed record UowAddress(string Id, string City);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<UowUser>()
            .Document<UowAddress>()
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    // ── Unit of Work ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Registration_TwoAggregates_CommitAtomically_WithSharedCorrelationId()
    {
        var scope = NewTenant();
        var userId = NewId();
        var addressId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new UowUser(userId, "Harry"), 0);
            await session.SaveAsync(new UowAddress(addressId, "Bonn"), 0);
            await session.CommitAsync();
        }

        await using var verify = _store.OpenSession(scope);
        Assert.NotNull(await verify.LoadAsync<UowUser>(userId));
        Assert.NotNull(await verify.LoadAsync<UowAddress>(addressId));

        var correlationIds = await LoadCorrelationIdsAsync(scope);
        Assert.Equal(2, correlationIds.Count);
        Assert.Single(correlationIds.Distinct());
    }

    [Fact]
    public async Task DisposeWithoutCommit_RollsBackEverything()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new UowUser(userId, "Ghost"), 0);
            // no CommitAsync
        }

        await using var verify = _store.OpenSession(scope);
        Assert.Null(await verify.LoadAsync<UowUser>(userId));
    }

    [Fact]
    public async Task FailedWrite_LeavesEarlierWritesIntact_AndSessionUsable()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using (var session = _store.OpenSession(scope))
        {
            await session.SaveAsync(new UowUser(userId, "Harry"), 0);

            // This write fails (insert on existing id) — the savepoint confines the damage.
            await Assert.ThrowsAsync<ConcurrencyException>(
                () => session.SaveAsync(new UowUser(userId, "Again"), 0));

            // Session stays usable; earlier write commits fine.
            await session.SaveAsync(new UowAddress(NewId(), "Bonn"), 0);
            await session.CommitAsync();
        }

        await using var verify = _store.OpenSession(scope);
        Assert.Equal("Harry", (await verify.LoadAsync<UowUser>(userId))!.Document.Name);
    }

    [Fact]
    public async Task SessionMetadata_ActorAndCausation_LandInChangeRecords()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope, new SessionOptions
        {
            ActorId = "admin_7",
            CausationId = "cmd_register",
        });
        await session.SaveAsync(new UowUser(NewId(), "Harry"), 0);
        await session.CommitAsync();

        var metadata = await LoadMetadataAsync(scope);
        Assert.Equal("admin_7", metadata["actorId"]);
        Assert.Equal("cmd_register", metadata["causationId"]);
        Assert.Equal(session.CorrelationId.ToString("N"), metadata["correlationId"]);
    }

    // ── Rollback (ADR-008) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Rollback_RestoresTargetState_AppendOnly_WithMetadata()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await session.SaveAsync(new UowUser(id, "VersionOne"), 0);   // v1
        await session.SaveAsync(new UowUser(id, "VersionTwo"), 1);   // v2
        await session.SaveAsync(new UowUser(id, "VersionThree"), 2); // v3

        var result = await session.RollbackAsync<UowUser>(id, toVersion: 1, expectedVersion: 3);
        await session.CommitAsync();

        // Append-only: rollback adds version 4 with the content of version 1.
        Assert.Equal(4, result.Version);
        Assert.Equal(ChangeOperation.Update, result.Operation);
        var loaded = await session.LoadAsync<UowUser>(id);
        Assert.Equal("VersionOne", loaded!.Document.Name);
        Assert.Equal(4, loaded.Version);

        var metadata = await LoadMetadataAsync(scope, version: 4);
        Assert.Equal("true", metadata["isRollback"]);
        Assert.Equal("1", metadata["restoredVersion"]);
    }

    [Fact]
    public async Task Rollback_AcrossDeleteAndRecreate_ReconstructsThroughTheChain()
    {
        // UowAddress deliberately: insert/delete diffs of a type with a [SensitiveData]
        // field contain redacted entries, which correctly block rollback (see below).
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await session.SaveAsync(new UowAddress(id, "Bonn"), 0);    // v1
        await session.DeleteAsync<UowAddress>(id, 1);              // v2 (delete)
        await session.SaveAsync(new UowAddress(id, "Köln"), 0);    // v3 (insert)

        var result = await session.RollbackAsync<UowAddress>(id, toVersion: 1, expectedVersion: 3);

        Assert.Equal(4, result.Version);
        Assert.Equal("Bonn", (await session.LoadAsync<UowAddress>(id))!.Document.City);
    }

    [Fact]
    public async Task Rollback_ToDeleteVersion_IsRejected()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UowAddress(id, "Bonn"), 0);    // v1
        await session.DeleteAsync<UowAddress>(id, 1);              // v2 (delete)
        await session.SaveAsync(new UowAddress(id, "Köln"), 0);    // v3

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.RollbackAsync<UowAddress>(id, toVersion: 2, expectedVersion: 3));
    }

    [Fact]
    public async Task Rollback_OverRedactedDiff_ThrowsTyped_NeverSilentlyWrong()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UowUser(id, "Harry"), 0);                     // v1
        await session.SaveAsync(new UowUser(id, "Harry", "geheim"), 1);           // v2: [SensitiveData] changed → redacted diff

        var ex = await Assert.ThrowsAsync<RollbackNotPossibleException>(
            () => session.RollbackAsync<UowUser>(id, toVersion: 1, expectedVersion: 2));

        Assert.Equal("secret", ex.Path);
        Assert.Equal(DiffEntryKind.Redacted, ex.Kind);

        // Nothing was written.
        Assert.Equal(2, (await session.LoadAsync<UowUser>(id))!.Version);
    }

    [Fact]
    public async Task Rollback_WithStaleExpectedVersion_ThrowsConcurrencyException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UowUser(id, "One"), 0);
        await session.SaveAsync(new UowUser(id, "Two"), 1);
        await session.SaveAsync(new UowUser(id, "Three"), 2);

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.RollbackAsync<UowUser>(id, toVersion: 1, expectedVersion: 2));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<List<string>> LoadCorrelationIdsAsync(ScopeContext scope)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT metadata ->> 'correlationId'
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<Dictionary<string, string>> LoadMetadataAsync(ScopeContext scope, long? version = null)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, value
            FROM papuma.change, jsonb_each_text(metadata)
            WHERE scope = @scope AND tenant_id = @tenantId
              AND (@version IS NULL OR version = @version)
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter<long?>("version", NpgsqlTypes.NpgsqlDbType.Bigint)
        {
            TypedValue = version,
        });

        var metadata = new Dictionary<string, string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            metadata[reader.GetString(0)] = reader.GetString(1);
        }

        return metadata;
    }
}
