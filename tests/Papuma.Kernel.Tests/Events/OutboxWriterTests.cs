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

    [Fact]
    public async Task EnqueueAsync_ThrowsForNullEventType()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.EnqueueAsync(
            transaction: null!,
            eventId: Guid.NewGuid(),
            eventType: null!,
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
}