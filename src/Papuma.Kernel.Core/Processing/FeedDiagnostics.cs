// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Papuma.Kernel.Diagnostics;

namespace Papuma.Kernel.Processing;

/// <summary>
/// Storage-neutral diagnostics plumbing shared by every feed engine (Postgres or
/// otherwise): handler spans with trace links, and error sanitization for the failure
/// table. The failure-table read/write API itself is storage-specific and lives next to
/// each kernel's own feed processor (e.g. <c>Papuma.Kernel.Processing.PostgresFeedDiagnostics</c>).
/// </summary>
internal static partial class FeedDiagnostics
{
    private const int MaxStoredErrorLength = 200;

    /// <summary>
    /// Produces the value stored in the failure table's <c>last_error</c> column from a
    /// handler exception (security review M1): the exception type plus a length-bounded,
    /// path-stripped message — never a stack trace. The full exception (incl. stack)
    /// is preserved by the processor's structured logger, which the consumer governs.
    /// </summary>
    public static string SanitizeError(Exception ex)
    {
        var message = FileSystemPath().Replace(ex.Message, "<path>");
        message = message.ReplaceLineEndings(" ").Trim();
        if (message.Length > MaxStoredErrorLength)
        {
            message = message[..MaxStoredErrorLength] + "…";
        }

        return $"{ex.GetType().Name}: {message}";
    }

    // Windows (C:\…, \\server\share) and POSIX (/usr/…) absolute paths — stripped so
    // file-system layout and incidentally-embedded data do not land in the feed table.
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\|/)[^\s""']*", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex FileSystemPath();

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
}
