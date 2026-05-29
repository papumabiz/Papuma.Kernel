// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Writes validated change feed records into the PostgreSQL-backed change feed store.
/// </summary>
public sealed class ChangeWriter
{
    private static readonly Regex ValidEntityPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{1,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex ValidEventTypePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{2,100}$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly ChangeWriterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeWriter"/> class.
    /// </summary>
    /// <param name="options">Optional writer configuration.</param>
    public ChangeWriter(ChangeWriterOptions? options = null)
    {
        _options = options ?? new ChangeWriterOptions();
    }

    /// <summary>
    /// Appends a new change feed record to the current transaction.
    /// </summary>
    /// <param name="transaction">The ambient PostgreSQL transaction.</param>
    /// <param name="entity">The logical entity name that produced the change.</param>
    /// <param name="entityId">The entity identifier within its logical namespace.</param>
    /// <param name="eventType">The event type that describes the change.</param>
    /// <param name="version">The aggregate version associated with the change.</param>
    /// <param name="payloadJson">The JSON payload to persist.</param>
    /// <param name="actorId">The actor that caused the change.</param>
    /// <param name="correlationId">Optional correlation identifier for distributed tracing.</param>
    /// <param name="causationId">Optional causation identifier pointing to the upstream event.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task AppendAsync(
        NpgsqlTransaction transaction,
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId,
        string? correlationId = null,
        string? causationId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        ValidateInputs(entity, entityId, eventType, version, payloadJson, actorId);

        await using var cmd = transaction.Connection!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO change_feed
                (entity, entity_id, event_type, version, correlation_id, causation_id, actor_id, payload)
            VALUES
                (@entity, @entityId, @eventType, @version, @correlationId, @causationId, @actorId, @payload::jsonb)
            """;

        cmd.Parameters.AddWithValue("entity", entity);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("eventType", eventType);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("correlationId", (object?)correlationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("causationId", (object?)causationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("actorId", actorId);
        cmd.Parameters.AddWithValue("payload", payloadJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Validates change feed input values before they are written to the store.
    /// </summary>
    /// <param name="entity">The logical entity name that produced the change.</param>
    /// <param name="entityId">The entity identifier within its logical namespace.</param>
    /// <param name="eventType">The event type that describes the change.</param>
    /// <param name="version">The aggregate version associated with the change.</param>
    /// <param name="payloadJson">The JSON payload to validate.</param>
    /// <param name="actorId">The actor that caused the change.</param>
    public void ValidateInputs(
        string entity,
        string entityId,
        string eventType,
        int version,
        string payloadJson,
        string actorId)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        if (!ValidEntityPattern.IsMatch(entity))
        {
            throw new ArgumentException(
                $"Invalid entity name '{entity}'. Must match [A-Za-z][A-Za-z0-9_]{{1,100}}.",
                nameof(entity));
        }

        if (string.IsNullOrWhiteSpace(entityId) || entityId.Length > 200)
        {
            throw new ArgumentException(
                "entityId must not be empty and max 200 characters.",
                nameof(entityId));
        }

        if (!ValidEventTypePattern.IsMatch(eventType))
        {
            throw new ArgumentException(
                $"Invalid eventType '{eventType}'. Must match [A-Za-z][A-Za-z0-9_]{{2,100}}.",
                nameof(eventType));
        }

        if (version < 1)
        {
            throw new ArgumentException("version must be >= 1.", nameof(version));
        }

        if (string.IsNullOrWhiteSpace(actorId) || actorId.Length > 200)
        {
            throw new ArgumentException(
                "actorId is required and must not exceed 200 characters.",
                nameof(actorId));
        }

        if (Encoding.UTF8.GetByteCount(payloadJson) > _options.MaxPayloadSizeBytes)
        {
            throw new ArgumentException(
                $"Payload exceeds maximum size of {_options.MaxPayloadSizeBytes} bytes ({Encoding.UTF8.GetByteCount(payloadJson)} bytes).",
                nameof(payloadJson));
        }
    }
}