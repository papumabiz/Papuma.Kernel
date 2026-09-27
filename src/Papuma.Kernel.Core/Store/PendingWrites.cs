// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// The uncommitted writes of a session — what a dispose without commit would silently
/// roll back (feedback F-18). Shared by both kernels' sessions.
/// </summary>
internal sealed class PendingWrites
{
    /// <summary>The event id of the "disposed with uncommitted writes" warning.</summary>
    public const int UncommittedDisposalEventId = 1001;

    /// <summary>The event name of the "disposed with uncommitted writes" warning.</summary>
    public const string UncommittedDisposalEventName = "UncommittedSessionDisposed";

    /// <summary>The warning's message template, shared so both kernels log the same shape.</summary>
    public const string UncommittedDisposalMessage =
        "Session {CorrelationId} was disposed with {Count} uncommitted write(s), the first to {FirstWrite}; " +
        "they were rolled back. Call CommitAsync to keep them, or DiscardAsync to drop them deliberately.";

    /// <summary>Gets the number of writes since the last commit or discard.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the target of the first of them (<c>Type/id</c>, or the event type).</summary>
    public string? First { get; private set; }

    /// <summary>Records <paramref name="count"/> writes to <paramref name="target"/>.</summary>
    public void Add(string target, int count = 1)
    {
        if (count <= 0)
        {
            return;
        }

        First ??= target;
        Count += count;
    }

    /// <summary>Forgets the writes — they were committed or discarded.</summary>
    public void Clear()
    {
        Count = 0;
        First = null;
    }
}
