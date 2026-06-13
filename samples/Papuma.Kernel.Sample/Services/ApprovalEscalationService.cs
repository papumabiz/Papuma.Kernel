// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

using SessionOptions = Papuma.Kernel.Store.SessionOptions;

namespace Papuma.Kernel.Sample.Services;

/// <summary>
/// The timer primitive of the workflow recipe (concepts §18): the kernel has
/// deliberately no scheduler, so "escalate when nobody decides in time" is a small
/// hosted service that polls due instances and turns "time passed" into a normal
/// write — from there the ordinary handler chain takes over.
/// </summary>
/// <remarks>
/// The due query is a read-only SQL lens over the document store (sanctioned with
/// the caveats of concepts §16): explicit scope predicates, no writes. The
/// escalation itself goes through the session — patch with <c>expectedVersion</c>,
/// so a human decision that lands first wins and the poller backs off typed.
/// For multi-instance deployments, put the poll under a leader lock
/// (<c>FOR UPDATE SKIP LOCKED</c> on a coordination row, concepts §11) — omitted
/// here because the sample runs as a single instance.
/// </remarks>
public sealed class ApprovalEscalationService(
    NpgsqlDataSource dataSource,
    DocumentStore store,
    ILogger<ApprovalEscalationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EscalateDueTasksAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Escalation poll failed; retrying next cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task EscalateDueTasksAsync(CancellationToken ct)
    {
        var scope = SampleScope.Tenant;

        // 1. Find due instances — read-only, index-friendly, scope-bound.
        var due = new List<(string Id, long Version)>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.SetScopeAsync(tx, scope, ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, version
                FROM papuma.document
                WHERE scope = @scope AND tenant_id = @tenantId
                  AND document_type = 'ApprovalTask'
                  AND data ->> 'status' = 'Pending'
                  AND (data ->> 'dueAt')::timestamptz < now()
                """;
            cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
            cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        // 2. Turn "time passed" into normal writes — one session per task so one
        //    conflict doesn't block the others.
        foreach (var (taskId, version) in due)
        {
            await using var session = store.OpenSession(scope,
                new SessionOptions { ActorId = "escalation-poller" });
            try
            {
                await session.PatchAsync<ApprovalTask>(taskId,
                    p => p.Set(x => x.Status, ApprovalStatus.Escalated),
                    expectedVersion: version, ct);
                await session.CommitAsync(ct);
                logger.LogWarning("Approval task {TaskId} escalated (no decision before dueAt).", taskId);
            }
            catch (ConcurrencyException)
            {
                // A human decided between our read and our write — they win, we stand down.
            }
        }
    }
}
