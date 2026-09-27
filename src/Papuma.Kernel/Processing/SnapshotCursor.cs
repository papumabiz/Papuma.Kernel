// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using NpgsqlTypes;

namespace Papuma.Kernel.Processing;

/// <summary>
/// A feed handler's position as a transaction snapshot (ADR-022), shared by the change
/// and the event feed. A row is delivered when its transaction is visible in
/// <see cref="DoneSnapshot"/> — or, before the first completed slice, when its
/// <c>seq</c> is at most <see cref="BaseSeq"/> — or when it lies in the current slice at
/// or below <see cref="SliceSeq"/>.
/// </summary>
/// <param name="BaseSeq">Delivered prefix by <c>seq</c>, valid while <see cref="DoneSnapshot"/> is <c>null</c> (0 for a new or reset handler).</param>
/// <param name="DoneSnapshot">Every transaction visible in it is fully delivered; <c>null</c> = use <see cref="BaseSeq"/>.</param>
/// <param name="SliceSnapshot">The slice in progress: its target snapshot; <c>null</c> = none.</param>
/// <param name="SliceSeq">Within the slice, rows with <c>seq</c> at most this are delivered.</param>
/// <param name="LastSeq">Highest <c>seq</c> delivered so far — informational.</param>
internal sealed record SnapshotCursor(
    long BaseSeq,
    string? DoneSnapshot,
    string? SliceSnapshot,
    long SliceSeq,
    long LastSeq)
{
    /// <summary>
    /// Locks the handler's checkpoint row for this processor (leader coordination,
    /// <c>FOR UPDATE SKIP LOCKED</c>) and reads the cursor; <c>null</c> when another
    /// instance holds it or the handler is not registered yet.
    /// </summary>
    public static async Task<SnapshotCursor?> LockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT base_seq, done_snapshot::text, slice_snapshot::text, slice_seq, last_seq
            FROM papuma.checkpoint
            WHERE handler_name = @name
            FOR UPDATE SKIP LOCKED
            """;
        cmd.Parameters.AddWithValue("name", key);
        return await ReadAsync(cmd, ct);
    }

    /// <summary>Reads the cursor without locking (lag inspection).</summary>
    public static async Task<SnapshotCursor> ReadOrInitialAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT base_seq, done_snapshot::text, slice_snapshot::text, slice_seq, last_seq
            FROM papuma.checkpoint
            WHERE handler_name = @name
            """;
        cmd.Parameters.AddWithValue("name", key);
        return await ReadAsync(cmd, ct) ?? new SnapshotCursor(0, null, null, 0, 0);
    }

    /// <summary>
    /// Returns this cursor with a slice to work on: the one in progress, or a new one
    /// taken from the current snapshot. Under READ COMMITTED every later statement of the
    /// transaction sees at least what that snapshot shows as committed.
    /// </summary>
    public async Task<SnapshotCursor> WithSliceAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        if (SliceSnapshot is not null)
        {
            return this;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT pg_current_snapshot()::text";
        var snapshot = (string)(await cmd.ExecuteScalarAsync(ct))!;
        return this with { SliceSnapshot = snapshot, SliceSeq = 0 };
    }

    /// <summary>
    /// Adds the predicate selecting the slice's next undelivered rows of the table aliased
    /// <paramref name="alias"/> to <paramref name="cmd"/> and returns it. The slice's row
    /// set is fixed — all of its transactions have committed — so paginating it by
    /// <c>seq</c> neither skips nor repeats.
    /// </summary>
    public string SlicePredicate(NpgsqlCommand cmd, string alias)
    {
        if (SliceSnapshot is null)
        {
            throw new InvalidOperationException("No slice taken.");
        }

        cmd.Parameters.Add(Snapshot("cursorSlice", SliceSnapshot));
        if (DoneSnapshot is null)
        {
            // Before the first completed slice: the delivered prefix is a seq range (PK scan).
            cmd.Parameters.AddWithValue("cursorFloor", Math.Max(BaseSeq, SliceSeq));
            return $"""
                {alias}.seq > @cursorFloor
                  AND pg_visible_in_snapshot({alias}.txid, @cursorSlice::pg_snapshot)
                """;
        }

        // Not visible in done ⇒ txid ≥ xmin(done); visible in the slice ⇒ txid < xmax(slice):
        // an index range on txid.
        cmd.Parameters.Add(Snapshot("cursorDone", DoneSnapshot));
        cmd.Parameters.AddWithValue("cursorSliceSeq", SliceSeq);
        return $"""
            {alias}.txid >= pg_snapshot_xmin(@cursorDone::pg_snapshot)
              AND {alias}.txid < pg_snapshot_xmax(@cursorSlice::pg_snapshot)
              AND pg_visible_in_snapshot({alias}.txid, @cursorSlice::pg_snapshot)
              AND NOT pg_visible_in_snapshot({alias}.txid, @cursorDone::pg_snapshot)
              AND {alias}.seq > @cursorSliceSeq
            """;
    }

    /// <summary>
    /// Adds the predicate selecting every committed row this cursor has not delivered —
    /// the lag, counted — and returns it.
    /// </summary>
    public string UndeliveredPredicate(NpgsqlCommand cmd, string alias)
    {
        var predicate = $"pg_visible_in_snapshot({alias}.txid, pg_current_snapshot())";
        if (DoneSnapshot is null)
        {
            cmd.Parameters.AddWithValue("lagBase", BaseSeq);
            predicate += $" AND {alias}.seq > @lagBase";
        }
        else
        {
            cmd.Parameters.Add(Snapshot("lagDone", DoneSnapshot));
            predicate += $"""
                 AND {alias}.txid >= pg_snapshot_xmin(@lagDone::pg_snapshot)
                 AND NOT pg_visible_in_snapshot({alias}.txid, @lagDone::pg_snapshot)
                """;
        }

        if (SliceSnapshot is not null && SliceSeq > 0)
        {
            cmd.Parameters.Add(Snapshot("lagSlice", SliceSnapshot));
            cmd.Parameters.AddWithValue("lagSliceSeq", SliceSeq);
            predicate += $"""
                 AND NOT (pg_visible_in_snapshot({alias}.txid, @lagSlice::pg_snapshot)
                          AND {alias}.seq <= @lagSliceSeq)
                """;
        }

        return predicate;
    }

    /// <summary>
    /// The cursor after a batch: the slice completed (becomes the done snapshot) or its
    /// position moved to <paramref name="position"/>.
    /// </summary>
    public SnapshotCursor After(long position, long highestDelivered, bool sliceCompleted) =>
        sliceCompleted
            ? this with
            {
                DoneSnapshot = SliceSnapshot,
                SliceSnapshot = null,
                SliceSeq = 0,
                LastSeq = Math.Max(LastSeq, highestDelivered),
            }
            : this with { SliceSeq = position, LastSeq = Math.Max(LastSeq, highestDelivered) };

    /// <summary>Persists the cursor in the (locked) checkpoint row.</summary>
    public async Task SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE papuma.checkpoint
            SET base_seq = @base,
                done_snapshot = @done::pg_snapshot,
                slice_snapshot = @slice::pg_snapshot,
                slice_seq = @sliceSeq,
                last_seq = @last,
                updated_at = now()
            WHERE handler_name = @name
            """;
        cmd.Parameters.AddWithValue("name", key);
        cmd.Parameters.AddWithValue("base", BaseSeq);
        cmd.Parameters.Add(Snapshot("done", DoneSnapshot));
        cmd.Parameters.Add(Snapshot("slice", SliceSnapshot));
        cmd.Parameters.AddWithValue("sliceSeq", SliceSeq);
        cmd.Parameters.AddWithValue("last", LastSeq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The statement resetting a handler to "nothing delivered" (full replay), upserting
    /// its checkpoint row; parameter <c>@name</c>.
    /// </summary>
    public const string ResetSql = """
        INSERT INTO papuma.checkpoint (handler_name, last_seq, base_seq, slice_seq, updated_at)
        VALUES (@name, 0, 0, 0, now())
        ON CONFLICT (handler_name) DO UPDATE
            SET last_seq = 0, base_seq = 0, done_snapshot = NULL, slice_snapshot = NULL,
                slice_seq = 0, updated_at = now()
        """;

    private static async Task<SnapshotCursor?> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new SnapshotCursor(
            BaseSeq: reader.GetInt64(0),
            DoneSnapshot: reader.IsDBNull(1) ? null : reader.GetString(1),
            SliceSnapshot: reader.IsDBNull(2) ? null : reader.GetString(2),
            SliceSeq: reader.GetInt64(3),
            LastSeq: reader.GetInt64(4));
    }

    private static NpgsqlParameter Snapshot(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
