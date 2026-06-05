// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Events;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Tests.Events;

public class BusinessEventWriterTests
{
    private readonly BusinessEventWriter _sut = new();

    [Fact]
    public async Task AppendAsync_ThrowsForNullTransaction()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}"));
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("1UserLoggedIn")]
    [InlineData("User-Logged-In")]
    public async Task AppendAsync_ThrowsForInvalidEventType(string eventType)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: eventType,
            actorId: "user:123",
            payloadJson: "{}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AppendAsync_ThrowsForEmptyActorId(string actorId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: actorId,
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForTooLongActorId()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: new string('a', 201),
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForNullPayloadJson()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: null!));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForPayloadOverConfiguredByteLimit()
    {
        var sut = new BusinessEventWriter(new BusinessEventWriterOptions { MaxPayloadSizeBytes = 5 });

        await Assert.ThrowsAsync<ArgumentException>(() => sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "123456"));
    }

    [Theory]
    [InlineData("1Entity")]
    [InlineData("E")]
    [InlineData("Entity-Name")]
    public async Task AppendAsync_ThrowsForInvalidEntity(string entity)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            entity: entity));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AppendAsync_ThrowsForInvalidEntityId(string entityId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            entity: "User",
            entityId: entityId));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForTooLongIdempotencyKey()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            idempotencyKey: new string('a', 201)));
    }

    [Fact]
    public async Task AppendAsync_AllowsNullEntityAndEntityId()
    {
        // Should fail on null transaction, not on validation – entity/entityId are optional.
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            scope: ScopeContext.Tenant("acme"),
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}",
            entity: null,
            entityId: null));
    }
}
