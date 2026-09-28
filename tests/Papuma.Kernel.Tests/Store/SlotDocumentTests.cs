// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// "At most one per …" through a slot document (concepts §34): the pattern as the docs
/// show it, including why the slot is written first.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SlotDocumentTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public SlotDocumentTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record WorkspaceOwner(string Id, string UserId);

    private sealed record SlotMembership(string Id, string WorkspaceId, string UserId, string Role);

    public async Task InitializeAsync()
    {
        _store = await _fixture.Database.CreateStoreAsync(
            new KernelModelBuilder().Document<WorkspaceOwner>().Document<SlotMembership>().Build());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MakeOwnerAsync(ScopeContext scope, string workspaceId, string userId)
    {
        await using var session = _store.OpenSession(scope);
        try
        {
            await session.SaveAsync(new WorkspaceOwner(workspaceId, userId), 0); // the slot first
            await session.SaveAsync(new SlotMembership(Guid.NewGuid().ToString("N"), workspaceId, userId, "owner"), 0);
            await session.CommitAsync();
        }
        catch (ConcurrencyException)
        {
            await session.DiscardAsync();
            throw;
        }
    }

    [Fact]
    public async Task SecondOwner_IsRejected_AndLeavesNothingBehind()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var workspaceId = Guid.NewGuid().ToString("N");

        await MakeOwnerAsync(scope, workspaceId, "alice");
        await Assert.ThrowsAsync<ConcurrencyException>(() => MakeOwnerAsync(scope, workspaceId, "bob"));

        await using var read = _store.OpenSession(scope);
        Assert.Equal("alice", (await read.LoadAsync<WorkspaceOwner>(workspaceId))!.Document.UserId);

        // Nothing of bob's attempt: the slot was the gate before his membership existed.
        await using var cmd = _fixture.DataSource.CreateCommand("""
            SELECT count(*) FROM papuma.document
            WHERE tenant_id = @tenant AND document_type = 'SlotMembership' AND data ->> 'userId' = 'bob'
            """);
        cmd.Parameters.AddWithValue("tenant", scope.TenantId!);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConcurrentOwners_ExactlyOneWins()
    {
        var scope = ScopeContext.Tenant(Guid.NewGuid());
        var workspaceId = Guid.NewGuid().ToString("N");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                await MakeOwnerAsync(scope, workspaceId, $"user-{i}");
                return true;
            }
            catch (ConcurrencyException)
            {
                return false;
            }
        }));

        Assert.Single(attempts, won => won);
    }
}
