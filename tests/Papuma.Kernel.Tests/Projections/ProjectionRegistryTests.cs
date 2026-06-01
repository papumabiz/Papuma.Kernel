// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Projections;

namespace Papuma.Kernel.Tests.Projections;

public class ProjectionRegistryTests
{
    [Fact]
    public async Task DispatchAsync_RoutesToRegisteredVersionedHandler()
    {
        var registry = new ProjectionRegistry();
        var handler = new TestVersionedHandler();
        var record = new ChangeRecord(
            1,
            "User",
            "user-1",
            "UserEmailUpdated",
            2,
            null,
            null,
            "user:123",
            "{\"Value\":\"alice@example.com\"}",
            DateTimeOffset.UtcNow);

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
            1,
            "User",
            "user-1",
            "UserEmailUpdated",
            99,
            null,
            null,
            "user:123",
            "{}",
            DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DispatchAsync(record));

        Assert.Contains("UserEmailUpdated", exception.Message, StringComparison.Ordinal);
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