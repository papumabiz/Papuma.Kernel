// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;

namespace Papuma.Kernel.Store;

/// <summary>
/// GDPR redaction primitives (ADR-015) — SQLite counterpart of
/// <c>DocumentSession.Gdpr.cs</c>. Both operations are irreversible, demand an audit
/// reason, and run inside the session transaction (commit required, savepoint semantics
/// apply). No <c>SELECT ... FOR UPDATE</c> row locking here — a single-writer embedded
/// store has no concurrent writers to guard against inside one transaction.
/// </summary>
public sealed partial class SqliteDocumentSession
{
    /// <summary>
    /// Rewrites historical change diffs of one document to redaction markers —
    /// the safety net for personal data that was tracked verbatim before a policy
    /// existed (ADR-015). Works for deleted documents too (their history survives).
    /// </summary>
    /// <remarks>
    /// Every rewritten change gets a <c>redaction</c> audit block in its metadata
    /// (when, why, actor, correlation). Consequence per ADR-008: rolling back across
    /// the redacted range fails typed (<see cref="RollbackNotPossibleException"/>) —
    /// the values are gone, by design.
    /// </remarks>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="id">The document identifier.</param>
    /// <param name="reason">The mandatory audit reason (e.g. the erasure request reference).</param>
    /// <param name="paths">
    /// Dot-separated paths to redact, covering descendants (<c>address</c> covers
    /// <c>address.city</c>). <c>null</c> redacts every entry of every change.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of change records that were rewritten.</returns>
    public async Task<int> RedactHistoryAsync<T>(
        string id,
        string reason,
        IReadOnlyCollection<string>? paths = null,
        CancellationToken ct = default)
        where T : class
    {
        var metadata = _model.GetRequired<T>();
        Validation.InputValidator.ValidateDocumentId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var activity = StartWriteActivity("redact", metadata.Name, id);
        try
        {
            return await ExecuteWriteAsync(async (conn, tx) =>
            {
                var dirty = new List<(long Seq, JsonObject Diff, JsonObject Metadata)>();

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT seq, diff, metadata
                        FROM change
                        WHERE scope = @scope AND tenant_id = @tenantId
                          AND document_type = @type AND document_id = @id
                        ORDER BY version
                        """;
                    AddIdentityParameters(cmd, metadata.Name, id);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var diff = (JsonObject)JsonNode.Parse(reader.GetString(1))!;
                        if (RedactionEngine.RedactDiffEntries(diff, paths))
                        {
                            var existingMetadata = (JsonObject)JsonNode.Parse(reader.GetString(2))!;
                            dirty.Add((reader.GetInt64(0), diff, existingMetadata));
                        }
                    }
                }

                var audit = RedactionEngine.BuildRedactionAudit(CorrelationId, _options.ActorId, reason, paths);
                foreach (var (seq, diff, existingMetadata) in dirty)
                {
                    MergeInto(existingMetadata, audit);

                    await using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = "UPDATE change SET diff = @diff, metadata = @metadata WHERE seq = @seq";
                    update.Parameters.AddWithValue("seq", seq);
                    update.Parameters.AddWithValue("diff", diff.ToJsonString());
                    update.Parameters.AddWithValue("metadata", existingMetadata.ToJsonString());
                    await update.ExecuteNonQueryAsync(ct);
                }

                _hasWrites |= dirty.Count > 0;
                return dirty.Count;
            }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    /// <summary>
    /// Removes payload fields from stored events selected by a payload field value —
    /// the event-log counterpart of <see cref="RedactHistoryAsync{T}"/> (ADR-015).
    /// Removal matches the <c>Redact</c> event policy semantics: the field is absent,
    /// consumers see their type defaults (ADR-013).
    /// </summary>
    /// <typeparam name="TEvent">The registered event CLR type.</typeparam>
    /// <param name="selectorPath">The dot-separated payload path identifying the data subject (e.g. <c>userId</c>).</param>
    /// <param name="selectorValue">The value to match (text comparison).</param>
    /// <param name="paths">The payload paths to remove (descendant-covering). Must not be empty.</param>
    /// <param name="reason">The mandatory audit reason.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The number of events that were rewritten.</returns>
    public async Task<int> RedactEventsAsync<TEvent>(
        string selectorPath,
        string selectorValue,
        IReadOnlyCollection<string> paths,
        string reason,
        CancellationToken ct = default)
        where TEvent : class
    {
        var metadata = _model.GetRequiredEvent<TEvent>();
        ArgumentException.ThrowIfNullOrWhiteSpace(selectorPath);
        ArgumentNullException.ThrowIfNull(selectorValue);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (paths.Count == 0)
        {
            throw new ArgumentException(
                "At least one payload path is required. To drop whole events, use the " +
                "retention mechanism or delete by type and selector in the application.",
                nameof(paths));
        }

        using var activity = StartWriteActivity("redact", metadata.Name, documentId: null);
        try
        {
            return await ExecuteWriteAsync(async (conn, tx) =>
            {
                var dirty = new List<(long Seq, JsonObject Payload, JsonObject Metadata)>();

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT seq, payload, metadata
                        FROM event
                        WHERE scope = @scope AND tenant_id = @tenantId
                          AND event_type = @type
                          AND json_extract(payload, @path) = @value
                        ORDER BY seq
                        """;
                    cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
                    cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
                    cmd.Parameters.AddWithValue("type", metadata.Name);
                    cmd.Parameters.AddWithValue("path", "$." + selectorPath);
                    cmd.Parameters.AddWithValue("value", selectorValue);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var payload = (JsonObject)JsonNode.Parse(reader.GetString(1))!;
                        if (RedactionEngine.RemovePayloadPaths(payload, paths))
                        {
                            var existingMetadata = (JsonObject)JsonNode.Parse(reader.GetString(2))!;
                            dirty.Add((reader.GetInt64(0), payload, existingMetadata));
                        }
                    }
                }

                var audit = RedactionEngine.BuildRedactionAudit(CorrelationId, _options.ActorId, reason, paths);
                foreach (var (seq, payload, existingMetadata) in dirty)
                {
                    MergeInto(existingMetadata, audit);

                    await using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = "UPDATE event SET payload = @payload, metadata = @metadata WHERE seq = @seq";
                    update.Parameters.AddWithValue("seq", seq);
                    update.Parameters.AddWithValue("payload", payload.ToJsonString());
                    update.Parameters.AddWithValue("metadata", existingMetadata.ToJsonString());
                    await update.ExecuteNonQueryAsync(ct);
                }

                _hasWrites |= dirty.Count > 0;
                return dirty.Count;
            }, ct);
        }
        catch (Exception ex) when (RecordFailure(activity, ex))
        {
            throw; // unreachable — the filter never catches
        }
    }

    /// <summary>
    /// Shallow top-level merge of <paramref name="source"/> into <paramref name="target"/>
    /// — the in-process replacement for Postgres's jsonb <c>||</c> concat operator
    /// (<c>BuildRedactionAudit</c> only ever produces one top-level key, so this is
    /// exactly equivalent, not an approximation).
    /// </summary>
    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value?.DeepClone();
        }
    }
}
