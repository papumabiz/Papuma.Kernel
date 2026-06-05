// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Npgsql;

namespace Papuma.Kernel.Schema;

/// <summary>
/// Checks whether the database schema has reached a required migration version.
/// </summary>
public sealed class SchemaVersionChecker
{
    /// <summary>
    /// The schema version expected by the current library build.
    /// </summary>
    public const int CurrentRequiredVersion = 2;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaVersionChecker"/> class.
    /// </summary>
    /// <param name="dataSource">The data source used to query schema version metadata.</param>
    public SchemaVersionChecker(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>
    /// Gets the current schema version from <c>papuma_schema_version</c>.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The current schema version, or <c>0</c> when the tracking table is missing.</returns>
    public async Task<int> GetCurrentVersionAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using (var existsCmd = conn.CreateCommand())
        {
            existsCmd.CommandText = "SELECT to_regclass('public.papuma_schema_version') IS NOT NULL";
            var exists = await existsCmd.ExecuteScalarAsync(ct);
            if (exists is not true)
            {
                return 0;
            }
        }

        await using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM papuma_schema_version";

        var result = await versionCmd.ExecuteScalarAsync(ct);
        return result is int version ? version : 0;
    }

    /// <summary>
    /// Ensures the database schema version is at least the required version.
    /// </summary>
    /// <param name="minimumVersion">The minimum required version.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="minimumVersion"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the current version is lower than required.</exception>
    public async Task EnsureMinimumVersionAsync(int minimumVersion, CancellationToken ct = default)
    {
        if (minimumVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumVersion), "minimumVersion must be greater than or equal to 1.");
        }

        var currentVersion = await GetCurrentVersionAsync(ct);
        if (currentVersion < minimumVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is lower than the required version {minimumVersion}. " +
                "Apply versioned scripts from Schema/Migrations before starting the application.");
        }
    }

    /// <summary>
    /// Ensures the database schema version matches the library's required baseline.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    public Task EnsureCurrentBaselineAsync(CancellationToken ct = default) =>
        EnsureMinimumVersionAsync(CurrentRequiredVersion, ct);
}