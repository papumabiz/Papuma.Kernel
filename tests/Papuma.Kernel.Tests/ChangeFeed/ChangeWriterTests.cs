// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.ChangeFeed;

namespace Papuma.Kernel.Tests.ChangeFeed;

public class ChangeWriterTests
{
    private readonly ChangeWriter _sut = new();

    [Fact]
    public void ValidateInputs_AllowsDocumentedValidInput()
    {
        var exception = Record.Exception(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "user:550e8400-e29b-41d4-a716-446655440000"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("1User")]
    [InlineData("U")]
    [InlineData("User-Profile")]
    public void ValidateInputs_RejectsInvalidEntity(string entity)
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity,
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler"));

        Assert.Equal("entity", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateInputs_RejectsEmptyEntityId(string entityId)
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId,
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler"));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Fact]
    public void ValidateInputs_RejectsTooLongEntityId()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: new string('a', 201),
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler"));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("1UserEmailUpdated")]
    [InlineData("User-Email-Updated")]
    public void ValidateInputs_RejectsInvalidEventType(string eventType)
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType,
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler"));

        Assert.Equal("eventType", exception.ParamName);
    }

    [Fact]
    public void ValidateInputs_RejectsVersionBelowOne()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 0,
            payloadJson: "{}",
            actorId: "system:scheduler"));

        Assert.Equal("version", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateInputs_RejectsEmptyActorId(string actorId)
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId));

        Assert.Equal("actorId", exception.ParamName);
    }

    [Fact]
    public void ValidateInputs_RejectsTooLongActorId()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: new string('a', 201)));

        Assert.Equal("actorId", exception.ParamName);
    }

    [Fact]
    public void ValidateInputs_RejectsPayloadOverConfiguredByteLimit()
    {
        var sut = new ChangeWriter(new ChangeWriterOptions { MaxPayloadSizeBytes = 5 });

        var exception = Assert.Throws<ArgumentException>(() => sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "123456",
            actorId: "system:scheduler"));

        Assert.Equal("payloadJson", exception.ParamName);
    }

    [Fact]
    public void ValidateInputs_RejectsTooLongIdempotencyKey()
    {
        var exception = Assert.Throws<ArgumentException>(() => _sut.ValidateInputs(
            entity: "UserProfile",
            entityId: "user-123",
            eventType: "UserEmailUpdated",
            version: 1,
            payloadJson: "{}",
            actorId: "system:scheduler",
            idempotencyKey: new string('a', 201)));

        Assert.Equal("idempotencyKey", exception.ParamName);
    }
}