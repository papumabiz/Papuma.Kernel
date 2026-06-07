// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Tests.ChangeFeed;

public class ChangeWriterTests
{
    private readonly ChangeWriter _sut = new();

    [Fact]
    public async Task AppendChangeAsync_ThrowsForInvalidEntity()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendChangeAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            entity: "U",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler"));
    }

    [Fact]
    public async Task AppendChangeAsync_ThrowsForVersionBelowOne()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendChangeAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 0,
            payloadJson: "{}",
            actorId: "system:scheduler"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AppendChangeAsync_ThrowsForEmptyActorId(string actorId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendChangeAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: actorId));
    }

    [Fact]
    public async Task AppendEventAsync_ThrowsForInvalidEventType()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendEventAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            eventType: "Up",
            actorId: "user:123",
            payloadJson: "{}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AppendEventAsync_ThrowsForEmptyActorId(string actorId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendEventAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: actorId,
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendEventAsync_AllowsNullEntityAndEntityId()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendEventAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            entity: null,
            entityId: null));
    }

    [Fact]
    public async Task AppendEventAsync_ThrowsForPayloadOverConfiguredByteLimit()
    {
        var sut = new ChangeWriter(new ChangeWriterOptions { MaxPayloadSizeBytes = 5 });

        await Assert.ThrowsAsync<ArgumentException>(() => sut.AppendEventAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "123456"));
    }

    [Fact]
    public async Task AppendEventAsync_ThrowsForTooLongIdempotencyKey()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendEventAsync(
            transaction: null!,
            scope: Papuma.Kernel.Tenancy.ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            idempotencyKey: new string('a', 201)));
    }
}
