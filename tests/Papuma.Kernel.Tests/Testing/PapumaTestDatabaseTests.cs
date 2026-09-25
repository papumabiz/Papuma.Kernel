// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Testing;

/// <summary>
/// The testing package (ADR-021) against a real PostgreSQL 18: the whole kernel runs as
/// the non-superuser application role, RLS applies to it, and draining surfaces handler
/// failures instead of passing over them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PapumaTestDatabaseTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public PapumaTestDatabaseTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record TestingCounter(string Id, string Name, int Value = 0);

    private sealed record TestingPoison(string Id);

    private sealed record TestingEcho(string Id, int Generation);

    private sealed record TestingPing(string Id);

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<TestingCounter>(d => d.UniqueKey(x => x.Name))
            .Document<TestingPoison>()
            .Document<TestingEcho>()
            .Event<TestingPing>()
            .Build();
        _store = await _fixture.Database.CreateStoreAsync(model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ScopeContext NewTenant() => ScopeContext.Tenant(Guid.NewGuid());

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AppRole_IsNeitherSuperuserNorBypassingRls()
    {
        await using var conn = await _fixture.Database.AppDataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task Store_RunsTheWholeWritePath_AsTheAppRole()
    {
        await using var session = _store.OpenSession(NewTenant());
        var id = NewId();
        var name = NewId();

        await session.SaveAsync(new TestingCounter(id, name), 0);
        var patched = await session.PatchAsync<TestingCounter>(id, p => p.Increment(x => x.Value));
        await Assert.ThrowsAsync<UniqueKeyViolationException>(
            () => session.SaveAsync(new TestingCounter(NewId(), name), 0));
        await session.CommitAsync();

        Assert.Equal(1, patched.GetDocument<TestingCounter>().Value);
    }

    [Fact]
    public async Task AppRole_SeesOnlyTheScopedTenant_WithoutAnyWherePredicate()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var idA = await SaveAsync(tenantA, new TestingCounter(NewId(), NewId()));
        var idB = await SaveAsync(tenantB, new TestingCounter(NewId(), NewId()));

        await using var conn = await _fixture.Database.AppDataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.SetScopeAsync(tx, tenantA);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM papuma.document WHERE id = ANY(@ids)";
        cmd.Parameters.AddWithValue("ids", new[] { idA, idB });
        var visible = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visible.Add(reader.GetString(0));
            }
        }

        Assert.Equal([idA], visible);
    }

    [Fact]
    public async Task DrainAsync_DeliversEverything_AsTheAppRole()
    {
        var tenant = NewTenant();
        var id = await SaveAsync(tenant, new TestingCounter(NewId(), NewId()));
        var seen = new List<string>();
        var handler = new DelegateChangeHandler(c =>
        {
            if (c.DocumentType == nameof(TestingCounter) && c.Scope == tenant)
            {
                seen.Add(c.DocumentId);
            }

            return Task.CompletedTask;
        });
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [handler]);

        var delivered = await processor.DrainAsync();

        Assert.True(delivered >= 1);
        Assert.Equal([id], seen);
        Assert.Equal(0, await processor.DrainAsync()); // settled: nothing left
    }

    [Fact]
    public async Task DrainAsync_SurfacesAHandlerFailure()
    {
        var id = await SaveAsync(NewTenant(), new TestingPoison(NewId()));
        var handler = new DelegateChangeHandler(c => c.DocumentType == nameof(TestingPoison) && c.DocumentId == id
            ? throw new InvalidOperationException("projection broke")
            : Task.CompletedTask);
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [handler]);

        var ex = await Assert.ThrowsAsync<FeedDrainException>(() => processor.DrainAsync());

        var failure = Assert.Single(ex.Failures);
        Assert.Equal(handler.Name, failure.HandlerName);
        Assert.Contains("projection broke", failure.LastError);
    }

    [Fact]
    public async Task DrainAsync_StopsAtTheCycleLimit_WhenAHandlerKeepsProducing()
    {
        var tenant = NewTenant();
        var armed = false;
        var handler = new DelegateChangeHandler(async c =>
        {
            if (armed && c.DocumentType == nameof(TestingEcho) && c.Scope == tenant)
            {
                await SaveAsync(tenant, new TestingEcho(NewId(), 1)); // every change begets another
            }
        });
        using var processor = new ChangeFeedProcessor(_fixture.Database.AppDataSource, [handler]);
        await processor.DrainAsync(); // the backlog of other tests settles first

        armed = true;
        await SaveAsync(tenant, new TestingEcho(NewId(), 0));
        var ex = await Assert.ThrowsAsync<FeedDrainException>(() => processor.DrainAsync(maxCycles: 20));

        Assert.Empty(ex.Failures);
        Assert.Contains("did not settle within 20 cycles", ex.Message);
    }

    [Fact]
    public async Task DrainAsync_WorksForTheEventFeed()
    {
        var tenant = NewTenant();
        await using (var session = _store.OpenSession(tenant))
        {
            await session.AppendAsync(new TestingPing(NewId()));
            await session.CommitAsync();
        }

        var seen = 0;
        var handler = new DelegateEventHandler(e =>
        {
            if (e.EventType == nameof(TestingPing) && e.Scope == tenant)
            {
                seen++;
            }

            return Task.CompletedTask;
        });
        using var processor = new EventFeedProcessor(_fixture.Database.AppDataSource, [handler]);

        await processor.DrainAsync();

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task ConnectAsync_UsesAnExistingServer_AndReusesTheRole()
    {
        const string role = "papuma_test_connect";
        await using (var first = await PapumaTestDatabase.ConnectAsync(_fixture.Database.OwnerConnectionString, role))
        {
            Assert.Equal(role, first.AppRoleName);
        }

        // Second run: the role exists, gets a new password, and still logs in.
        await using var second = await PapumaTestDatabase.ConnectAsync(_fixture.Database.OwnerConnectionString, role);
        await using var conn = await second.AppDataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_user";

        Assert.Equal(role, (string?)await cmd.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("x; DROP ROLE postgres")]
    public async Task ConnectAsync_RejectsInvalidRoleNames(string role)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => PapumaTestDatabase.ConnectAsync(_fixture.Database.OwnerConnectionString, role));
    }

    private async Task<string> SaveAsync<T>(ScopeContext scope, T document) where T : class
    {
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(document, 0);
        await session.CommitAsync();
        return (string)typeof(T).GetProperty("Id")!.GetValue(document)!;
    }

    private sealed class DelegateChangeHandler(Func<ChangeRecord, Task> handle) : IChangeHandler
    {
        public string Name { get; } = $"testing-{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => handle(change);
    }

    private sealed class DelegateEventHandler(Func<EventRecord, Task> handle) : IEventHandler
    {
        public string Name { get; } = $"testing-{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => handle(@event);
    }
}
