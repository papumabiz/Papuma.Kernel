// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Tests.Validation;

public class InputValidatorTests
{
    [Theory]
    [InlineData("User")]
    [InlineData("UserProfile")]
    [InlineData("Asset_V2")]
    public void ValidateEntity_AllowsValidNames(string entity)
    {
        var exception = Record.Exception(() => InputValidator.ValidateEntity(entity));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("1User")]
    [InlineData("U")]
    [InlineData("User-Profile")]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateEntity_RejectsInvalidNames(string entity)
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateEntity(entity));

        Assert.Equal("entity", exception.ParamName);
    }

    [Theory]
    [InlineData("user-123")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    public void ValidateEntityId_AllowsValidIds(string entityId)
    {
        var exception = Record.Exception(() => InputValidator.ValidateEntityId(entityId));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateEntityId_RejectsEmptyIds(string entityId)
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateEntityId(entityId));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Fact]
    public void ValidateEntityId_RejectsTooLongIds()
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateEntityId(new string('a', 201)));

        Assert.Equal("entityId", exception.ParamName);
    }

    [Theory]
    [InlineData("UserEmailUpdated")]
    [InlineData("UserLoggedIn")]
    [InlineData("Asset_Created_V2")]
    public void ValidateEventType_AllowsValidTypes(string eventType)
    {
        var exception = Record.Exception(() => InputValidator.ValidateEventType(eventType));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("Up")]
    [InlineData("1UserLoggedIn")]
    [InlineData("User-Logged-In")]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateEventType_RejectsInvalidTypes(string eventType)
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateEventType(eventType));

        Assert.Equal("eventType", exception.ParamName);
    }

    [Theory]
    [InlineData("user:123")]
    [InlineData("system")]
    [InlineData("admin:550e8400-e29b-41d4-a716-446655440000")]
    public void ValidateActorId_AllowsValidIds(string actorId)
    {
        var exception = Record.Exception(() => InputValidator.ValidateActorId(actorId));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateActorId_RejectsEmptyIds(string actorId)
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateActorId(actorId));

        Assert.Equal("actorId", exception.ParamName);
    }

    [Fact]
    public void ValidateActorId_RejectsTooLongIds()
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateActorId(new string('a', 201)));

        Assert.Equal("actorId", exception.ParamName);
    }

    [Fact]
    public void ValidatePayloadSize_AllowsPayloadWithinLimit()
    {
        var exception = Record.Exception(() => InputValidator.ValidatePayloadSize("{}", 256 * 1024));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidatePayloadSize_RejectsPayloadOverLimit()
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidatePayloadSize("123456", 5));

        Assert.Equal("payloadJson", exception.ParamName);
    }

    [Fact]
    public void ValidatePayloadSize_ThrowsForNullPayload()
    {
        Assert.Throws<ArgumentNullException>(() => InputValidator.ValidatePayloadSize(null!, 256 * 1024));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void ValidateVersion_AllowsValidVersions(int version)
    {
        var exception = Record.Exception(() => InputValidator.ValidateVersion(version));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateVersion_RejectsInvalidVersions(int version)
    {
        var exception = Assert.Throws<ArgumentException>(() => InputValidator.ValidateVersion(version));

        Assert.Equal("version", exception.ParamName);
    }
}
