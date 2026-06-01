// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Events;

namespace Papuma.Kernel.Tests.Events;

public class BusinessEventWriterTests
{
    private readonly BusinessEventWriter _sut = new();

    [Fact]
    public async Task AppendAsync_ThrowsForNullTransaction()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForNullEventType()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            eventType: null!,
            actorId: "user:123",
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForNullActorId()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            eventType: "UserLoggedIn",
            actorId: null!,
            payloadJson: "{}"));
    }

    [Fact]
    public async Task AppendAsync_ThrowsForNullPayloadJson()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.AppendAsync(
            transaction: null!,
            eventType: "UserLoggedIn",
            actorId: "user:123",
            payloadJson: null!));
    }
}