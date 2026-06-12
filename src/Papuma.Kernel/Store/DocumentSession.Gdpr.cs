// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

namespace Papuma.Kernel.Store;

/// <summary>
/// GDPR redaction primitives (ADR-015). These deliberately break the append-only
/// purity of the feed — Art. 17 beats architectural aesthetics. Both operations are
/// irreversible, demand an audit reason, and run inside the session transaction
/// (commit required, savepoint semantics apply).
/// </summary>
public sealed partial class DocumentSession
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
                var dirty = new List<(long Seq, JsonObject Diff)>();

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT seq, diff::text
                        FROM papuma.change
                        WHERE scope = @scope AND tenant_id = @tenantId
                          AND document_type = @type AND document_id = @id
                        ORDER BY version
                        FOR UPDATE
                        """;
                    AddIdentityParameters(cmd, metadata.Name, id);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var diff = (JsonObject)JsonNode.Parse(reader.GetString(1))!;
                        if (RedactDiffEntries(diff, paths))
                        {
                            dirty.Add((reader.GetInt64(0), diff));
                        }
                    }
                }

                var audit = BuildRedactionAudit(reason, paths);
                foreach (var (seq, diff) in dirty)
                {
                    await using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = """
                        UPDATE papuma.change
                        SET diff = @diff, metadata = metadata || @audit
                        WHERE seq = @seq
                        """;
                    update.Parameters.AddWithValue("seq", seq);
                    AddJsonbParameter(update, "diff", diff);
                    AddJsonbParameter(update, "audit", audit.DeepClone());
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
                var dirty = new List<(long Seq, JsonObject Payload)>();

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT seq, payload::text
                        FROM papuma.event
                        WHERE scope = @scope AND tenant_id = @tenantId
                          AND event_type = @type
                          AND payload #>> @path = @value
                        ORDER BY seq
                        FOR UPDATE
                        """;
                    cmd.Parameters.AddWithValue("scope", Scope.Scope.ToString());
                    cmd.Parameters.AddWithValue("tenantId", Scope.TenantId ?? string.Empty);
                    cmd.Parameters.AddWithValue("type", metadata.Name);
                    cmd.Parameters.Add(new NpgsqlParameter("path", NpgsqlDbType.Array | NpgsqlDbType.Text)
                    {
                        Value = selectorPath.Split('.'),
                    });
                    cmd.Parameters.AddWithValue("value", selectorValue);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var payload = (JsonObject)JsonNode.Parse(reader.GetString(1))!;
                        if (RemovePayloadPaths(payload, paths))
                        {
                            dirty.Add((reader.GetInt64(0), payload));
                        }
                    }
                }

                var audit = BuildRedactionAudit(reason, paths);
                foreach (var (seq, payload) in dirty)
                {
                    await using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = """
                        UPDATE papuma.event
                        SET payload = @payload, metadata = metadata || @audit
                        WHERE seq = @seq
                        """;
                    update.Parameters.AddWithValue("seq", seq);
                    AddJsonbParameter(update, "payload", payload);
                    AddJsonbParameter(update, "audit", audit.DeepClone());
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

    // ── Redaction internals ────────────────────────────────────────────────────

    /// <summary>
    /// Rewrites matching diff entries (wire format, ADR-004) to the redaction marker
    /// <c>{"changed": true}</c>. Returns whether anything changed — already-redacted
    /// entries make the operation idempotent.
    /// </summary>
    private static bool RedactDiffEntries(JsonObject diff, IReadOnlyCollection<string>? paths)
    {
        var changed = false;
        foreach (var entryPath in diff.Select(e => e.Key).ToList())
        {
            if (paths is not null && !paths.Any(p => PathCovers(p, entryPath)))
            {
                continue;
            }

            // Idempotency: a pure {"changed": true} marker is already fully redacted.
            if (diff[entryPath] is JsonObject { Count: 1 } existing && existing.ContainsKey("changed"))
            {
                continue;
            }

            diff[entryPath] = new JsonObject { ["changed"] = true };
            changed = true;
        }

        return changed;
    }

    private static bool RemovePayloadPaths(JsonObject payload, IReadOnlyCollection<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            var segments = path.Split('.');
            var current = payload;
            for (var i = 0; i < segments.Length - 1 && current is not null; i++)
            {
                current = current[segments[i]] as JsonObject;
            }

            if (current is not null && current.Remove(segments[^1]))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Returns whether <paramref name="declared"/> covers <paramref name="path"/> (self or descendant).</summary>
    private static bool PathCovers(string declared, string path) =>
        path == declared || (path.Length > declared.Length && path[declared.Length] == '.'
            && path.StartsWith(declared, StringComparison.Ordinal));

    /// <summary>
    /// Builds the mandatory audit block merged into the metadata of every rewritten
    /// record: when, why, who (when the session carries an actor), correlation.
    /// </summary>
    private JsonObject BuildRedactionAudit(string reason, IReadOnlyCollection<string>? paths)
    {
        var redaction = new JsonObject
        {
            ["redactedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["reason"] = reason,
            ["correlationId"] = CorrelationId.ToString("N"),
        };

        if (_options.ActorId is not null)
        {
            redaction["actorId"] = _options.ActorId;
        }

        if (paths is null)
        {
            redaction["paths"] = "all";
        }
        else
        {
            var array = new JsonArray();
            foreach (var path in paths)
            {
                array.Add(path);
            }

            redaction["paths"] = array;
        }

        return new JsonObject { ["redaction"] = redaction };
    }
}
