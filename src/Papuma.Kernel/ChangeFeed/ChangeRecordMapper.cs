// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.ChangeFeed;

/// <summary>
/// Maps a row from <c>papuma_event_feed</c> to a <see cref="ChangeRecord"/>.
/// Resolves column ordinals once per result set to avoid per-row dictionary lookups.
/// </summary>
internal static class ChangeRecordMapper
{
    /// <summary>
    /// Reads all rows from <paramref name="reader"/> into a list of <see cref="ChangeRecord"/>.
    /// The reader must be positioned before the first row (i.e. <c>ReadAsync</c> not yet called).
    /// </summary>
    internal static async Task<List<ChangeRecord>> ReadAllAsync(
        NpgsqlDataReader reader,
        CancellationToken ct)
    {
        // Resolve ordinals once — avoids a dictionary lookup on every GetXxx call inside the loop.
        var colSequenceId    = reader.GetOrdinal("sequence_id");
        var colKind          = reader.GetOrdinal("kind");
        var colEventId       = reader.GetOrdinal("event_id");
        var colScope         = reader.GetOrdinal("scope");
        var colTenantId      = reader.GetOrdinal("tenant_id");
        var colEntity        = reader.GetOrdinal("entity");
        var colEntityId      = reader.GetOrdinal("entity_id");
        var colEventType     = reader.GetOrdinal("event_type");
        var colVersion       = reader.GetOrdinal("version");
        var colCorrelationId = reader.GetOrdinal("correlation_id");
        var colCausationId   = reader.GetOrdinal("causation_id");
        var colActorId       = reader.GetOrdinal("actor_id");
        var colPayload       = reader.GetOrdinal("payload");
        var colOccurredAt    = reader.GetOrdinal("occurred_at");

        var records = new List<ChangeRecord>();

        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                SequenceId:    reader.GetInt64(colSequenceId),
                Kind:          reader.GetString(colKind),
                EventId:       reader.IsDBNull(colEventId)       ? null : reader.GetGuid(colEventId),
                Entity:        reader.IsDBNull(colEntity)        ? null : reader.GetString(colEntity),
                EntityId:      reader.IsDBNull(colEntityId)      ? null : reader.GetString(colEntityId),
                EventType:     reader.GetString(colEventType),
                Version:       reader.IsDBNull(colVersion)       ? null : reader.GetInt32(colVersion),
                CorrelationId: reader.IsDBNull(colCorrelationId) ? null : reader.GetString(colCorrelationId),
                CausationId:   reader.IsDBNull(colCausationId)   ? null : reader.GetString(colCausationId),
                ActorId:       reader.GetString(colActorId),
                PayloadJson:   reader.GetString(colPayload),
                OccurredAt:    reader.GetFieldValue<DateTimeOffset>(colOccurredAt),
                Scope:         Enum.Parse<ScopeType>(reader.GetString(colScope), ignoreCase: false),
                TenantId:      reader.IsDBNull(colTenantId)      ? null : reader.GetString(colTenantId)));
        }

        return records;
    }
}
