// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Assembles a structured Art.-15/20 export (ADR-015): current state, change history
/// and events of a data subject, as one JSON document read in a single transaction
/// (consistent snapshot). The subject→data mapping is application knowledge — callers
/// pass document refs and event selectors.
/// </summary>
/// <remarks>
/// Policy minimization (ADR-007) applies automatically: diffs and event payloads are
/// stored policy-applied, so redacted values appear only as change markers — also in
/// the export. The current document state, however, is the stored truth in plain form.
/// </remarks>
public static class GdprExport
{
    /// <summary>
    /// Exports the given documents (state + history) and matching events of one scope
    /// as a structured JSON object.
    /// </summary>
    /// <param name="store">The document store.</param>
    /// <param name="scope">The scope to export from (per-tenant execution, ADR-015).</param>
    /// <param name="documents">The document references to export.</param>
    /// <param name="events">Optional event selectors (payload path = value).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ArgumentException">A referenced document or event type is not registered.</exception>
    public static async Task<JsonObject> ExportAsync(
        DocumentStore store,
        ScopeContext scope,
        IReadOnlyCollection<DocumentRef> documents,
        IReadOnlyCollection<EventSelector>? events = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(documents);

        // Resolve all type names up front — typos fail fast, before any I/O.
        var documentTypes = store.Model.DocumentTypes.ToDictionary(m => m.Name, StringComparer.Ordinal);
        foreach (var reference in documents)
        {
            if (!documentTypes.ContainsKey(reference.DocumentType))
            {
                throw new ArgumentException(
                    $"Document type '{reference.DocumentType}' is not registered in the model.", nameof(documents));
            }
        }

        var eventTypes = store.Model.EventTypes.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var selector in events ?? [])
        {
            if (!eventTypes.Contains(selector.EventType))
            {
                throw new ArgumentException(
                    $"Event type '{selector.EventType}' is not registered in the model.", nameof(events));
            }
        }

        await using var conn = await store.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.SetScopeAsync(scope, ct);

        var export = new JsonObject
        {
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["scope"] = scope.Scope.ToString(),
            ["tenantId"] = scope.TenantId,
        };

        var documentsJson = new JsonArray();
        foreach (var reference in documents)
        {
            documentsJson.Add(await ExportDocumentAsync(conn, tx, scope, reference, ct));
        }

        export["documents"] = documentsJson;
        export["events"] = await ExportEventsAsync(conn, tx, scope, events ?? [], ct);

        await tx.RollbackAsync(ct); // read-only — nothing to commit
        return export;
    }

    private static async Task<JsonObject> ExportDocumentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, ScopeContext scope, DocumentRef reference,
        CancellationToken ct)
    {
        var json = new JsonObject
        {
            ["documentType"] = reference.DocumentType,
            ["id"] = reference.Id,
        };

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT data::text, version, schema_version, created_at, updated_at
                FROM papuma.document
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type AND id = @id
                """;
            AddIdentityParameters(cmd, scope, reference);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                json["exists"] = true;
                json["state"] = JsonNode.Parse(reader.GetString(0));
                json["version"] = reader.GetInt64(1);
                json["schemaVersion"] = reader.GetInt32(2);
                json["createdAt"] = reader.GetFieldValue<DateTimeOffset>(3).ToString("O");
                json["updatedAt"] = reader.GetFieldValue<DateTimeOffset>(4).ToString("O");
            }
            else
            {
                json["exists"] = false; // deleted (or never existed) — history below still matters
            }
        }

        var history = new JsonArray();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT version, operation, diff::text, metadata::text, occurred_at
                FROM papuma.change
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = @type AND document_id = @id
                ORDER BY version
                """;
            AddIdentityParameters(cmd, scope, reference);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                history.Add(new JsonObject
                {
                    ["version"] = reader.GetInt64(0),
                    ["operation"] = ((ChangeOperation)reader.GetInt16(1)).ToString(),
                    ["diff"] = JsonNode.Parse(reader.GetString(2)),
                    ["metadata"] = JsonNode.Parse(reader.GetString(3)),
                    ["occurredAt"] = reader.GetFieldValue<DateTimeOffset>(4).ToString("O"),
                });
            }
        }

        json["history"] = history;
        return json;
    }

    private static async Task<JsonArray> ExportEventsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, ScopeContext scope,
        IReadOnlyCollection<EventSelector> selectors, CancellationToken ct)
    {
        var events = new JsonArray();
        var seenSeqs = new HashSet<long>(); // overlapping selectors must not duplicate events

        foreach (var selector in selectors)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT seq, event_type, payload::text, metadata::text, occurred_at
                FROM papuma.event
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND event_type = @type
                  AND payload #>> @path = @value
                ORDER BY seq
                """;
            cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
            cmd.Parameters.AddWithValue("type", selector.EventType);
            cmd.Parameters.Add(new NpgsqlParameter("path", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = selector.PayloadPath.Split('.'),
            });
            cmd.Parameters.AddWithValue("value", selector.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!seenSeqs.Add(reader.GetInt64(0)))
                {
                    continue;
                }

                events.Add(new JsonObject
                {
                    ["eventType"] = reader.GetString(1),
                    ["payload"] = JsonNode.Parse(reader.GetString(2)),
                    ["metadata"] = JsonNode.Parse(reader.GetString(3)),
                    ["occurredAt"] = reader.GetFieldValue<DateTimeOffset>(4).ToString("O"),
                });
            }
        }

        return events;
    }

    private static void AddIdentityParameters(NpgsqlCommand cmd, ScopeContext scope, DocumentRef reference)
    {
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        cmd.Parameters.AddWithValue("type", reference.DocumentType);
        cmd.Parameters.AddWithValue("id", reference.Id);
    }
}
