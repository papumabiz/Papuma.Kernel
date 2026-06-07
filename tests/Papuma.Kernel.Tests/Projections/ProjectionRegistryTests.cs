// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Projections;

public class ProjectionRegistryTests
{
    [Fact]
    public async Task DispatchAsync_RoutesToRegisteredVersionedHandler()
    {
        var registry = new ProjectionRegistry();
        var handler = new TestVersionedHandler();
        var record = new ChangeRecord(
            SequenceId: 1,
            Kind: "Change",
            EventId: null,
            Entity: "User",
            EntityId: "user-1",
            EventType: "UserEmailUpdated",
            Version: 2,
            CorrelationId: null,
            CausationId: null,
            ActorId: "user:123",
            PayloadJson: "{\"Value\":\"alice@example.com\"}",
            OccurredAt: DateTimeOffset.UtcNow,
            Scope: ScopeType.Tenant,
            TenantId: "acme");

        registry.Register("UserEmailUpdated", handler);

        await registry.DispatchAsync(record);

        Assert.Equal("alice@example.com", handler.LastValue);
        Assert.Same(record, handler.LastRecord);
    }

    [Fact]
    public async Task DispatchAsync_ThrowsWhenNoHandlerIsRegistered()
    {
        var registry = new ProjectionRegistry();
        var record = new ChangeRecord(
            SequenceId: 1,
            Kind: "Change",
            EventId: null,
            Entity: "User",
            EntityId: "user-1",
            EventType: "UserEmailUpdated",
            Version: 99,
            CorrelationId: null,
            CausationId: null,
            ActorId: "user:123",
            PayloadJson: "{}",
            OccurredAt: DateTimeOffset.UtcNow,
            Scope: ScopeType.Tenant,
            TenantId: "acme");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DispatchAsync(record));

        Assert.Contains("UserEmailUpdated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_ThrowsWhenVersionIsNull()
    {
        var registry = new ProjectionRegistry();
        var record = new ChangeRecord(
            SequenceId: 1,
            Kind: "Event",
            EventId: Guid.NewGuid(),
            Entity: null,
            EntityId: null,
            EventType: "UserLoggedIn",
            Version: null,
            CorrelationId: null,
            CausationId: null,
            ActorId: "user:123",
            PayloadJson: "{}",
            OccurredAt: DateTimeOffset.UtcNow,
            Scope: ScopeType.Tenant,
            TenantId: "acme");

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DispatchAsync(record));
    }

    private sealed class TestVersionedHandler : IVersionedHandler<TestPayload>
    {
        public int Version => 2;

        public string? LastValue { get; private set; }

        public ChangeRecord? LastRecord { get; private set; }

        public Task HandleAsync(TestPayload data, ChangeRecord record, CancellationToken ct = default)
        {
            LastValue = data.Value;
            LastRecord = record;
            return Task.CompletedTask;
        }
    }

    private sealed record TestPayload(string Value);
}
