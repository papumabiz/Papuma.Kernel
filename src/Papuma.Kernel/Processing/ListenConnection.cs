// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.Logging;

using Npgsql;

namespace Papuma.Kernel.Processing;

/// <summary>
/// The dedicated <c>LISTEN</c> connection of a feed processor's idle wait. NOTIFY is only
/// a wakeup — polling is the source of truth (ADR-010) — so losing the connection
/// (failover, server restart, a pooler or admin killing it) must not stop or spin the
/// loop: the wait degrades to a plain sleep and the connection is re-established on the
/// next idle wait.
/// </summary>
internal sealed class ListenConnection(NpgsqlDataSource dataSource, ILogger logger) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;

    /// <summary>Opens the connection if it is not open; a failure is logged, never thrown.</summary>
    public async Task TryOpenAsync(CancellationToken ct)
    {
        try
        {
            await EnsureOpenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DropAsync();
            logger.LogWarning(ex, "LISTEN connection unavailable; polling only until it is re-established.");
        }
    }

    /// <summary>
    /// Waits for a NOTIFY or until <paramref name="timeout"/> elapses. When the connection
    /// is broken, sleeps the full timeout instead (then retries next time).
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await EnsureOpenAsync(ct);
            await _connection!.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DropAsync();
            logger.LogWarning(ex, "LISTEN connection lost; polling only until it is re-established.");
            await Task.Delay(timeout, ct);
        }
    }

    public ValueTask DisposeAsync() => DropAsync();

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection is { FullState: System.Data.ConnectionState.Open })
        {
            return;
        }

        await DropAsync();
        var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"LISTEN {ChangeFeedProcessor.NotifyChannel}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        _connection = connection;
    }

    private async ValueTask DropAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }
}
