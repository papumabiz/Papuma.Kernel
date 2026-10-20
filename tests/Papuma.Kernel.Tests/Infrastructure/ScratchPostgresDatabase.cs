// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Testing;

namespace Papuma.Kernel.Tests.Infrastructure;

/// <summary>
/// A database of its own in the shared container, for tests that need an empty database
/// (schema creation, concurrent first starts) or change global state.
/// </summary>
public sealed class ScratchPostgresDatabase : IAsyncDisposable
{
    private readonly PapumaTestDatabase _server;
    private readonly string _name;

    private ScratchPostgresDatabase(PapumaTestDatabase server, string name, NpgsqlDataSource dataSource)
    {
        _server = server;
        _name = name;
        DataSource = dataSource;
    }

    /// <summary>Gets the owner data source of the scratch database.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Creates an empty database with a unique name.</summary>
    public static async Task<ScratchPostgresDatabase> CreateAsync(PapumaTestDatabase server)
    {
        var name = $"scratch_{Guid.NewGuid():N}";
        await using (var cmd = server.OwnerDataSource.CreateCommand($"CREATE DATABASE {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(server.OwnerConnectionString) { Database = name };
        return new ScratchPostgresDatabase(server, name, NpgsqlDataSource.Create(builder.ConnectionString));
    }

    /// <summary>Executes a statement batch.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a query and returns its first column of the first row.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await using var cmd = _server.OwnerDataSource.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
        await cmd.ExecuteNonQueryAsync();
    }
}
