// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Npgsql;

using Papuma.Kernel.ChangeFeed;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Projections;

/// <summary>
/// Polls the unified event feed and applies matching events to a projection with at-least-once semantics.
/// The handler receives the ambient <see cref="NpgsqlTransaction"/> so that its writes and the
/// checkpoint update are committed atomically.
/// </summary>
public sealed class ProjectionWorker : ProjectionWorkerBase
{
    private readonly IProjectionHandler _handler;
    private readonly Channel<ReplayRequest> _replayChannel = Channel.CreateBounded<ReplayRequest>(1);

    /// <inheritdoc />
    protected override IReadOnlyCollection<string> EventTypes => _handler.EventTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectionWorker"/> class.
    /// </summary>
    /// <param name="handler">The projection handler that processes events.</param>
    /// <param name="dataSource">The PostgreSQL data source.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="options">Optional worker configuration.</param>
    /// <param name="scopeFilter">Optional scope filter to isolate the worker to a specific scope.</param>
    public ProjectionWorker(
        IProjectionHandler handler,
        NpgsqlDataSource dataSource,
        ILogger<ProjectionWorker> logger,
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

    /// <summary>
    /// Requests a full replay of this projection by resetting the checkpoint to 0.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public async Task RequestReplayAsync(CancellationToken ct = default)
    {
        await _replayChannel.Writer.WriteAsync(new ReplayRequest(), ct);
    }

    /// <inheritdoc />
    protected override bool TryDequeueReplayRequest() =>
        _replayChannel.Reader.TryRead(out _);

    /// <inheritdoc />
    protected override async Task OnReplayRequestedAsync(CancellationToken ct)
    {
        if (_handler is IReplayableProjection replayable)
        {
            await replayable.PrepareReplayAsync(ct);
        }
    }

    /// <inheritdoc />
    protected override async Task ProcessEventAsync(
        ChangeRecord change,
        NpgsqlConnection conn,
        CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await _handler.HandleAsync(change, conn, tx, ct);
            await ClearFailureAsync(conn, tx, change.SequenceId, ct);
            await SaveCheckpointAsync(conn, tx, change.SequenceId, ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);

            await using var failTx = await conn.BeginTransactionAsync(ct);
            var movedToDeadLetter = await RegisterFailureAsync(conn, failTx, change, ex, ct);
            if (movedToDeadLetter)
            {
                await SaveCheckpointAsync(conn, failTx, change.SequenceId, ct);
            }
            await failTx.CommitAsync(ct);

            if (movedToDeadLetter)
            {
                return;
            }

            throw;
        }
    }

    private sealed record ReplayRequest;
}
