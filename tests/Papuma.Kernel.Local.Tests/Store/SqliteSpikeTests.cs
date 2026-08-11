// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

namespace Papuma.Kernel.Local.Tests.Store;

/// <summary>
/// Self-documenting spikes pinning down exact SQLite/Microsoft.Data.Sqlite behavior before
/// <c>SqliteDocumentSession</c> is written against it — mirrors the Postgres kernel's
/// <c>SqlReturningSpikeTests.cs</c>. Findings are recorded as comments next to each
/// assertion; this file *is* the documentation for the choices made in Stage 2.
/// </summary>
public sealed class SqliteSpikeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"papuma_spike_{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;

    public SqliteSpikeTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE doc (id TEXT PRIMARY KEY, version INTEGER NOT NULL, data TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        // Finding: Microsoft.Data.Sqlite pools connections by connection string by
        // default — Dispose() alone does not release the underlying native file handle,
        // so an immediate File.Delete on Windows throws IOException ("used by another
        // process"). SqliteConnection.ClearPool forces the pool to actually close it.
        // Every fixture in Stage 5 needs this same pattern (or Pooling=False on the
        // connection string) before deleting its temp-file database.
        _connection.Dispose();
        SqliteConnection.ClearPool(_connection);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void Update_ReturningNewState_WorksAsSingleStatement()
    {
        // Finding: SQLite's bundled e_sqlite3 (via Microsoft.Data.Sqlite 10.0.10) supports
        // a plain `RETURNING <expr>` clause (added upstream in SQLite 3.35, 2021) — it
        // returns the row(s) as they exist AFTER the statement, there is no OLD/NEW
        // distinction the way Postgres 18's `RETURNING OLD/NEW` has. For UpdateAsync,
        // Stage 2 therefore needs a preceding SELECT for the "old" state, then this
        // single-statement UPDATE...RETURNING for the "new" state — two round-trips
        // instead of Postgres's one, safe because the whole session transaction is
        // exclusive (SQLite serializes writers at the file level; nothing can interleave
        // between the two statements of the same connection/transaction).
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO doc (id, version, data) VALUES ('a', 1, '{\"x\":1}');";
        insert.ExecuteNonQuery();

        using var update = _connection.CreateCommand();
        update.CommandText = """
            UPDATE doc SET data = '{"x":2}', version = version + 1
            WHERE id = 'a' AND version = 1
            RETURNING data, version;
            """;
        using var reader = update.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("{\"x\":2}", reader.GetString(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.False(reader.Read()); // exactly one row
    }

    [Fact]
    public void Update_VersionMismatch_ReturnsZeroRows()
    {
        // Finding: a version-mismatched UPDATE...RETURNING simply yields zero rows from
        // the reader — no exception. Stage 2's UpdateAsync must check HasRows/Read()
        // returning false and route to VersionConflictAsync, mirroring the Postgres
        // "zero rows from RETURNING" convention exactly.
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO doc (id, version, data) VALUES ('b', 1, '{}');";
        insert.ExecuteNonQuery();

        using var update = _connection.CreateCommand();
        update.CommandText = "UPDATE doc SET version = version + 1 WHERE id = 'b' AND version = 99 RETURNING version;";
        using var reader = update.ExecuteReader();
        Assert.False(reader.Read());
    }

    [Fact]
    public void Delete_ReturningOldState_WorksAsSingleStatement()
    {
        // Finding: DELETE...RETURNING naturally returns the deleted ("old") row's values —
        // no OLD/NEW split needed here at all, unlike UPDATE. Direct single-statement port
        // of the Postgres DeleteAsync shape.
        using var insert = _connection.CreateCommand();
        insert.CommandText = "INSERT INTO doc (id, version, data) VALUES ('c', 1, '{\"y\":true}');";
        insert.ExecuteNonQuery();

        using var delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM doc WHERE id = 'c' AND version = 1 RETURNING data;";
        using var reader = delete.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("{\"y\":true}", reader.GetString(0));
    }

    [Fact]
    public void UniqueIndexViolation_ExceptionShape()
    {
        // Finding (pins ExecuteMappingKeyViolationsAsync's exception-mapping regex for
        // Stage 2): Microsoft.Data.Sqlite surfaces a unique-constraint violation as
        // SqliteException with SqliteErrorCode == 19 (SQLITE_CONSTRAINT) and
        // SqliteExtendedErrorCode == 2067 (SQLITE_CONSTRAINT_UNIQUE). The message format
        // for a violation on a *named* UNIQUE INDEX (not an inline table constraint) is:
        //   "SQLite Error 19: 'UNIQUE constraint failed: index 'ux_doc_email'.'."
        // i.e. the literal substring "index '<name>'" appears in the message when the
        // index has an explicit name — Stage 2's key-violation mapping should extract the
        // name via a regex matching `index '([^']+)'`, falling back to "no declared key
        // matched" (rethrow unchanged) exactly as the Postgres path does for constraints
        // that aren't declared keys.
        using var idx = _connection.CreateCommand();
        idx.CommandText = "CREATE UNIQUE INDEX ux_doc_email ON doc (json_extract(data, '$.email'));";
        idx.ExecuteNonQuery();

        using var insert1 = _connection.CreateCommand();
        insert1.CommandText = "INSERT INTO doc (id, version, data) VALUES ('d1', 1, '{\"email\":\"a@b.com\"}');";
        insert1.ExecuteNonQuery();

        using var insert2 = _connection.CreateCommand();
        insert2.CommandText = "INSERT INTO doc (id, version, data) VALUES ('d2', 1, '{\"email\":\"a@b.com\"}');";

        var ex = Assert.Throws<SqliteException>(() => insert2.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);
        Assert.Equal(2067, ex.SqliteExtendedErrorCode);
        Assert.Contains("index 'ux_doc_email'", ex.Message);
    }

    [Fact]
    public void Savepoint_RawSqlText_RollsBackOnlyItsOwnEffects()
    {
        // Finding: Microsoft.Data.Sqlite's SqliteTransaction does NOT expose a
        // Save/Release/Rollback(string) savepoint API (unlike Npgsql's NpgsqlTransaction)
        // — Stage 2's ExecuteWriteAsync savepoint wrapper must issue raw
        // "SAVEPOINT x;" / "RELEASE x;" / "ROLLBACK TO x;" as plain SqliteCommand text
        // against the ambient transaction, same as any other statement.
        using var tx = _connection.BeginTransaction();

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO doc (id, version, data) VALUES ('e', 1, '{}');";
            insert.ExecuteNonQuery();
        }

        using (var save = _connection.CreateCommand())
        {
            save.Transaction = tx;
            save.CommandText = "SAVEPOINT papuma_write;";
            save.ExecuteNonQuery();
        }

        using (var badInsert = _connection.CreateCommand())
        {
            badInsert.Transaction = tx;
            badInsert.CommandText = "INSERT INTO doc (id, version, data) VALUES ('e', 1, '{}');"; // PK collision
            Assert.Throws<SqliteException>(() => badInsert.ExecuteNonQuery());
        }

        using (var rollbackTo = _connection.CreateCommand())
        {
            rollbackTo.Transaction = tx;
            rollbackTo.CommandText = "ROLLBACK TO papuma_write;";
            rollbackTo.ExecuteNonQuery();
        }

        tx.Commit();

        using var count = _connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM doc WHERE id = 'e';";
        Assert.Equal(1L, (long)count.ExecuteScalar()!); // the first insert survived; the savepoint rollback undid nothing else
    }

    [Fact]
    public void JsonExtractIndex_UsedByQueryPlanner()
    {
        // Finding: EXPLAIN QUERY PLAN confirms the partial expression index is used
        // (plan mentions "USING INDEX ux_doc_email") when the WHERE clause textually
        // matches the index's json_extract expression — SqliteDocumentSession's queries
        // must use json_extract(data, '$.path') literally, not a decode-in-.NET
        // comparison, or the index built by SqliteSchemaManager goes unused.
        using var idx = _connection.CreateCommand();
        idx.CommandText = "CREATE UNIQUE INDEX ux_doc_email ON doc (json_extract(data, '$.email'));";
        idx.ExecuteNonQuery();

        using var plan = _connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM doc WHERE json_extract(data, '$.email') = 'a@b.com';";
        using var reader = plan.ExecuteReader();
        var planText = "";
        while (reader.Read())
        {
            planText += reader.GetString(reader.GetOrdinal("detail")) + "\n";
        }

        Assert.Contains("ux_doc_email", planText);
    }
}
