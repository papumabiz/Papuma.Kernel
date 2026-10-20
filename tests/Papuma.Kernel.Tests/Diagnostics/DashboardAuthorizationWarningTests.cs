// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.AspNetCore.Dashboard;
using Papuma.Kernel.Events;
using Papuma.Kernel.Processing;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Review A-3: the "dashboard mapped without protection" guard-rail must raise an error
/// (not a warning) exactly once, must recognise every protection the host can put in
/// front of the endpoints (<c>RequireAuthorization</c>, <c>RequireHost</c>, a
/// <c>FallbackPolicy</c>) and must not cry wolf on those.
/// </summary>
public sealed class DashboardAuthorizationWarningTests
{
    private const string Category = "Papuma.Kernel.Dashboard";

    private static readonly AuthorizationPolicy AuthenticatedUser =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    [Fact]
    public async Task Unprotected_LogsOneError()
    {
        await using var host = Build(protect: null);

        Materialize(host.App);

        var entry = Assert.Single(host.Logs.For(Category));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("/papuma", entry.Message);
    }

    [Fact]
    public async Task Unprotected_LogsOnce_NoMatterHowOftenTheEndpointsAreRead()
    {
        await using var host = Build(protect: null);

        Materialize(host.App);
        Materialize(host.App);

        // The dashboard maps two endpoints (page and data) and the convention runs per
        // endpoint: still only one log entry.
        Assert.Single(host.Logs.For(Category));
    }

    [Fact]
    public async Task Unprotected_IsLoggedWhenTheEndpointsAreBuilt_NotAtStartup()
    {
        await using var host = Build(protect: null);
        await host.App.StartAsync();

        // Pins the real behaviour that SECURITY.md describes: starting the host alone does
        // not build the endpoint table, so nothing has been logged yet.
        Assert.Empty(host.Logs.For(Category));

        using var http = new HttpClient { BaseAddress = new Uri(host.App.Urls.First()) };
        using var response = await http.GetAsync("/papuma");
        Assert.True(response.IsSuccessStatusCode);

        Assert.Single(host.Logs.For(Category));
    }

    [Fact]
    public async Task RequireAuthorization_LogsNothing()
    {
        await using var host = Build(b => b.RequireAuthorization());

        Materialize(host.App);

        Assert.Empty(host.Logs.For(Category));
    }

    [Fact]
    public async Task RequireAuthorizationWithPolicyObject_LogsNothing()
    {
        await using var host = Build(b => b.RequireAuthorization(AuthenticatedUser));

        Materialize(host.App);

        Assert.Empty(host.Logs.For(Category));
    }

    [Fact]
    public async Task RequireHost_LogsNothing()
    {
        await using var host = Build(b => b.RequireHost("*:9090"));

        Materialize(host.App);

        Assert.Empty(host.Logs.For(Category));
    }

    [Fact]
    public async Task FallbackPolicy_LogsNothing()
    {
        await using var host = Build(
            protect: null,
            services => services.AddAuthorization(o => o.FallbackPolicy = AuthenticatedUser));

        Materialize(host.App);

        Assert.Empty(host.Logs.For(Category));
    }

    [Fact]
    public async Task AuthorizationWithoutFallbackPolicy_StillLogsTheError()
    {
        await using var host = Build(protect: null, services => services.AddAuthorization());

        Materialize(host.App);

        Assert.Single(host.Logs.For(Category));
    }

    [Fact]
    public async Task AllowAnonymous_OverridesTheFallbackPolicy_AndLogsTheError()
    {
        await using var host = Build(
            b => b.AllowAnonymous(),
            services => services.AddAuthorization(o => o.FallbackPolicy = AuthenticatedUser));

        Materialize(host.App);

        // The fallback policy does not apply to an endpoint marked anonymous.
        Assert.Single(host.Logs.For(Category));
    }

    [Fact]
    public async Task AllowAnonymous_OverridesRequireAuthorization_AndLogsTheError()
    {
        await using var host = Build(b => b.RequireAuthorization().AllowAnonymous());

        Materialize(host.App);

        Assert.Single(host.Logs.For(Category));
    }

    private static TestHost Build(
        Func<IEndpointConventionBuilder, IEndpointConventionBuilder>? protect,
        Action<IServiceCollection>? configureServices = null)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddPapumaDashboard();

        // Registered so the data endpoint's parameters bind as services when the endpoints are
        // built; the factories never run because these tests never request {path}/data.
        builder.Services.AddSingleton<ChangeFeedProcessor>(
            _ => throw new InvalidOperationException("not used"));
        builder.Services.AddSingleton<EventFeedProcessor>(
            _ => throw new InvalidOperationException("not used"));
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        var mapped = ((IEndpointRouteBuilder)app).MapPapumaDashboard("/papuma");
        protect?.Invoke(mapped);
        return new TestHost(app, logs);
    }

    // Reading the endpoints is what runs the endpoint conventions (as the routing
    // middleware does on the first request).
    private static void Materialize(WebApplication app)
    {
        foreach (var source in ((IEndpointRouteBuilder)app).DataSources)
        {
            _ = source.Endpoints.Count;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogEntry Entry)> _entries = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public IReadOnlyList<LogEntry> For(string category) =>
            [.. _entries.Where(e => e.Category == category).Select(e => e.Entry)];

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<(string Category, LogEntry Entry)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, new LogEntry(logLevel, formatter(state, exception))));
        }
    }

    private sealed class TestHost(WebApplication app, CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public CapturingLoggerProvider Logs { get; } = logs;

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
