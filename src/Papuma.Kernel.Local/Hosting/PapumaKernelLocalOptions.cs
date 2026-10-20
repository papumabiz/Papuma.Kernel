// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Configures the SQLite kernel bootstrap (<c>AddPapumaKernelLocal</c>) — counterpart of
/// <c>PapumaKernelOptions</c>. Database, model, schema management and feed processing.
/// </summary>
public sealed class PapumaKernelLocalOptions
{
    /// <summary>
    /// Gets or sets the SQLite database file path. Mutually exclusive with
    /// <see cref="ConnectionString"/> — a plain <c>Data Source=...</c> connection string
    /// is built from it; the path is taken literally (<c>;</c> and <c>=</c> in it are not
    /// connection-string syntax). An empty path and <c>:memory:</c> are rejected: every
    /// connection would get its own private database.
    /// </summary>
    public string? DbPath { get; set; }

    /// <summary>
    /// Gets or sets a raw SQLite connection string (e.g. for
    /// <c>Data Source=file.db;Cache=Shared</c> or other tuning). Mutually exclusive with
    /// <see cref="DbPath"/>. It needs a non-empty <c>Data Source</c>; a private in-memory
    /// database (<c>:memory:</c> or <c>Mode=Memory</c> without <c>Cache=Shared</c>) is rejected,
    /// because the schema initializer, sessions and feed workers each open their own
    /// connection and would see different, empty databases.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets whether the kernel schema — and after it every registered
    /// <see cref="ISqliteSchemaContributor"/> — is applied on startup (default: true).
    /// </summary>
    public bool EnsureSchema { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the change feed and event feed run as hosted background
    /// workers (default: true). Turn it off in a host-based test fixture that drives the
    /// feeds itself with <c>ProcessOnceAsync</c> until a cycle delivers nothing: next to a
    /// hosted worker, such a cycle races it — it can report nothing while the worker is
    /// still delivering. Also for instances that should not process feeds, such as
    /// web nodes next to dedicated worker nodes. Schema setup, the processors and event
    /// retention are registered either way.
    /// </summary>
    public bool RunFeedWorkers { get; set; } = true;

    /// <summary>
    /// Gets or sets the master key of the keyed <c>Hash</c> policy (ADR-025), base64, at least
    /// 32 random bytes. Every process that writes must use the same key and
    /// <see cref="HashKeyId"/>. Read it from a secret store or an environment variable — never
    /// commit it. For per-tenant keys from a KMS register your own
    /// <see cref="IHashKeyProvider"/> instead.
    /// </summary>
    public string? HashMasterKey { get; set; }

    /// <summary>
    /// Gets or sets the identifier of <see cref="HashMasterKey"/> (default <c>k1</c>), written into
    /// every hash. Configure a new id together with a new key to rotate.
    /// </summary>
    public string HashKeyId { get; set; } = "k1";

    /// <summary>Gets the feed engine options (shared by change feed and event log).</summary>
    public ChangeFeedProcessorOptions Processing { get; } = new();

    /// <summary>
    /// Gets or sets the interval of the event retention worker (default: 1 hour, at most about 49.7 days). The
    /// worker only runs when at least one event type declares a retention.
    /// </summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    // The longest period a PeriodicTimer accepts (0xFFFFFFFE ms, ~49.7 days).
    private static readonly TimeSpan MaxRetentionInterval = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    internal Action<KernelModelBuilder>? ModelConfiguration { get; private set; }

    /// <summary>
    /// Configures the kernel model (documents, events, policies, keys, upcasters).
    /// Multiple calls compose.
    /// </summary>
    public void Model(Action<KernelModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ModelConfiguration = ModelConfiguration is null ? configure : ModelConfiguration + configure;
    }

    /// <summary>Resolves the effective connection string from whichever of the two was configured.</summary>
    internal string ResolveConnectionString() =>
        ConnectionString ?? new SqliteConnectionStringBuilder { DataSource = DbPath }.ConnectionString;

    /// <summary>
    /// Rejects a connection string without a data source and one that would give every
    /// connection its own private in-memory database.
    /// </summary>
    private static void ValidateDataSource(string connectionString, string paramName)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Not a valid SQLite connection string: {ex.Message}", paramName, ex);
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException(
                "The SQLite data source must not be empty: an empty data source opens a private, temporary database per connection.",
                paramName);
        }

        var isMemory = builder.Mode == SqliteOpenMode.Memory
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && (dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
                    || dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)));
        var isShared = builder.Cache == SqliteCacheMode.Shared
            || (dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && dataSource.Contains("cache=shared", StringComparison.OrdinalIgnoreCase));

        if (isMemory && !isShared)
        {
            throw new ArgumentException(
                "A private in-memory SQLite database is not supported: the schema initializer, sessions and feed workers each open "
                + "their own connection and would see different, empty databases. Use a file, or a shared in-memory database "
                + "(Cache=Shared, which lives only while a connection to it stays open).",
                paramName);
        }
    }

    internal void Validate()
    {
        if ((DbPath is null) == (ConnectionString is null))
        {
            throw new InvalidOperationException(
                "Configure exactly one of PapumaKernelLocalOptions.DbPath or .ConnectionString.");
        }

        if (ConnectionString is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
            ValidateDataSource(ConnectionString, nameof(ConnectionString));
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(DbPath);
            ValidateDataSource(ResolveConnectionString(), nameof(DbPath));
        }

        if (ModelConfiguration is null)
        {
            throw new InvalidOperationException(
                "Configure the kernel model via PapumaKernelLocalOptions.Model(m => m.Document<...>()).");
        }

        if (RetentionInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("RetentionInterval must be positive.");
        }

        if (RetentionInterval > MaxRetentionInterval)
        {
            // PeriodicTimer rejects longer periods: fail at startup, not when the worker starts.
            throw new InvalidOperationException(
                $"RetentionInterval must not exceed {MaxRetentionInterval} (the longest period a PeriodicTimer accepts).");
        }

        if (HashMasterKey is not null)
        {
            // Fail at startup, not at the first write that hashes a field.
            _ = MasterKeyHashKeyProvider.FromBase64(HashMasterKey, HashKeyId);
        }

        Processing.Validate();
    }
}
