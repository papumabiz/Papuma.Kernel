// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Papuma.Kernel.AspNetCore.Dashboard;
using Papuma.Kernel.Changes;
using Papuma.Kernel.Events;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Diagnostics;

/// <summary>
/// Security regression tests for the embedded dashboard (review A-1, A-2, A-4):
/// no unescaped interpolation into <c>innerHTML</c>, hardened response headers,
/// validated mount path.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DashboardSecurityTests
{
    private readonly PostgresFixture _fixture;

    public DashboardSecurityTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static string RenderPage(string dataPath = "/papuma/data", string nonce = "TESTNONCE") =>
        (string)typeof(DashboardMetricsCollector).Assembly
            .GetType("Papuma.Kernel.AspNetCore.Dashboard.DashboardPage")!
            .GetMethod("Html", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!
            .Invoke(null, [dataPath, nonce])!;

    [Fact]
    public void Page_DefinesAnHtmlEscapeFunction()
    {
        var html = RenderPage();

        Assert.Contains("function esc(", html);
        // All five HTML-significant characters are handled.
        foreach (var entity in new[] { "&amp;", "&lt;", "&gt;", "&quot;", "&#39;" })
        {
            Assert.Contains(entity, html);
        }
    }

    [Fact]
    public void Page_EveryTemplateInterpolationGoesThroughEsc()
    {
        var html = RenderPage();

        var interpolations = Regex.Matches(html, @"\$\{", RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.NotEmpty(interpolations);

        var unescaped = new List<string>();
        foreach (Match m in interpolations)
        {
            var rest = html.AsSpan(m.Index + 2);
            if (!rest.StartsWith("esc(", StringComparison.Ordinal))
            {
                var end = Math.Min(60, rest.Length);
                unescaped.Add(rest[..end].ToString());
            }
        }

        Assert.True(
            unescaped.Count == 0,
            "Template interpolations that bypass esc(): " + string.Join(" | ", unescaped));
    }

    [Fact]
    public void Page_AssignsInnerHtmlOnlyFromTemplatesOrConstants()
    {
        var html = RenderPage();

        // No string concatenation of dynamic data into markup: every innerHTML target is
        // either a constant or built by a template literal / map(...).join("").
        foreach (Match m in Regex.Matches(html, @"innerHTML\s*=\s*([^;]+);", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            Assert.DoesNotMatch(@"\+\s*(d|f|r|h|k)\.", m.Groups[1].Value);
        }
    }

    [Fact]
    public void Page_CarriesTheNonceOnScriptAndStyle()
    {
        var html = RenderPage(nonce: "abc123");

        Assert.Contains("<script nonce=\"abc123\">", html);
        Assert.Contains("<style nonce=\"abc123\">", html);
    }

    [Fact]
    public void Page_EncodesTheDataPathIntoTheJsLiteral()
    {
        var html = RenderPage("/a\"b</script>");

        Assert.DoesNotContain("/a\"b</script>", html);
        Assert.Contains("const DATA = \"", html);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("/a b")]
    [InlineData("/a\"b")]
    [InlineData("/a</script>")]
    [InlineData("papuma")]
    [InlineData("/../x")]
    public void MapPapumaDashboard_RejectsInvalidPaths(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddPapumaDashboard();
        using var app = builder.Build();

        var ex = Assert.Throws<ArgumentException>(() => ((IEndpointRouteBuilder)app).MapPapumaDashboard(path));
        Assert.Equal("path", ex.ParamName);
    }

    [Fact]
    public async Task PageAndData_CarryHardenedHeaders_AndMatchingNonce()
    {
        await using var app = await StartDashboardAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var page = await http.GetAsync("/papuma");
        var pageHtml = await page.Content.ReadAsStringAsync();
        AssertHardened(page);

        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = Regex.Match(csp, @"script-src 'nonce-([^']+)'", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"style-src 'nonce-{nonce}'", csp);
        Assert.Contains($"<script nonce=\"{nonce}\">", pageHtml);
        Assert.Contains($"<style nonce=\"{nonce}\">", pageHtml);
        Assert.DoesNotContain("script-src 'unsafe-inline'", csp);

        using var data = await http.GetAsync("/papuma/data");
        Assert.True(data.IsSuccessStatusCode, await data.Content.ReadAsStringAsync());
        AssertHardened(data);

        // A fresh nonce per request.
        using var page2 = await http.GetAsync("/papuma");
        var csp2 = page2.Headers.GetValues("Content-Security-Policy").Single();
        Assert.NotEqual(csp, csp2);
    }

    private static void AssertHardened(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single());

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'none'", csp);
    }

    private async Task<WebApplication> StartDashboardAsync()
    {
        // The data endpoint reads the lag, which needs the kernel tables — a fresh database has none
        // (the test used to pass only after other tests had created them).
        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, new KernelModelBuilder().Build());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddPapumaDashboard();
        builder.Services.AddSingleton(_ => new ChangeFeedProcessor(_fixture.DataSource, [new NoopChange()]));
        builder.Services.AddSingleton(_ => new EventFeedProcessor(_fixture.DataSource, [new NoopEvent()]));

        var app = builder.Build();
        ((IEndpointRouteBuilder)app).MapPapumaDashboard("/papuma");
        await app.StartAsync();
        return app;
    }

    private sealed class NoopChange : IChangeHandler
    {
        public string Name { get; } = $"dsec-{Guid.NewGuid():N}";

        public Task HandleAsync(ChangeRecord change, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopEvent : IEventHandler
    {
        public string Name { get; } = $"dsec-evt-{Guid.NewGuid():N}";

        public Task HandleAsync(EventRecord @event, CancellationToken ct) => Task.CompletedTask;
    }
}
