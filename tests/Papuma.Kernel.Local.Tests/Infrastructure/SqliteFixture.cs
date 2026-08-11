// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;

namespace Papuma.Kernel.Local.Tests.Infrastructure;

/// <summary>
/// Test fixture backed by a temp-file SQLite database — the <c>PostgresFixture</c>
/// counterpart for <c>Papuma.Kernel.Local</c>. No Testcontainers needed (SQLite is
/// embedded); one database file per fixture instance, deleted on teardown.
/// </summary>
/// <remarks>
/// Deliberately a temp file, not <c>:memory:</c> with <c>Cache=Shared</c>: shared
/// in-memory SQLite databases vanish when the last connection closes and have real
/// teardown-order-dependent flakiness (Microsoft.Data.Sqlite pools connections by
/// connection string — see <see cref="DisposeAsync"/>). A temp file is slower per test
/// but behaviorally identical to production and removes a whole flakiness class — the
/// right tradeoff for a suite whose entire point is storage-layer correctness.
/// </remarks>
public sealed class SqliteFixture : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"papuma_local_test_{Guid.NewGuid():N}.db");

    /// <summary>Gets the connection string sessions/stores under test should open against.</summary>
    public string ConnectionString { get; private set; } = null!;

    public Task InitializeAsync()
    {
        ConnectionString = $"Data Source={_dbPath}";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // Microsoft.Data.Sqlite pools connections by connection string — Dispose() alone
        // does not release the underlying file handle, so an immediate File.Delete on
        // Windows throws IOException ("used by another process"). ClearPool forces it.
        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies the schema and builds a store for the given model — the common per-test
    /// setup step (mirrors <c>SchemaManager.EnsureSchemaAsync</c> + <c>new DocumentStore(...)</c>
    /// in the Postgres test suite).
    /// </summary>
    public async Task<SqliteDocumentStore> CreateStoreAsync(KernelModel model, Action? notifyWaiters = null)
    {
        await using (var connection = await SqliteConnectionFactory.OpenAsync(ConnectionString))
        {
            await SqliteSchemaManager.EnsureSchemaAsync(connection, model);
        }

        return new SqliteDocumentStore(ConnectionString, model, notifyWaiters);
    }
}

/// <summary>
/// Collection definition — one fixture instance shared per test collection, same shape
/// as <c>PostgresCollection</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>
{
    public const string Name = "sqlite";
}
