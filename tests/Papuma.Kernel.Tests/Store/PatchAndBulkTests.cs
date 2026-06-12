// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Integration tests for the patch primitive (ADR-012) and bulk operations (ADR-014).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PatchAndBulkTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public PatchAndBulkTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PatchDoc(
        string Id,
        string Name,
        string Status,
        int LoginCount = 0,
        [property: TrackHash] string? PasswordHash = null,
        string? Nickname = null);

    private sealed record ValidatedDoc(string Id, int Quantity);

    private sealed record EvoPatchDoc(string Id, string Email);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<PatchDoc>(d => d.LookupKey(x => x.Status))
            .Document<ValidatedDoc>(d => d.Validate(doc =>
            {
                if (doc.Quantity < 0)
                {
                    throw new InvalidOperationException("Quantity must not be negative.");
                }
            }))
            .Document<EvoPatchDoc>(d => d.Upcast(1, json =>
            {
                json["email"] = json["mail"]?.DeepClone();
                json.Remove("mail");
            }))
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    // ── Patch (ADR-012) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_SetRemoveIncrement_WithoutLoadingTheDocument()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new PatchDoc(id, "Harry", "active", LoginCount: 5, Nickname: "H"), 0);

        var result = await session.PatchAsync<PatchDoc>(id, p => p
            .Set(x => x.Name, "Harald")
            .Remove(x => x.Nickname)
            .Increment(x => x.LoginCount));

        Assert.Equal(2, result.Version);
        Assert.Equal(ChangeOperation.Update, result.Operation);
        Assert.Equal("Harry", (string?)result.Diff.Entries["name"].Old);
        Assert.Equal("Harald", (string?)result.Diff.Entries["name"].New);
        Assert.False(result.Diff.Entries["nickname"].HasNew);

        var loaded = await session.LoadAsync<PatchDoc>(id);
        Assert.Equal("Harald", loaded!.Document.Name);
        Assert.Equal(6, loaded.Document.LoginCount);
        Assert.Null(loaded.Document.Nickname);
    }

    [Fact]
    public async Task Patch_OnDifferentFields_DoesNotConflict_VersionsStayLinear()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new PatchDoc(id, "Harry", "active"), 0);

        // Two writers patch different fields without expectedVersion — both succeed.
        var first = await session.PatchAsync<PatchDoc>(id, p => p.Set(x => x.Name, "Harald"));
        var second = await session.PatchAsync<PatchDoc>(id, p => p.Set(x => x.Status, "inactive"));

        Assert.Equal(2, first.Version);
        Assert.Equal(3, second.Version);
        var loaded = await session.LoadAsync<PatchDoc>(id);
        Assert.Equal("Harald", loaded!.Document.Name);
        Assert.Equal("inactive", loaded.Document.Status);
    }

    [Fact]
    public async Task Patch_WithStaleExpectedVersion_ThrowsConcurrencyException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new PatchDoc(id, "Harry", "active"), 0);
        await session.PatchAsync<PatchDoc>(id, p => p.Set(x => x.Name, "Harald"));

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.PatchAsync<PatchDoc>(id, p => p.Set(x => x.Name, "Stale"), expectedVersion: 1));

        Assert.Equal(2, ex.ActualVersion);
    }

    [Fact]
    public async Task Patch_OnMissingDocument_ThrowsDocumentNotFound()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => session.PatchAsync<PatchDoc>(NewId(), p => p.Set(x => x.Name, "Ghost")));
    }

    [Fact]
    public async Task Patch_RunsPolicies_HashedFieldNeverInClearText()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new PatchDoc(id, "Harry", "active"), 0);

        var result = await session.PatchAsync<PatchDoc>(id, p => p.Set(x => x.PasswordHash, "pbkdf2:new"));

        var entry = result.Diff.Entries["passwordHash"];
        Assert.Equal(DiffEntryKind.Hashed, entry.Kind);
        Assert.Null(entry.New);
        Assert.Equal(64, entry.Hash!.Length);
    }

    [Fact]
    public async Task BoundedCounter_ConcurrentBuyers_NeverOversell()
    {
        // The inventory pattern (concepts §17): Increment + validator = atomic
        // conditional decrement. 12 parallel buyers, 5 in stock — exactly 5 succeed,
        // 7 are rejected typed, stock ends at 0, never negative.
        var scope = NewTenant();
        var skuId = NewId();
        await using (var setup = _store.OpenSession(scope))
        {
            await setup.SaveAsync(new ValidatedDoc(skuId, Quantity: 5), 0);
            await setup.CommitAsync();
        }

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var buyer = _store.OpenSession(scope);
            try
            {
                await buyer.PatchAsync<ValidatedDoc>(skuId, p => p.Increment(x => x.Quantity, -1));
                await buyer.CommitAsync();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // out of stock — validator rejected, nothing written
            }
        }));

        Assert.Equal(5, outcomes.Count(success => success));

        await using var verify = _store.OpenSession(scope);
        Assert.Equal(0, (await verify.LoadAsync<ValidatedDoc>(skuId))!.Document.Quantity);
    }

    [Fact]
    public async Task Patch_ValidatorRejection_RollsBackTheWholeWrite()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new ValidatedDoc(id, Quantity: 3), 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PatchAsync<ValidatedDoc>(id, p => p.Increment(x => x.Quantity, by: -10)));

        var loaded = await session.LoadAsync<ValidatedDoc>(id);
        Assert.Equal(3, loaded!.Document.Quantity);
        Assert.Equal(1, loaded.Version); // no version bump, no change record
    }

    [Fact]
    public async Task Patch_OnOutdatedSchema_ThrowsUpcastRequired_AndRollsBack()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await SeedRawV1DocumentAsync(scope, id);

        await Assert.ThrowsAsync<SchemaUpcastRequiredException>(
            () => session.PatchAsync<EvoPatchDoc>(id, p => p.Set(x => x.Email, "new@x.de")));

        // Load + Save lifts the schema, then the patch works.
        var loaded = await session.LoadAsync<EvoPatchDoc>(id);
        await session.SaveAsync(loaded!.Document, loaded.Version);
        await session.PatchAsync<EvoPatchDoc>(id, p => p.Set(x => x.Email, "new@x.de"));
    }

    // ── Bulk (ADR-014) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PatchMany_PatchesAllIds_OneChangeRecordEach_SharedCorrelationId()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var ids = new[] { NewId(), NewId(), NewId() };
        foreach (var id in ids)
        {
            await session.SaveAsync(new PatchDoc(id, $"user_{id[..6]}", "active"), 0);
        }

        var result = await session.PatchManyAsync<PatchDoc>(ids, p => p.Set(x => x.Status, "archived"));

        Assert.Equal(3, result.Count);
        foreach (var id in ids)
        {
            var loaded = await session.LoadAsync<PatchDoc>(id);
            Assert.Equal("archived", loaded!.Document.Status);
            Assert.Equal(2, loaded.Version);
        }

        await session.CommitAsync();
        var correlationIds = await LoadCorrelationIdsAsync(scope, version: 2);
        Assert.Equal(3, correlationIds.Count);
        Assert.Single(correlationIds.Distinct());
        Assert.Equal(result.CorrelationId.ToString("N"), correlationIds[0]);
    }

    [Fact]
    public async Task PatchWhere_PatchesOnlyMatchingDocuments()
    {
        await using var session = _store.OpenSession(NewTenant());
        var inactive1 = NewId();
        var inactive2 = NewId();
        var active = NewId();
        await session.SaveAsync(new PatchDoc(inactive1, "A", "inactive"), 0);
        await session.SaveAsync(new PatchDoc(inactive2, "B", "inactive"), 0);
        await session.SaveAsync(new PatchDoc(active, "C", "active"), 0);

        var result = await session.PatchWhereAsync<PatchDoc>(
            x => x.Status, "inactive",
            p => p.Set(x => x.Status, "archived"));

        Assert.Equal(2, result.Count);
        Assert.Equal("archived", (await session.LoadAsync<PatchDoc>(inactive1))!.Document.Status);
        Assert.Equal("active", (await session.LoadAsync<PatchDoc>(active))!.Document.Status);
    }

    [Fact]
    public async Task PatchWhere_RejectsUndeclaredKeys()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.PatchWhereAsync<PatchDoc>(x => x.Name, "x", p => p.Set(x => x.Status, "y")));
    }

    [Fact]
    public async Task Bulk_ConflictsCorrectly_WithParallelOptimisticWriters()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new PatchDoc(id, "Harry", "active"), 0);  // version 1

        await session.PatchManyAsync<PatchDoc>([id], p => p.Set(x => x.Status, "archived")); // → version 2

        // A writer still holding version 1 must lose.
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.SaveAsync(new PatchDoc(id, "Stale", "active"), expectedVersion: 1));
        Assert.Equal(2, ex.ActualVersion);
    }

    [Fact]
    public async Task Bulk_IsAtomic_OneBadDocumentRollsBackEverything()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var goodId = NewId();
        var oldSchemaId = NewId();
        await session.SaveAsync(new EvoPatchDoc(goodId, "good@x.de"), 0);
        await SeedRawV1DocumentAsync(scope, oldSchemaId); // schema v1 → patch must reject

        await Assert.ThrowsAsync<SchemaUpcastRequiredException>(
            () => session.PatchManyAsync<EvoPatchDoc>(
                [goodId, oldSchemaId], p => p.Set(x => x.Email, "bulk@x.de")));

        // The good document must be untouched as well — all or nothing (ADR-014).
        var good = await session.LoadAsync<EvoPatchDoc>(goodId);
        Assert.Equal("good@x.de", good!.Document.Email);
        Assert.Equal(1, good.Version);
    }

    [Fact]
    public async Task DeleteWhere_DeletesMatches_WithDeleteChangeRecords()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id1 = NewId();
        var id2 = NewId();
        await session.SaveAsync(new PatchDoc(id1, "A", "obsolete"), 0);
        await session.SaveAsync(new PatchDoc(id2, "B", "obsolete"), 0);

        var result = await session.DeleteWhereAsync<PatchDoc>(x => x.Status, "obsolete");

        Assert.Equal(2, result.Count);
        Assert.Null(await session.LoadAsync<PatchDoc>(id1));
        Assert.Null(await session.LoadAsync<PatchDoc>(id2));

        await session.CommitAsync();
        var operations = await LoadOperationsAsync(scope, version: 2);
        Assert.Equal([3, 3], operations); // two delete records
    }

    [Fact]
    public async Task DeleteMany_WithEmptyIdList_AffectsNothing()
    {
        await using var session = _store.OpenSession(NewTenant());

        var result = await session.DeleteManyAsync<PatchDoc>([]);

        Assert.Equal(0, result.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task SeedRawV1DocumentAsync(ScopeContext scope, string id)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO papuma.document (scope, tenant_id, document_type, id, version, schema_version, data)
            VALUES (@scope, @tenantId, 'EvoPatchDoc', @id, 1, 1,
                    jsonb_build_object('id', @id::text, 'mail', 'old@x.de'))
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> LoadCorrelationIdsAsync(ScopeContext scope, long version)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT metadata ->> 'correlationId'
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId AND version = @version
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("version", version);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private async Task<List<short>> LoadOperationsAsync(ScopeContext scope, long version)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT operation
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId AND version = @version
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("version", version);

        var operations = new List<short>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            operations.Add(reader.GetInt16(0));
        }

        return operations;
    }
}
