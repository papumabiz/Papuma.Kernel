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
}
