// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

[Collection(PostgresCollection.Name)]
public sealed class DocumentSessionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public DocumentSessionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource);
        _store = new DocumentStore(_fixture.DataSource);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record UserDoc(string Name, string? Email = null, List<string>? Roles = null);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_Insert_ReturnsVersion1_AndInsertOnlyDiff()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();

        var result = await session.SaveAsync(id, new UserDoc("Harry"), expectedVersion: 0);

        Assert.Equal(1, result.Version);
        Assert.Equal(ChangeOperation.Insert, result.Operation);
        Assert.All(result.Diff.Entries.Values, e => Assert.False(e.HasOld));
        Assert.Contains("name", result.Diff.Paths);
    }

    [Fact]
    public async Task Load_ReturnsDocumentAndVersion()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry", "h@x.de"), 0);

        var loaded = await session.LoadAsync<UserDoc>(id);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Harry", loaded.Document.Name);
        Assert.Equal("h@x.de", loaded.Document.Email);
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenMissing()
    {
        var session = _store.OpenSession(NewTenant());

        Assert.Null(await session.LoadAsync<UserDoc>(NewId()));
    }

    [Fact]
    public async Task Save_Update_ComputesDiffFromReturnedOldState()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);

        var result = await session.SaveAsync(id, new UserDoc("Harald", "h@x.de"), expectedVersion: 1);

        Assert.Equal(2, result.Version);
        Assert.Equal(ChangeOperation.Update, result.Operation);
        Assert.Equal("Harry", (string?)result.Diff.Entries["name"].Old);
        Assert.Equal("Harald", (string?)result.Diff.Entries["name"].New);
        Assert.True(result.Diff.Entries["email"].HasNew);
    }

    [Fact]
    public async Task Save_WithStaleVersion_ThrowsConcurrencyException_WithActualVersion()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);
        await session.SaveAsync(id, new UserDoc("Harald"), 1);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.SaveAsync(id, new UserDoc("Stale"), expectedVersion: 1));

        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal("UserDoc", ex.DocumentType);
    }

    [Fact]
    public async Task Save_InsertOnExistingDocument_ThrowsConcurrencyException()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.SaveAsync(id, new UserDoc("Again"), expectedVersion: 0));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Save_UpdateOnMissingDocument_ThrowsDocumentNotFound()
    {
        var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => session.SaveAsync(NewId(), new UserDoc("Ghost"), expectedVersion: 3));
    }

    [Fact]
    public async Task Delete_RemovesDocument_AndRecordsDeleteOnlyDiff()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Gone", "g@x.de"), 0);

        var result = await session.DeleteAsync<UserDoc>(id, expectedVersion: 1);

        Assert.Equal(2, result.Version);
        Assert.Equal(ChangeOperation.Delete, result.Operation);
        Assert.All(result.Diff.Entries.Values, e => Assert.False(e.HasNew));
        Assert.Equal("Gone", (string?)result.Diff.Entries["name"].Old);
        Assert.Null(await session.LoadAsync<UserDoc>(id));
    }

    [Fact]
    public async Task Delete_WithStaleVersion_ThrowsConcurrencyException()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.DeleteAsync<UserDoc>(id, expectedVersion: 9));

        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Insert_AfterDelete_ContinuesVersionNumbering()
    {
        var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("First"), 0);      // version 1
        await session.DeleteAsync<UserDoc>(id, 1);                 // version 2 (delete)

        var result = await session.SaveAsync(id, new UserDoc("Reborn"), expectedVersion: 0);

        Assert.Equal(3, result.Version);
        Assert.Equal(ChangeOperation.Insert, result.Operation);

        var loaded = await session.LoadAsync<UserDoc>(id);
        Assert.Equal(3, loaded!.Version);
    }

    [Fact]
    public async Task ChangeFeed_RecordsEveryWrite_GaplessPerDocument()
    {
        var scope = NewTenant();
        var session = _store.OpenSession(scope);
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);
        await session.SaveAsync(id, new UserDoc("Harald"), 1);
        await session.DeleteAsync<UserDoc>(id, 2);

        var records = await LoadChangeRecordsAsync(scope, id);

        Assert.Equal(3, records.Count);
        Assert.Equal([(1L, 1), (2L, 2), (3L, 3)], records.Select(r => (r.Version, r.Operation)).ToArray());
    }

    [Fact]
    public async Task DocumentAndChangeRecord_AreAtomic_FailedChangeInsertRollsBackDocument()
    {
        var scope = NewTenant();
        var session = _store.OpenSession(scope);
        var id = NewId();
        await session.SaveAsync(id, new UserDoc("Harry"), 0);

        // Sabotage: pre-seed the change row the next update would write (version 2) so the
        // change insert violates the unique index — the document update must roll back too.
        await SeedChangeRowAsync(scope, id, version: 2);

        await Assert.ThrowsAsync<PostgresException>(
            () => session.SaveAsync(id, new UserDoc("MustRollBack"), expectedVersion: 1));

        var loaded = await session.LoadAsync<UserDoc>(id);
        Assert.Equal(1, loaded!.Version);
        Assert.Equal("Harry", loaded.Document.Name);
    }

    [Fact]
    public async Task Session_IsTenantIsolated_ViaExplicitPredicates()
    {
        var id = NewId();
        var sessionA = _store.OpenSession(NewTenant());
        var sessionB = _store.OpenSession(NewTenant());
        await sessionA.SaveAsync(id, new UserDoc("OnlyA"), 0);

        Assert.Null(await sessionB.LoadAsync<UserDoc>(id));
        await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => sessionB.SaveAsync(id, new UserDoc("Hijack"), expectedVersion: 1));
    }

    private async Task<IReadOnlyList<(long Version, short Operation)>> LoadChangeRecordsAsync(
        ScopeContext scope, string documentId)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version, operation
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId AND document_id = @id
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", documentId);

        var records = new List<(long, short)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add((reader.GetInt64(0), reader.GetInt16(1)));
        }

        return records;
    }

    private async Task SeedChangeRowAsync(ScopeContext scope, string documentId, long version)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO papuma.change
                (scope, tenant_id, document_type, document_id, version, schema_version, operation, diff, metadata)
            VALUES
                (@scope, @tenantId, 'UserDoc', @id, @version, 1, 2, @diff, '{}'::jsonb)
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.Add(new NpgsqlParameter("diff", NpgsqlDbType.Jsonb) { Value = "{}" });
        await cmd.ExecuteNonQueryAsync();
    }
}
