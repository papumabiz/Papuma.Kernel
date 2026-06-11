// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// Integration tests for policy application in the write path (ADR-007) and declared
/// keys as partial expression indexes (ADR-006).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PolicyAndKeyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public PolicyAndKeyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PolicyDoc(
        string Id,
        string Name,
        [property: SensitiveData] string Email,
        [property: TrackHash] string PasswordHash,
        [property: DoNotTrack] string? LastSeen = null,
        string? Phone = null);

    private sealed record PlainDoc(string Id, string Name);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<PolicyDoc>(d => d
                .Property(x => x.Phone).StoreAsReference()
                .UniqueKey(x => x.Email)
                .LookupKey(x => x.Name))
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_AppliesEveryPolicy_BeforeTheDiffReachesTheFeed()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();

        var result = await session.SaveAsync(
            new PolicyDoc(id, "Harry", NewEmail(), "pbkdf2:abc", LastSeen: "2026-06-11", Phone: "+49 228 123"),
            expectedVersion: 0);

        // [SensitiveData] → Redacted: no values
        Assert.Equal(DiffEntryKind.Redacted, result.Diff.Entries["email"].Kind);
        Assert.Null(result.Diff.Entries["email"].New);

        // [TrackHash] → Hashed with SHA-256 hex
        var password = result.Diff.Entries["passwordHash"];
        Assert.Equal(DiffEntryKind.Hashed, password.Kind);
        Assert.Equal(64, password.Hash!.Length);

        // Fluent StoreAsReference → Reference "Type/id/path"
        Assert.Equal(DiffEntryKind.Reference, result.Diff.Entries["phone"].Kind);
        Assert.Equal($"PolicyDoc/{id}/phone", result.Diff.Entries["phone"].Reference);

        // [DoNotTrack] → never in the diff
        Assert.DoesNotContain("lastSeen", result.Diff.Paths);

        // Untouched fields stay tracked
        Assert.Equal(DiffEntryKind.Tracked, result.Diff.Entries["name"].Kind);
        Assert.Equal("Harry", (string?)result.Diff.Entries["name"].New);
    }

    [Fact]
    public async Task PolicyDiff_RoundTripsThroughTheStoredChangeRecord()
    {
        var scope = NewTenant();
        await using var session = _store.OpenSession(scope);
        var id = NewId();
        await session.SaveAsync(new PolicyDoc(id, "Harry", NewEmail(), "h1"), 0);
        await session.CommitAsync();

        var storedDiff = DocumentDiff.FromJson(await LoadStoredDiffAsync(scope, id, version: 1));

        Assert.Equal(DiffEntryKind.Redacted, storedDiff.Entries["email"].Kind);
        Assert.Equal(DiffEntryKind.Hashed, storedDiff.Entries["passwordHash"].Kind);
        Assert.Equal(DiffEntryKind.Reference, storedDiff.Entries["phone"].Kind);
        Assert.Equal(DiffEntryKind.Tracked, storedDiff.Entries["name"].Kind);
    }

    [Fact]
    public async Task Delete_RedactsSensitiveFields_EvenInTheLastDiff()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        var email = NewEmail();
        await session.SaveAsync(new PolicyDoc(id, "Gone", email, "h1"), 0);

        var result = await session.DeleteAsync<PolicyDoc>(id, expectedVersion: 1);

        // The last known value of a sensitive field must never appear in clear text.
        Assert.Equal(DiffEntryKind.Redacted, result.Diff.Entries["email"].Kind);
        Assert.Null(result.Diff.Entries["email"].Old);
        Assert.Equal("Gone", (string?)result.Diff.Entries["name"].Old);
    }

    [Fact]
    public async Task UniqueKey_Violation_ThrowsTypedException_WithKeyPath()
    {
        await using var session = _store.OpenSession(NewTenant());
        var email = NewEmail();
        await session.SaveAsync(new PolicyDoc(NewId(), "First", email, "h1"), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new PolicyDoc(NewId(), "Second", email, "h2"), 0));

        Assert.Equal("PolicyDoc", ex.DocumentType);
        Assert.Equal("email", ex.KeyPath);
    }

    [Fact]
    public async Task UniqueKey_IsScopedPerTenant()
    {
        var email = NewEmail();
        await using (var sessionA = _store.OpenSession(NewTenant()))
        {
            await sessionA.SaveAsync(new PolicyDoc(NewId(), "A", email, "h1"), 0);
            await sessionA.CommitAsync();
        }

        // Same email in another tenant must not conflict.
        await using var sessionB = _store.OpenSession(NewTenant());
        await sessionB.SaveAsync(new PolicyDoc(NewId(), "B", email, "h2"), 0);
    }

    [Fact]
    public async Task LoadByKey_FindsDocument_ByDeclaredUniqueKey()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        var email = NewEmail();
        await session.SaveAsync(new PolicyDoc(id, "Harry", email, "h1"), 0);

        var loaded = await session.LoadByKeyAsync<PolicyDoc>(x => x.Email, email);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Document.Id);
    }

    [Fact]
    public async Task LoadByKey_ReturnsNull_WhenNoMatch()
    {
        await using var session = _store.OpenSession(NewTenant());

        Assert.Null(await session.LoadByKeyAsync<PolicyDoc>(x => x.Email, NewEmail()));
    }

    [Fact]
    public async Task LoadByKey_RejectsUndeclaredKeys()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<ArgumentException>(
            () => session.LoadByKeyAsync<PolicyDoc>(x => x.PasswordHash, "x"));
    }

    [Fact]
    public async Task UnregisteredDocumentType_FailsLoudly()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<DocumentTypeNotRegisteredException>(
            () => session.SaveAsync(new PlainDoc(NewId(), "x"), 0));
    }

    private async Task<JsonObject> LoadStoredDiffAsync(ScopeContext scope, string documentId, long version)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT diff::text
            FROM papuma.change
            WHERE scope = @scope AND tenant_id = @tenantId AND document_id = @id AND version = @version
            """;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("id", documentId);
        cmd.Parameters.AddWithValue("version", version);

        var text = (string)(await cmd.ExecuteScalarAsync())!;
        return (JsonObject)JsonNode.Parse(text)!;
    }
}
