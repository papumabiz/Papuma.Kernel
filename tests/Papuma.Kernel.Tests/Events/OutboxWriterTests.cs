// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Events;

namespace Papuma.Kernel.Tests.Events;

public class OutboxWriterTests
{
    private readonly OutboxWriter _sut = new();

    [Fact]
    public async Task EnqueueAsync_ThrowsForNullTransaction()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.EnqueueAsync(
            transaction: null!,
            eventId: Guid.NewGuid(),
            eventType: "UserLoggedIn",
            payloadJson: "{}"));
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("1UserLoggedIn")]
    [InlineData("User-Logged-In")]
    public async Task EnqueueAsync_ThrowsForInvalidEventType(string eventType)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.EnqueueAsync(
            transaction: null!,
            eventId: Guid.NewGuid(),
            eventType: eventType,
            payloadJson: "{}"));
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsForNullPayloadJson()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.EnqueueAsync(
            transaction: null!,
            eventId: Guid.NewGuid(),
            eventType: "UserLoggedIn",
            payloadJson: null!));
    }

    [Fact]
    public async Task EnqueueAsync_ThrowsForPayloadOverConfiguredByteLimit()
    {
        var sut = new OutboxWriter(new OutboxWriterOptions { MaxPayloadSizeBytes = 5 });

        await Assert.ThrowsAsync<ArgumentException>(() => sut.EnqueueAsync(
            transaction: null!,
            eventId: Guid.NewGuid(),
            eventType: "UserLoggedIn",
            payloadJson: "123456"));
    }
}
