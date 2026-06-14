// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Events;

/// <summary>
/// Integration tests for the event log (ADR-013): atomic appends with shared
/// correlation, payload policies, consumption via the event engine, retention.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EventLogTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;
    private KernelModel _model = null!;

    public EventLogTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record LoginUser(string Id, string Name, string? LastLoginAt = null);

    private sealed record UserLoggedIn(
        string UserId,
        string Device,
        [property: SensitiveData] string? Ip = null,
        [property: TrackHash] string? SessionToken = null);

    private sealed record ShortLived(string Note);

    private sealed record KeptForever(string Note);

    public async Task InitializeAsync()
    {
        _model = new KernelModelBuilder()
            .Document<LoginUser>()
            .Event<UserLoggedIn>()
            .Event<ShortLived>(e => e.Retention(TimeSpan.Zero)) // purgeable immediately (test)
            .Event<KeptForever>()
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, _model);
        _store = new DocumentStore(_fixture.DataSource, _model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private sealed class RecordingEventHandler(string name) : IEventHandler
    {
        public ConcurrentQueue<EventRecord> Received { get; } = new();

        public string Name => name;

        public Task HandleAsync(EventRecord @event, CancellationToken ct)
        {
            Received.Enqueue(@event);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task LoginScenario_FactAndStateChange_CommitAtomically_WithSharedCorrelationId()
    {
        var scope = NewTenant();
        var userId = NewId();

        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new LoginUser(userId, "Harry"), 0);
        await session.CommitAsync();

        // The login: fact into the event log + state patch — one transaction (ADR-013).
        await session.AppendAsync(new UserLoggedIn(userId, "android"));
        await session.PatchAsync<LoginUser>(userId, p => p.Set(x => x.LastLoginAt, "2026-06-11T10:00:00Z"));
        await session.CommitAsync();

        var eventCorrelation = await LoadScalarAsync(
            scope, "SELECT metadata ->> 'correlationId' FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId");
        var changeCorrelation = await LoadScalarAsync(
            scope, "SELECT metadata ->> 'correlationId' FROM papuma.change WHERE scope = @scope AND tenant_id = @tenantId AND version = 2");

        Assert.Equal(session.CorrelationId.ToString("N"), eventCorrelation);
        Assert.Equal(eventCorrelation, changeCorrelation);
    }

    [Fact]
    public async Task UncommittedAppend_IsRolledBack_WithTheSession()
    {
        var scope = NewTenant();
        await using (var session = _store.OpenSession(scope))
        {
            await session.AppendAsync(new UserLoggedIn(NewId(), "ios"));
            // no commit
        }

        var count = await LoadScalarAsync(
            scope, "SELECT count(*)::text FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId");
        Assert.Equal("0", count);
    }

    [Fact]
    public async Task PayloadPolicies_RedactRemovesField_HashReplacesValue()
    {
        var scope = NewTenant();
        await using (var session = _store.OpenSession(scope))
        {
            await session.AppendAsync(new UserLoggedIn(NewId(), "web", Ip: "203.0.113.7", SessionToken: "tok_secret"));
            await session.CommitAsync();
        }

        var payload = await LoadScalarAsync(
            scope, "SELECT payload::text FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId");

        Assert.DoesNotContain("203.0.113.7", payload);   // [SensitiveData] → removed
        Assert.DoesNotContain("ip", payload);
        Assert.DoesNotContain("tok_secret", payload);    // [TrackHash] → hex hash
        Assert.Contains("sessionToken", payload);
        Assert.Contains("web", payload);                 // untouched fields stay
    }

    [Fact]
    public async Task EventProcessor_DeliversCommittedEvents_InOrder_Deserializable()
    {
        var scope = NewTenant();
        var userId = NewId();
        await using (var session = _store.OpenSession(scope))
        {
            await session.AppendAsync(new UserLoggedIn(userId, "android"));
            await session.AppendAsync(new UserLoggedIn(userId, "desktop"));
            await session.CommitAsync();
        }

        var handler = new RecordingEventHandler($"h_{Guid.NewGuid():N}");
        var processor = new EventFeedProcessor(_fixture.DataSource, [handler]);
        await processor.ProcessOnceAsync();

        var received = handler.Received
            .Where(e => e.EventType == nameof(UserLoggedIn) && e.Deserialize<UserLoggedIn>().UserId == userId)
            .ToList();

        Assert.Equal(2, received.Count);
        Assert.True(received[0].Seq < received[1].Seq);
        Assert.Equal(["android", "desktop"], received.Select(e => e.Deserialize<UserLoggedIn>().Device));
        Assert.Equal(scope.TenantId, received[0].Scope.TenantId);

        // Redacted field comes back as default — consumers never see policy-protected values.
        Assert.Null(received[0].Deserialize<UserLoggedIn>().Ip);
    }

    [Fact]
    public async Task Retention_PurgesOnlyConfiguredTypes()
    {
        var scope = NewTenant();
        await using (var session = _store.OpenSession(scope))
        {
            await session.AppendAsync(new ShortLived("purge me"));
            await session.AppendAsync(new KeptForever("keep me"));
            await session.CommitAsync();
        }

        var deleted = await EventRetention.PurgeExpiredAsync(_fixture.DataSource, _model);

        Assert.True(deleted >= 1);
        var shortLived = await LoadScalarAsync(
            scope, "SELECT count(*)::text FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId AND event_type = 'ShortLived'");
        var kept = await LoadScalarAsync(
            scope, "SELECT count(*)::text FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId AND event_type = 'KeptForever'");

        Assert.Equal("0", shortLived);
        Assert.Equal("1", kept);
    }

    [Fact]
    public async Task UnregisteredEventType_FailsLoudly()
    {
        await using var session = _store.OpenSession(NewTenant());

        await Assert.ThrowsAsync<EventTypeNotRegisteredException>(
            () => session.AppendAsync(new { Whatever = 1 }));
    }

    [Fact]
    public void ReferencePolicy_OnEventType_IsRejectedAtBuild()
    {
        var builder = new KernelModelBuilder()
            .Event<UserLoggedIn>(e => e.Property(x => x.Ip).Track()); // reset attribute first

        // Reference via attribute on a fresh type:
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new KernelModelBuilder().Event<ReferencedEvent>().Build());
        Assert.Contains("Reference", ex.Message, StringComparison.Ordinal);

        _ = builder.Build(); // sanity: Track-reset variant builds fine
    }

    private sealed record ReferencedEvent(string Id, [property: TrackReference] string Email);

    private async Task<string> LoadScalarAsync(ScopeContext scope, string sql)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
