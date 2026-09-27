// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Extensions.DependencyInjection;

using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Hosting;

/// <summary>
/// Chained registration surface returned by <c>AddPapumaKernelLocal</c> — counterpart of
/// <c>PapumaKernelBuilder</c>.
/// </summary>
public sealed class PapumaKernelLocalBuilder
{
    /// <summary>Gets the underlying service collection.</summary>
    public IServiceCollection Services { get; }

    internal PapumaKernelLocalBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Registers a change feed handler as singleton.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public PapumaKernelLocalBuilder AddChangeHandler<THandler>()
        where THandler : class, IChangeHandler
    {
        Services.AddSingleton<IChangeHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Registers an event log handler as singleton.
    /// </summary>
    /// <typeparam name="THandler">The handler type.</typeparam>
    public PapumaKernelLocalBuilder AddEventHandler<THandler>()
        where THandler : class, IEventHandler
    {
        Services.AddSingleton<IEventHandler, THandler>();
        return this;
    }

    /// <summary>
    /// Registers application schema (projection tables) to be applied at startup after
    /// the kernel schema and before the feed workers start. Contributors run in
    /// registration order.
    /// </summary>
    /// <typeparam name="TContributor">The contributor type.</typeparam>
    public PapumaKernelLocalBuilder AddSchemaContributor<TContributor>()
        where TContributor : class, ISqliteSchemaContributor
    {
        Services.AddSingleton<ISqliteSchemaContributor, TContributor>();
        return this;
    }
}
