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
    /// taken from the current snapshot — or unchanged, without a slice, when nothing has
    /// committed since <see cref="DoneSnapshot"/>. Under READ COMMITTED every later
    /// statement of the transaction sees at least what the snapshot shows as committed.
    /// </summary>
    /// <param name="conn">The connection.</param>
    /// <param name="tx">The processor's transaction.</param>
    /// <param name="table">The feed table (<c>papuma.change</c> or <c>papuma.event</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<SnapshotCursor> WithSliceAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        if (SliceSnapshot is not null)
        {
            return this;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (DoneSnapshot is null)
        {
            // Before the first completed slice the seq floor bounds the PK scan already.
            cmd.CommandText = "SELECT pg_current_snapshot()::text";
            var snapshot = (string)(await cmd.ExecuteScalarAsync(ct))!;
            return this with { SliceSnapshot = snapshot, SliceSeq = 0 };
        }

        // The slice's lowest seq, found once through the txid index. Batches then page
        // the PK forward from just below it, each row read once: a txid-index read per
        // batch would re-read the whole slice every time — quadratic in its size.
        // MATERIALIZED keeps min() from being rewritten into a PK-ordered scan that
        // filters the whole table.
        cmd.CommandText = $"""
            WITH s AS MATERIALIZED (SELECT pg_current_snapshot() AS slice),
                 r AS MATERIALIZED (
                     SELECT c.seq FROM {table} c, s
                     WHERE {AfterXmax("c", "cursorDone")} AND pg_visible_in_snapshot(c.txid, s.slice)
                     UNION ALL
                     SELECT c.seq FROM {table} c, s
                     WHERE {InProgressAt("c", "cursorDone")} AND pg_visible_in_snapshot(c.txid, s.slice))
            SELECT (SELECT slice::text FROM s), (SELECT min(seq) FROM r)
            """;
        cmd.Parameters.Add(Snapshot("cursorDone", DoneSnapshot));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var slice = reader.GetString(0);
        return reader.IsDBNull(1)
            ? this // nothing committed since the done snapshot
            : this with { SliceSnapshot = slice, SliceSeq = reader.GetInt64(1) - 1 };
    }

    /// <summary>
    /// Adds the predicate selecting the slice's next undelivered rows of the table aliased
    /// <paramref name="alias"/> to <paramref name="cmd"/> and returns it. The slice's row
    /// set is fixed — all of its transactions have committed — so paginating it by
    /// <c>seq</c> neither skips nor repeats. Both forms are a forward PK range scan.
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

        // SliceSeq starts just below the slice's lowest seq (WithSliceAsync). No txid
        // bounds on purpose: they would invite the txid index, which cannot deliver in
        // seq order and re-reads the whole slice per batch.
        cmd.Parameters.Add(Snapshot("cursorDone", DoneSnapshot));
        cmd.Parameters.AddWithValue("cursorSliceSeq", SliceSeq);
        return $"""
            {alias}.seq > @cursorSliceSeq
              AND pg_visible_in_snapshot({alias}.txid, @cursorSlice::pg_snapshot)
              AND NOT pg_visible_in_snapshot({alias}.txid, @cursorDone::pg_snapshot)
            """;
    }

    /// <summary>
    /// Counts the committed rows of <paramref name="table"/> this cursor has not
    /// delivered — the lag (ADR-022). Rows of still-open transactions are not counted.
    /// </summary>
    /// <param name="conn">The connection.</param>
    /// <param name="tx">The transaction.</param>
    /// <param name="table">The feed table (<c>papuma.change</c> or <c>papuma.event</c>).</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<long> CountUndeliveredAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var committed = "pg_visible_in_snapshot(r.txid, pg_current_snapshot())";
        if (SliceSnapshot is not null)
        {
            // Rows of the slice in progress at or below its position are delivered.
            cmd.Parameters.Add(Snapshot("lagSlice", SliceSnapshot));
            cmd.Parameters.AddWithValue("lagSliceSeq", SliceSeq);
            committed += " AND NOT (pg_visible_in_snapshot(r.txid, @lagSlice::pg_snapshot) AND r.seq <= @lagSliceSeq)";
        }

        if (DoneSnapshot is null)
        {
            cmd.Parameters.AddWithValue("lagBase", BaseSeq);
            cmd.CommandText = $"SELECT count(*) FROM {table} r WHERE r.seq > @lagBase AND {committed}";
        }
        else
        {
            cmd.Parameters.Add(Snapshot("lagDone", DoneSnapshot));
            cmd.CommandText = $"""
                SELECT (SELECT count(*) FROM {table} r WHERE {AfterXmax("r", "lagDone")} AND {committed})
                     + (SELECT count(*) FROM {table} r WHERE {InProgressAt("r", "lagDone")} AND {committed})
                """;
        }

        return (long)(await cmd.ExecuteScalarAsync(ct))!;
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

    // "Not visible in a snapshot" is exactly: at or beyond its xmax, or in progress when it
    // was taken. Written as two disjoint parts, each a plain txid index condition that
    // generic plans keep too. The equivalent "txid >= xmin AND NOT visible" would scan
    // everything since the oldest transaction that was open at the time.
    private static string AfterXmax(string alias, string parameter) =>
        $"{alias}.txid >= pg_snapshot_xmax(@{parameter}::pg_snapshot)";

    private static string InProgressAt(string alias, string parameter) =>
        $"{alias}.txid = ANY (ARRAY(SELECT pg_snapshot_xip(@{parameter}::pg_snapshot)))";

    private static NpgsqlParameter Snapshot(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
