// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

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
}
