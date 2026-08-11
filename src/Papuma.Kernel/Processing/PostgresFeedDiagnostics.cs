// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

namespace Papuma.Kernel.Processing;

/// <summary>
/// Postgres-specific half of the feed diagnostics plumbing: the failure table as an API.
/// The storage-neutral half (handler spans, error sanitization) lives in
/// <see cref="FeedDiagnostics"/> (Papuma.Kernel.Core).
/// </summary>
internal static class PostgresFeedDiagnostics
{
    /// <summary>
    /// Reads the failure entries of the given handlers, mapping internal checkpoint
    /// prefixes back to public handler names.
    /// </summary>
    public static async Task<IReadOnlyList<Papuma.Kernel.Processing.FeedFailure>> GetFailuresAsync(
        NpgsqlDataSource dataSource,
        IEnumerable<string> handlerNames,
        string prefix,
        CancellationToken ct)
    {
        var keys = handlerNames.Select(name => prefix + name).ToArray();

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT handler_name, seq, attempts, last_error, next_retry_at, updated_at
            FROM papuma.failure
            WHERE handler_name = ANY(@names)
            ORDER BY handler_name, seq
            """;
        cmd.Parameters.AddWithValue("names", keys);

        var failures = new List<Papuma.Kernel.Processing.FeedFailure>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new Papuma.Kernel.Processing.FeedFailure(
                HandlerName: reader.GetString(0)[prefix.Length..],
                Seq: reader.GetInt64(1),
                Attempts: reader.GetInt32(2),
                LastError: reader.GetString(3),
                NextRetryAt: reader.GetFieldValue<DateTimeOffset>(4),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return failures;
    }

    /// <summary>
    /// Removes one failure entry (manual retry after fixing the cause).
    /// </summary>
    public static async Task<bool> RetryFailureAsync(
        NpgsqlDataSource dataSource,
        string handlerName,
        string prefix,
        long seq,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerName);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM papuma.failure WHERE handler_name = @name AND seq = @seq";
        cmd.Parameters.AddWithValue("name", prefix + handlerName);
        cmd.Parameters.AddWithValue("seq", seq);

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
