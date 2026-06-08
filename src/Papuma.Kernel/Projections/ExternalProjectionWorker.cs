// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Polls the unified event feed and applies matching events to an external projection with at-least-once semantics.
/// The handler does not receive a database transaction; the checkpoint is committed separately after
/// the handler call succeeds.
/// </summary>
public sealed class ExternalProjectionWorker : ProjectionWorkerBase
{
    private readonly IExternalProjectionHandler _handler;

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> EventTypes => _handler.EventTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExternalProjectionWorker"/> class.
    /// </summary>
    /// <param name="handler">The external projection handler that processes events.</param>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">Optional worker configuration.</param>
    /// <param name="scopeFilter">Optional scope filter to isolate the worker to a specific scope.</param>
    public ExternalProjectionWorker(
        IExternalProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ExternalProjectionWorker> logger,
        ProjectionWorkerOptions? options = null,
        ScopeFilter? scopeFilter = null)
        : base(
            handlerName: (handler ?? throw new ArgumentNullException(nameof(handler))).Name,
            dataSource: dataSource ?? throw new ArgumentNullException(nameof(dataSource)),
            logger: logger ?? throw new ArgumentNullException(nameof(logger)),
            options: options ?? new ProjectionWorkerOptions(),
            scopeFilter: scopeFilter ?? ScopeFilter.All())
    {
        _handler = handler;
        ValidateOptions(options ?? new ProjectionWorkerOptions());
    }

    /// <inheritdoc />
    protected override async Task ProcessEventAsync(
        ChangeRecord change,
        NpgsqlConnection conn,
        CancellationToken ct)
    {
        try
        {
            await _handler.HandleAsync(change, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);
            await ClearFailureAsync(conn, tx, change.SequenceId, ct);
            await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await using var failTx = await conn.BeginTransactionAsync(ct);
            var movedToDeadLetter = await RegisterFailureAsync(conn, failTx, change, ex, ct);
            if (movedToDeadLetter)
            {
                await SaveCheckpointAsync(conn, failTx, change.SequenceId, ct);

                Logger.LogWarning(
                    ex,
                    "External projection entry {SequenceId} reached the maximum retry count and was dead-lettered.",
                    change.SequenceId);
            }
            await failTx.CommitAsync(ct);

            if (movedToDeadLetter)
            {
                return;
            }

            throw;
        }
    }
}
