// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

// The NATS bridge as its own process (recipe: nats-bridge, concepts §22): it
// shares the shop's database and model, runs only the two publisher handlers and
// publishes both feeds to JetStream. The shop itself knows nothing about NATS.

using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

using Papuma.Kernel.Hosting;
using ShopMinimalApi;
using ShopNatsBridge;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddPapumaKernel(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("papuma")
            ?? throw new InvalidOperationException("ConnectionStrings:papuma is required.");
        o.Model(m => m
            .Document<Product>()
            .Document<Inventory>()
            .Document<Order>()
            .Document<ApprovalTask>()
            .Event<StockReplenished>());
    })
    .AddChangeHandler<NatsChangePublisher>()
    .AddEventHandler<NatsEventPublisher>();

var natsUrl = builder.Configuration["Nats:Url"]
    ?? throw new InvalidOperationException("Nats:Url is required.");
builder.Services.AddSingleton(_ => new NatsConnection(new NatsOpts { Url = natsUrl }));
builder.Services.AddSingleton<INatsJSContext>(sp =>
    sp.GetRequiredService<NatsConnection>().CreateJetStreamContext());
builder.Services.AddHostedService<NatsStreamInitializer>();

await builder.Build().RunAsync();
