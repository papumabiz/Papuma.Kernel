// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text;
using System.Text.RegularExpressions;

namespace Papuma.Kernel.Validation;

/// <summary>
/// Provides shared input validation for change feed and event writers.
/// </summary>
public static class InputValidator
{
    private static readonly Regex ValidEntityPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{1,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ValidEventTypePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{2,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Validates an entity name against the allowed pattern.
    /// </summary>
    /// <param name="entity">The entity name to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the entity name is invalid.</exception>
    public static void ValidateEntity(string entity)
    {
        if (!ValidEntityPattern.IsMatch(entity))
        {
            throw new ArgumentException(
                $"Invalid entity name '{entity}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                nameof(entity));
        }
    }

    /// <summary>
    /// Validates an entity identifier.
    /// </summary>
    /// <param name="entityId">The entity identifier to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the entity identifier is invalid.</exception>
    public static void ValidateEntityId(string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId) || entityId.Length > 200)
        {
            throw new ArgumentException(
                "entityId must not be empty and max 200 characters.",
                nameof(entityId));
        }
    }

    /// <summary>
    /// Validates an event type against the allowed pattern.
    /// </summary>
    /// <param name="eventType">The event type to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the event type is invalid.</exception>
    public static void ValidateEventType(string eventType)
    {
        if (!ValidEventTypePattern.IsMatch(eventType))
        {
            throw new ArgumentException(
                $"Invalid eventType '{eventType}'. Must match [A-Za-z][A-Za-z0-9_]{{2,100}}.",
                nameof(eventType));
        }
    }

    /// <summary>
    /// Validates an actor identifier.
    /// </summary>
    /// <param name="actorId">The actor identifier to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the actor identifier is invalid.</exception>
    public static void ValidateActorId(string actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || actorId.Length > 200)
        {
            throw new ArgumentException(
                "actorId is required and must not exceed 200 characters.",
                nameof(actorId));
        }
    }

    /// <summary>
    /// Validates a JSON payload against a maximum byte size.
    /// </summary>
    /// <param name="payloadJson">The JSON payload to validate.</param>
    /// <param name="maxPayloadSizeBytes">The maximum allowed size in bytes.</param>
    /// <exception cref="ArgumentException">Thrown when the payload exceeds the size limit.</exception>
    public static void ValidatePayloadSize(string payloadJson, int maxPayloadSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var byteCount = Encoding.UTF8.GetByteCount(payloadJson);
        if (byteCount > maxPayloadSizeBytes)
        {
            throw new ArgumentException(
                $"Payload exceeds maximum size of {maxPayloadSizeBytes} bytes ({byteCount} bytes).",
                nameof(payloadJson));
        }
    }

    /// <summary>
    /// Validates a schema version number.
    /// </summary>
    /// <param name="version">The version to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the version is less than 1.</exception>
    public static void ValidateVersion(int version)
    {
        if (version < 1)
        {
            throw new ArgumentException("version must be >= 1.", nameof(version));
        }
    }

    /// <summary>
    /// Validates an idempotency key.
    /// </summary>
    /// <param name="idempotencyKey">The idempotency key to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the key is invalid.</exception>
    public static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new ArgumentException(
                "idempotencyKey is required and must not exceed 200 characters.",
                nameof(idempotencyKey));
        }
    }
}
