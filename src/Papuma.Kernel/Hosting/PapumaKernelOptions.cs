// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Configures the kernel bootstrap (<c>AddPapumaKernel</c>): database, model,
/// schema management and feed processing.
/// </summary>
public sealed class PapumaKernelOptions
{
    /// <summary>
    /// Gets or sets the connection string. Mutually exclusive with <see cref="DataSource"/>;
    /// the kernel owns (and disposes) the data source it creates from it.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets an externally owned data source. Mutually exclusive with
    /// <see cref="ConnectionString"/>; the caller stays responsible for disposal.
    /// </summary>
    public NpgsqlDataSource? DataSource { get; set; }

    /// <summary>
    /// Gets or sets whether <c>EnsureSchemaAsync</c> — and after it every registered
    /// <see cref="ISchemaContributor"/> — runs on startup (default: true). Turn it off when
    /// schema changes are applied by a separate deployment step.
    /// </summary>
    public bool EnsureSchema { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the change feed and event feed run as hosted background
    /// workers (default: true). Turn it off in a host-based test fixture that drives the
    /// feeds itself (<c>DrainAsync</c> from <c>Papuma.Kernel.Testing</c>, or
    /// <c>ProcessOnceAsync</c>): a hosted worker holding a checkpoint makes a concurrent
    /// cycle skip that handler. Also for instances that should not process feeds, such as
    /// web nodes next to dedicated worker nodes. Schema setup, the processors and event
    /// retention are registered either way.
    /// </summary>
    public bool RunFeedWorkers { get; set; } = true;

    /// <summary>Gets the feed engine options (shared by change feed and event log).</summary>
    public ChangeFeedProcessorOptions Processing { get; } = new();

    /// <summary>
    /// Gets or sets the interval of the event retention worker (default: 1 hour).
    /// The worker only runs when at least one event type declares a retention (ADR-013).
    /// </summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    internal Action<KernelModelBuilder>? ModelConfiguration { get; private set; }

    /// <summary>
    /// Configures the kernel model (documents, events, policies, keys, upcasters).
    /// Multiple calls compose.
    /// </summary>
    /// <param name="configure">The model configuration.</param>
    public void Model(Action<KernelModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ModelConfiguration = ModelConfiguration is null ? configure : ModelConfiguration + configure;
    }

    internal void Validate()
    {
        if ((ConnectionString is null) == (DataSource is null))
        {
            throw new InvalidOperationException(
                "Configure exactly one of PapumaKernelOptions.ConnectionString or .DataSource.");
        }

        if (ModelConfiguration is null)
        {
            throw new InvalidOperationException(
                "Configure the kernel model via PapumaKernelOptions.Model(m => m.Document<...>()).");
        }

        if (RetentionInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("RetentionInterval must be positive.");
        }

        Processing.Validate();
    }
}
