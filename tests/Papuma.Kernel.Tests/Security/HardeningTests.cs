// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.AspNetCore.Http;

using Papuma.Kernel.AspNetCore.Tenancy;
using Papuma.Kernel.Gdpr;
using Papuma.Kernel.Model;
using Papuma.Kernel.Processing;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Tests.Security;

/// <summary>
/// Unit tests (no database) for the hardening items from the security review:
/// failure-message sanitization (M1), DDL identifier injection rejection (M6),
/// the <c>[JsonIgnore]</c> scan skip (L7), and scope-middleware behavior.
/// </summary>
public sealed class HardeningTests
{
    // ── M1: failure messages are sanitized before storage ───────────────────────

    [Fact]
    public void SanitizeError_StripsPaths_BoundsLength_KeepsType()
    {
        var ex = new InvalidOperationException(
            @"Failed reading C:\secrets\customers\ssn.txt for /home/app/data while parsing");

        var sanitized = FeedDiagnostics.SanitizeError(ex);

        Assert.StartsWith("InvalidOperationException: ", sanitized);
        Assert.DoesNotContain("ssn.txt", sanitized);
        Assert.DoesNotContain("/home/app/data", sanitized);
        Assert.Contains("<path>", sanitized);
    }

    [Fact]
    public void SanitizeError_TruncatesLongMessages()
    {
        var ex = new InvalidOperationException(new string('x', 5000));

        var sanitized = FeedDiagnostics.SanitizeError(ex);

        Assert.True(sanitized.Length < 260, $"expected bounded length, was {sanitized.Length}");
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void SanitizeError_FlattensNewlines_HidingStackLikeContent()
    {
        var ex = new InvalidOperationException("boom\n   at Some.Method()\n   at Other.Method()");

        var sanitized = FeedDiagnostics.SanitizeError(ex);

        Assert.DoesNotContain("\n", sanitized);
    }

    // ── M6: DDL identifier injection is rejected by the validator ────────────────

    [Theory]
    [InlineData("User'; DROP TABLE papuma.document; --")]
    [InlineData("User OR 1=1")]
    [InlineData("User-Name")]
    [InlineData("1User")]
    [InlineData("")]
    public void ValidateDocumentType_RejectsInjectionAndMalformed(string candidate)
    {
        Assert.Throws<ArgumentException>(() => InputValidator.ValidateDocumentType(candidate));
    }

    [Fact]
    public void ValidateDocumentType_AcceptsLegitimateNames()
    {
        InputValidator.ValidateDocumentType("User");
        InputValidator.ValidateDocumentType("Order_Line2");
    }

    // ── L7: [JsonIgnore] properties carry no policy/key and no inventory path ────

    private sealed record IgnoreDoc(
        string Id,
        string Name,
        [property: System.Text.Json.Serialization.JsonIgnore]
        [property: SensitiveData] string? Computed = null);

    [Fact]
    public void JsonIgnoredProperty_IsAbsentFromModelAndInventory()
    {
        var model = new KernelModelBuilder().Document<IgnoreDoc>().Build();
        var metadata = model.GetRequired<IgnoreDoc>();

        // No policy is recorded for the ignored path (it would be a no-op anyway).
        Assert.DoesNotContain("computed", metadata.Policies.Keys);

        var inventory = DataInventory.Build(model);
        var doc = Assert.Single(inventory.Documents, d => d.Name == nameof(IgnoreDoc));
        Assert.DoesNotContain(doc.Fields, f => f.Path == "computed");
        Assert.Contains(doc.Fields, f => f.Path == "name");
    }

    // ── ScopeMiddleware stores the resolved scope for the request ────────────────

    [Fact]
    public async Task ScopeMiddleware_StoresResolvedScope_InHttpContextItems()
    {
        var scope = ScopeContext.Tenant("acme");
        var resolver = new FixedResolver(scope);
        var nextCalled = false;
        var middleware = new ScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        await middleware.InvokeAsync(context, resolver);

        Assert.True(nextCalled);
        Assert.Same(scope, context.GetScopeContext());
    }

    [Fact]
    public void GetScopeContext_ThrowsWhenMiddlewareDidNotRun()
    {
        var context = new DefaultHttpContext();
        Assert.Throws<InvalidOperationException>(() => context.GetScopeContext());
    }

    private sealed class FixedResolver(ScopeContext scope) : IScopeResolver
    {
        public ScopeContext Resolve(HttpContext context) => scope;
    }
}
