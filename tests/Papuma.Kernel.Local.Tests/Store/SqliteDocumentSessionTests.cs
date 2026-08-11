// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// First-pass write-path tests for <see cref="SqliteDocumentSession"/> — Stage 2 of
/// docs/analyses/local-kernel-sqlite-sibling.md's implementation sequencing. Mirrors
/// the shape of <c>Papuma.Kernel.Tests.Store.DocumentSessionTests</c>/<c>PolicyAndKeyTests</c>;
/// full parity test coverage lands in Stage 5.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteDocumentSessionTests : IAsyncLifetime
{
    private readonly SqliteFixture _fixture;
    private SqliteDocumentStore _store = null!;

    public SqliteDocumentSessionTests(SqliteFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<UserDoc>(d => d.UniqueKey(x => x.Email))
            .Event<UserLoggedIn>()
            .Build();
        _store = await _fixture.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record UserDoc(string Id, string Name, string? Email = null, List<string>? Roles = null);

    private sealed record UserLoggedIn(string UserId, string Ip);

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    [Fact]
    public async Task Save_Insert_ReturnsVersion1_AndInsertOnlyDiff()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();

        var result = await session.SaveAsync(new UserDoc(id, "Harry"), expectedVersion: 0);

        Assert.Equal(1, result.Version);
        Assert.Equal(ChangeOperation.Insert, result.Operation);
        Assert.All(result.Diff.Entries.Values, e => Assert.False(e.HasOld));
        Assert.Contains("name", result.Diff.Paths);
    }

    [Fact]
    public async Task Load_ReturnsDocumentAndVersion()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry", NewEmail()), 0);

        var loaded = await session.LoadAsync<UserDoc>(id);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal("Harry", loaded.Document.Name);
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenMissing()
    {
        await using var session = _store.OpenSession(NewTenant());

        Assert.Null(await session.LoadAsync<UserDoc>(NewId()));
    }

    [Fact]
    public async Task LoadByKey_ReturnsDocument_ForDeclaredUniqueKey()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        var email = NewEmail();
        await session.SaveAsync(new UserDoc(id, "Harry", email), 0);

        var loaded = await session.LoadByKeyAsync<UserDoc>(x => x.Email, email);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Document.Id);
    }

    [Fact]
    public async Task Save_Update_ComputesDiffFromReturnedOldState()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);

        var result = await session.SaveAsync(new UserDoc(id, "Harald", NewEmail()), expectedVersion: 1);

        Assert.Equal(2, result.Version);
        Assert.Equal(ChangeOperation.Update, result.Operation);
        Assert.Equal("Harry", (string?)result.Diff.Entries["name"].Old);
        Assert.Equal("Harald", (string?)result.Diff.Entries["name"].New);
        Assert.True(result.Diff.Entries["email"].HasNew);
    }

    [Fact]
    public async Task Save_WithStaleVersion_ThrowsConcurrencyException_WithActualVersion()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);
        await session.SaveAsync(new UserDoc(id, "Harald"), 1);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.SaveAsync(new UserDoc(id, "Stale"), expectedVersion: 1));

        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal("UserDoc", ex.DocumentType);
    }

    [Fact]
    public async Task Save_InsertOnExistingDocument_ThrowsConcurrencyException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.SaveAsync(new UserDoc(id, "Again"), expectedVersion: 0));

        Assert.Equal(0, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Save_UpdateOnMissingDocument_ThrowsDocumentNotFound()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => session.SaveAsync(new UserDoc(NewId(), "Ghost"), expectedVersion: 3));
    }

    [Fact]
    public async Task Delete_RemovesDocument_AndRecordsOldStateDiff()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);

        var result = await session.DeleteAsync<UserDoc>(id, expectedVersion: 1);

        Assert.Equal(2, result.Version);
        Assert.Equal(ChangeOperation.Delete, result.Operation);
        Assert.Equal("Harry", (string?)result.Diff.Entries["name"].Old);
        Assert.Null(await session.LoadAsync<UserDoc>(id));
    }

    [Fact]
    public async Task Delete_WithStaleVersion_ThrowsConcurrencyException()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(
            () => session.DeleteAsync<UserDoc>(id, expectedVersion: 99));

        Assert.Equal(99, ex.ExpectedVersion);
        Assert.Equal(1, ex.ActualVersion);
    }

    [Fact]
    public async Task Delete_ThenReinsert_ContinuesVersionFromChangeHistory()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        await session.SaveAsync(new UserDoc(id, "Harry"), 0);
        await session.DeleteAsync<UserDoc>(id, 1);

        // Version numbering resumes from max(change.version)+1, never collides with the
        // gapless per-document change history (same rule as the Postgres kernel).
        var result = await session.SaveAsync(new UserDoc(id, "Harry Again"), expectedVersion: 0);

        Assert.Equal(3, result.Version);
    }

    [Fact]
    public async Task UniqueKey_Violation_ThrowsTypedException_WithKeyPath()
    {
        await using var session = _store.OpenSession(NewTenant());
        var email = NewEmail();
        await session.SaveAsync(new UserDoc(NewId(), "First", email), 0);

        var ex = await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new UserDoc(NewId(), "Second", email), 0));

        Assert.Equal("UserDoc", ex.DocumentType);
        Assert.Equal("email", ex.KeyPath);
    }

    [Fact]
    public async Task UniqueKey_IsScopedPerTenant()
    {
        var email = NewEmail();
        await using (var session1 = _store.OpenSession(NewTenant()))
        {
            await session1.SaveAsync(new UserDoc(NewId(), "First", email), 0);
            await session1.CommitAsync();
        }

        // Same email, different tenant — no violation, isolation via scope/tenant_id
        // WHERE predicates (no RLS in the SQLite kernel, per design).
        await using var session2 = _store.OpenSession(NewTenant());
        var result = await session2.SaveAsync(new UserDoc(NewId(), "Second", email), 0);

        Assert.Equal(1, result.Version);
    }

    [Fact]
    public async Task AppendAsync_ReturnsSequenceNumber()
    {
        await using var session = _store.OpenSession(NewTenant());

        var seq1 = await session.AppendAsync(new UserLoggedIn("u1", "127.0.0.1"));
        var seq2 = await session.AppendAsync(new UserLoggedIn("u1", "127.0.0.1"));

        Assert.True(seq2 > seq1);
    }

    [Fact]
    public async Task CommitAsync_PersistsAcrossSessions()
    {
        var tenant = NewTenant();
        var id = NewId();

        await using (var session = _store.OpenSession(tenant))
        {
            await session.SaveAsync(new UserDoc(id, "Harry"), 0);
            await session.CommitAsync();
        }

        await using var readSession = _store.OpenSession(tenant);
        var loaded = await readSession.LoadAsync<UserDoc>(id);

        Assert.NotNull(loaded);
        Assert.Equal("Harry", loaded.Document.Name);
    }

    [Fact]
    public async Task DisposeAsync_WithoutCommit_RollsBackWrites()
    {
        var tenant = NewTenant();
        var id = NewId();

        await using (var session = _store.OpenSession(tenant))
        {
            await session.SaveAsync(new UserDoc(id, "Harry"), 0);
            // no CommitAsync
        }

        await using var readSession = _store.OpenSession(tenant);
        Assert.Null(await readSession.LoadAsync<UserDoc>(id));
    }
}
