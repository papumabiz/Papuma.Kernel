// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.ComponentModel;
using System.Text.Json.Nodes;

using ModelContextProtocol.Server;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Mcp;

/// <summary>
/// MCP tools over the kernel's diagnostics surface (phase 13, ADR-015 posture):
/// thin wrappers around the phase-11/12 APIs — no own diagnostic logic, read-only
/// by default, every data-reading tool is scope-bound.
/// </summary>
/// <remarks>
/// Privacy: document history is policy-applied (ADR-007) before it leaves the store,
/// the inventory is pure metadata, and there is deliberately no GDPR-export tool —
/// a subject export is an application workflow, not an agent capability.
/// </remarks>
[McpServerToolType]
public sealed class PapumaKernelTools
{
    private readonly DocumentStore _store;
    private readonly ChangeFeedProcessor _changeProcessor;
    private readonly EventFeedProcessor _eventProcessor;
    private readonly PapumaMcpOptions _options;

    /// <summary>
    /// Initializes the tools over the application's kernel services.
    /// </summary>
    /// <param name="store">The document store (carries the model).</param>
    /// <param name="changeProcessor">The change feed processor.</param>
    /// <param name="eventProcessor">The event feed processor.</param>
    /// <param name="options">The MCP options; read-only defaults when omitted.</param>
    public PapumaKernelTools(
        DocumentStore store,
        ChangeFeedProcessor changeProcessor,
        EventFeedProcessor eventProcessor,
        PapumaMcpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changeProcessor);
        ArgumentNullException.ThrowIfNull(eventProcessor);
        _store = store;
        _changeProcessor = changeProcessor;
        _eventProcessor = eventProcessor;
        _options = options ?? new PapumaMcpOptions();
    }

    [McpServerTool(Name = "get_model_inventory", ReadOnly = true)]
    [Description("Returns the registered document and event types with all field paths, " +
        "effective privacy policies, keys, schema versions and event retentions " +
        "(the Art.-30 data inventory). Pure metadata — no tenant data is read.")]
    public string GetModelInventory() =>
        DataInventory.Build(_store.Model).ToJson().ToJsonString();

    [McpServerTool(Name = "get_feed_lag", ReadOnly = true)]
    [Description("Returns the current lag (stable feed head minus checkpoint) per handler " +
        "for both the change feed and the event feed.")]
    public async Task<string> GetFeedLagAsync(CancellationToken ct = default)
    {
        var json = new JsonObject
        {
            ["changeFeed"] = LagToJson(await _changeProcessor.GetLagAsync(ct)),
            ["eventFeed"] = LagToJson(await _eventProcessor.GetLagAsync(ct)),
        };
        return json.ToJsonString();
    }

    [McpServerTool(Name = "get_feed_failures", ReadOnly = true)]
    [Description("Returns the failure table of both feeds: handler name, sequence, " +
        "attempts, last error, next retry time. Entries that exhausted their retries " +
        "are poison records — the record was skipped and waits for manual retry.")]
    public async Task<string> GetFeedFailuresAsync(CancellationToken ct = default)
    {
        var json = new JsonObject
        {
            ["changeFeed"] = FailuresToJson(await _changeProcessor.GetFailuresAsync(ct)),
            ["eventFeed"] = FailuresToJson(await _eventProcessor.GetFailuresAsync(ct)),
        };
        return json.ToJsonString();
    }

    [McpServerTool(Name = "get_document_history", ReadOnly = true)]
    [Description("Returns the change history of one document in ascending version order: " +
        "operation, field diff, metadata (actor/correlation), timestamp. Diffs are " +
        "policy-applied — values of protected fields never appear. Scope-bound: " +
        "pass tenantId for tenant data, omit it for platform documents.")]
    public async Task<string> GetDocumentHistoryAsync(
        [Description("The logical document type name as registered in the model.")] string documentType,
        [Description("The document identifier.")] string documentId,
        [Description("The tenant id; omit for platform-scoped documents.")] string? tenantId = null,
        [Description("Inclusive lower version bound.")] long? fromVersion = null,
        [Description("Inclusive upper version bound.")] long? toVersion = null,
        CancellationToken ct = default)
    {
        var metadata = _store.Model.DocumentTypes.FirstOrDefault(m => m.Name == documentType)
            ?? throw new ArgumentException(
                $"Document type '{documentType}' is not registered. Use get_model_inventory to list types.");

        var scope = tenantId is null ? ScopeContext.Platform() : ScopeContext.Tenant(tenantId);
        await using var session = _store.OpenSession(scope);
        var history = await InvokeHistoryAsync(session, metadata.ClrType, documentId, fromVersion, toVersion, ct);

        var json = new JsonArray();
        foreach (var change in history)
        {
            json.Add(new JsonObject
            {
                ["version"] = change.Version,
                ["operation"] = change.Operation.ToString(),
                ["schemaVersion"] = change.SchemaVersion,
                ["diff"] = change.Diff.ToJson(),
                ["metadata"] = change.Metadata.DeepClone(),
                ["occurredAt"] = change.OccurredAt.ToString("O"),
            });
        }

        return json.ToJsonString();
    }

    [McpServerTool(Name = "get_document", ReadOnly = true)]
    [Description("Loads the current state of one document by id, with privacy policies " +
        "applied: sensitive fields are masked, hashed or omitted — never clear text " +
        "(ADR-016). Scope-bound (pass tenantId for tenant data). Only document types " +
        "opted in via ExposeToMcp() are readable; use get_model_inventory to discover types.")]
    public async Task<string> GetDocumentAsync(
        [Description("The logical document type name as registered in the model.")] string documentType,
        [Description("The document identifier.")] string documentId,
        [Description("The tenant id; omit for platform-scoped documents.")] string? tenantId = null,
        CancellationToken ct = default)
    {
        var scope = tenantId is null ? ScopeContext.Platform() : ScopeContext.Tenant(tenantId);
        await using var session = _store.OpenSession(scope);
        var result = await session.LoadMaskedForMcpAsync(documentType, documentId, ct);
        return MaskedToJson(result);
    }

    [McpServerTool(Name = "get_document_by_key", ReadOnly = true)]
    [Description("Loads one document by a declared key (ADR-006), policy-masked like " +
        "get_document. The key path must be a declared key — there is no free-form " +
        "query. Scope-bound; only ExposeToMcp() types are readable.")]
    public async Task<string> GetDocumentByKeyAsync(
        [Description("The logical document type name.")] string documentType,
        [Description("The declared key path (e.g. 'email').")] string keyPath,
        [Description("The key value to match.")] string value,
        [Description("The tenant id; omit for platform-scoped documents.")] string? tenantId = null,
        CancellationToken ct = default)
    {
        var scope = tenantId is null ? ScopeContext.Platform() : ScopeContext.Tenant(tenantId);
        await using var session = _store.OpenSession(scope);
        var result = await session.LoadMaskedByKeyForMcpAsync(documentType, keyPath, value, ct);
        return MaskedToJson(result);
    }

    [McpServerTool(Name = "retry_feed_failure", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Clears one failure/poison entry so the record is retried. Requires " +
        "AllowMutations. If the checkpoint already passed the sequence, additionally " +
        "reset the checkpoint for a replay.")]
    public async Task<string> RetryFeedFailureAsync(
        [Description("Which feed: 'change' or 'event'.")] string feed,
        [Description("The handler name as listed by get_feed_failures.")] string handlerName,
        [Description("The failed sequence number.")] long seq,
        CancellationToken ct = default)
    {
        EnsureMutationsAllowed();
        var removed = feed switch
        {
            "change" => await _changeProcessor.RetryFailureAsync(handlerName, seq, ct),
            "event" => await _eventProcessor.RetryFailureAsync(handlerName, seq, ct),
            _ => throw new ArgumentException("feed must be 'change' or 'event'."),
        };
        return removed
            ? $"Failure entry for {handlerName}@{seq} removed — the record will be retried."
            : $"No failure entry for {handlerName}@{seq} found.";
    }

    [McpServerTool(Name = "reset_feed_checkpoint", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Resets a handler's checkpoint to 0 for a full replay (projection " +
        "rebuild). Requires AllowMutations. ONLY for projections — never reset an " +
        "effect handler (it would repeat its side effects, e.g. resend emails).")]
    public async Task<string> ResetFeedCheckpointAsync(
        [Description("Which feed: 'change' or 'event'.")] string feed,
        [Description("The handler name.")] string handlerName,
        CancellationToken ct = default)
    {
        EnsureMutationsAllowed();
        switch (feed)
        {
            case "change":
                await _changeProcessor.ResetCheckpointAsync(handlerName, ct);
                break;
            case "event":
                await _eventProcessor.ResetCheckpointAsync(handlerName, ct);
                break;
            default:
                throw new ArgumentException("feed must be 'change' or 'event'.");
        }

        return $"Checkpoint for {handlerName} reset — the handler replays from the beginning.";
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private static string MaskedToJson(MaskedDocumentResult? result) =>
        result is null
            ? "null"
            : new JsonObject
            {
                ["version"] = result.Version,
                ["document"] = result.Document.DeepClone(),
            }.ToJsonString();

    private void EnsureMutationsAllowed()
    {
        if (!_options.AllowMutations)
        {
            throw new InvalidOperationException(
                "Mutating tools are disabled (read-only default). Enable them explicitly " +
                "via PapumaMcpOptions { AllowMutations = true } when registering the MCP tools.");
        }
    }

    /// <summary>
    /// Bridges the type-name world of MCP to the generic <c>GetHistoryAsync&lt;T&gt;</c>
    /// via reflection — acceptable for a diagnostics tool, keeps the kernel API typed.
    /// </summary>
    private static async Task<IReadOnlyList<ChangeRecord>> InvokeHistoryAsync(
        DocumentSession session, Type clrType, string id, long? fromVersion, long? toVersion,
        CancellationToken ct)
    {
        var method = typeof(DocumentSession)
            .GetMethod(nameof(DocumentSession.GetHistoryAsync))!
            .MakeGenericMethod(clrType);
        var task = (Task<IReadOnlyList<ChangeRecord>>)method.Invoke(
            session, [id, fromVersion, toVersion, ct])!;
        return await task;
    }

    private static JsonArray LagToJson(IReadOnlyList<ChangeFeedLagSnapshot> snapshots)
    {
        var json = new JsonArray();
        foreach (var snapshot in snapshots)
        {
            json.Add(new JsonObject
            {
                ["handler"] = snapshot.HandlerName,
                ["checkpoint"] = snapshot.Checkpoint,
                ["latestSeq"] = snapshot.LatestSeq,
                ["lag"] = snapshot.Lag,
            });
        }

        return json;
    }

    private static JsonArray FailuresToJson(IReadOnlyList<FeedFailure> failures)
    {
        var json = new JsonArray();
        foreach (var failure in failures)
        {
            json.Add(new JsonObject
            {
                ["handler"] = failure.HandlerName,
                ["seq"] = failure.Seq,
                ["attempts"] = failure.Attempts,
                ["lastError"] = failure.LastError,
                ["nextRetryAt"] = failure.NextRetryAt.ToString("O"),
                ["updatedAt"] = failure.UpdatedAt.ToString("O"),
            });
        }

        return json;
    }
}
