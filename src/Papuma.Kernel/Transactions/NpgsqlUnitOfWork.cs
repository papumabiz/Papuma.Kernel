// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Transactions;

using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// Executes database work in a transaction and retries transient PostgreSQL failures.
/// </summary>
public sealed class NpgsqlUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly UnitOfWorkOptions _options;
    private readonly ILogger<NpgsqlUnitOfWork>? _logger;

    // PostgreSQL error codes that are transient and justify a retry.
    private static readonly HashSet<string> TransientSqlStates = new()
    {
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "08006", // connection_failure
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "57P03", // cannot_connect_now
    };

    /// <summary>
    /// Creates a new instance of <see cref="NpgsqlUnitOfWork"/>.
    /// </summary>
    /// <param name="dataSource">Data source used to open connections.</param>
    /// <param name="options">Retry options. Defaults are used when null.</param>
    /// <param name="logger">Optional logger for retry diagnostics.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dataSource"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when options contain invalid values.</exception>
    public NpgsqlUnitOfWork(
        NpgsqlDataSource dataSource,
        UnitOfWorkOptions? options = null,
        ILogger<NpgsqlUnitOfWork>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _options = options ?? new UnitOfWorkOptions();

        if (_options.MaxRetries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must be greater than or equal to 1.");
        }

        if (_options.BaseRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BaseRetryDelay must not be negative.");
        }

        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (int attempt = 1; ; attempt++)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                await action(conn, tx, ct);
                await tx.CommitAsync(ct);

                return; // Success
            }
            catch (Exception ex) when (
                attempt < _options.MaxRetries &&
                IsTransient(ex))
            {
                try { await tx.RollbackAsync(ct); }
                catch (Exception rollbackEx)
                {
                    _logger?.LogDebug(rollbackEx, "Rollback failed after transient error (connection may already be broken).");
                }

                var exponentialBackoffFactor = Math.Pow(2, attempt - 1);
                var delay = TimeSpan.FromMilliseconds(_options.BaseRetryDelay.TotalMilliseconds * exponentialBackoffFactor);
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 50));

                _logger?.LogWarning(
                    "Transient DB error (attempt {Attempt}/{Max}), retrying in {Delay}ms.",
                    attempt, _options.MaxRetries, (delay + jitter).TotalMilliseconds);

                await Task.Delay(delay + jitter, ct);
            }
            catch
            {
                try { await tx.RollbackAsync(ct); }
                catch (Exception rollbackEx)
                {
                    _logger?.LogDebug(rollbackEx, "Rollback failed (connection may already be broken).");
                }

                throw;
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is NpgsqlException npgsqlEx && npgsqlEx.SqlState is not null)
        {
            return TransientSqlStates.Contains(npgsqlEx.SqlState);
        }

        if (ex is TimeoutException)
        {
            return true;
        }

        if (ex is IOException)
        {
            return true;
        }

        return false;
    }
}
