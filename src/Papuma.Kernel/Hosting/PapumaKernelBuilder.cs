// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Chained registration surface returned by <c>AddPapumaKernel</c>.
/// </summary>
public sealed class PapumaKernelBuilder
{
    /// <summary>Gets the underlying service collection.</summary>
    public IServiceCollection Services { get; }

    internal PapumaKernelBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Registers a change feed handler (ADR-009) as singleton.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public PapumaKernelBuilder AddChangeHandler<THandler>()
        where THandler : class, IChangeHandler
    {
        Services.AddSingleton<IChangeHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Registers an event log handler (ADR-013) as singleton.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public PapumaKernelBuilder AddEventHandler<THandler>()
        where THandler : class, IEventHandler
    {
        Services.AddSingleton<IEventHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Registers application schema (projection tables, their RLS policies) to be applied
    /// at startup after the kernel schema and before the feed workers start.
    /// Contributors run in registration order.
    /// </summary>
    /// <typeparam name="TContributor">The contributor type.</typeparam>
    public PapumaKernelBuilder AddSchemaContributor<TContributor>()
        where TContributor : class, ISchemaContributor
    {
        Services.AddSingleton<ISchemaContributor, TContributor>();
        return this;
    }
}
