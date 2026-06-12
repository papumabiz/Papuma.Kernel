// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Diagnostics;
using System.Text.Json.Nodes;

using Npgsql;

using Papuma.Kernel.Diagnostics;

namespace Papuma.Kernel.Processing;

/// <summary>
/// Shared diagnostics plumbing of the two feed engines (phase 11): handler spans with
/// trace links, and the failure table as an API.
/// </summary>
internal static class FeedDiagnostics
{
    /// <summary>
    /// Starts a handler span. When the record's metadata carries a <c>traceparent</c>
    /// (written by the session, phase 11), the span links to the originating trace —
    /// a link rather than a parent, because feed processing is asynchronous batch work.
    /// </summary>
    public static Activity? StartHandlerActivity(string feed, string handlerName, long seq, JsonObject metadata)
    {
        IEnumerable<ActivityLink>? links = null;
        if (metadata["traceparent"] is JsonValue value
            && value.TryGetValue<string>(out var traceparent)
            && ActivityContext.TryParse(traceparent, null, out var context))
        {
            links = [new ActivityLink(context)];
        }

        return KernelDiagnostics.ActivitySource.StartActivity(
            "papuma.feed.handle",
            ActivityKind.Internal,
            parentContext: default,
            tags:
            [
                new KeyValuePair<string, object?>("papuma.feed", feed),
                new KeyValuePair<string, object?>("papuma.handler", handlerName),
                new KeyValuePair<string, object?>("papuma.seq", seq),
            ],
            links: links);
    }

    /// <summary>
    /// Reads the failure entries of the given handlers, mapping internal checkpoint
    /// prefixes back to public handler names.
    /// </summary>
    public static async Task<IReadOnlyList<FeedFailure>> GetFailuresAsync(
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

        var failures = new List<FeedFailure>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            failures.Add(new FeedFailure(
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
