// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

// Event-modeled vertical slices on Papuma.Kernel — the *shape*, not the breadth.
// See README.md; conventions in docs/ai/papuma-kernel-slice-conventions.md.

using EventModeledSlices;
using EventModeledSlices.Features.OnOrderPlaced;
using EventModeledSlices.Features.OrderById;
using EventModeledSlices.Features.PlaceOrder;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Hosting;
using Papuma.Kernel.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma")
            ?? "Host=localhost;Port=5432;Database=papuma_slices;Username=postgres;Password=postgres";
        o.Model(m => m
            .Document<Product>()
            .Document<Inventory>(d => d.Validate(inv =>
            {
                if (inv.Stock < 0) throw new OutOfStockException(inv.Id); // bounded counter (§17)
            }))
            .Document<Order>()
            .Event<OrderPlaced>());
    })
    .AddChangeHandler<OnOrderPlaced>(); // the automation slice

builder.Services.AddPapumaScope<DemoScopeResolver>();

var app = builder.Build();
app.UseScopeResolution();

// Each slice maps its own endpoint — the slice owns its wiring.
app.MapPlaceOrder();   // command slice
app.MapOrderById();    // view slice
// (the OnOrderPlaced automation slice has no endpoint — it reacts to the feed)

// StartAsync first so the kernel's hosted schema initializer has run (the tables
// exist) before we seed; then seed; then block until shutdown.
await app.StartAsync();
await SeedAsync(app.Services);
await app.WaitForShutdownAsync();

static async Task SeedAsync(IServiceProvider services)
{
    var store = services.GetRequiredService<DocumentStore>();
    await using var session = store.OpenSession(SampleScope.Tenant);
    if (await session.LoadAsync<Product>("demo-grinder") is not null)
    {
        return;
    }

    await session.SaveAsync(new Product("demo-grinder", "Grinder", 200m), expectedVersion: 0);
    await session.SaveAsync(new Inventory("demo-grinder", 10), expectedVersion: 0);
    await session.CommitAsync();
}
