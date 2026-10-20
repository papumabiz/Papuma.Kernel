// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Core.Tests.Validation;

/// <summary>
/// The whitelists for tenant ids, type names and document ids (reviews C-2, C-10) and the parameter names the
/// guard clauses report (review Q-4).
/// </summary>
public sealed class InputValidationTests
{
    private sealed record Doc(string Id, string Name);

    private sealed record Happened(string Who);

    // ── C-2: a trailing newline must not pass the whitelists ───────────────────

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    [InlineData("acme-1\n")]
    [InlineData("\nabc")]
    [InlineData("a")] // too short
    [InlineData("_abc")]
    [InlineData("a b")]
    public void Tenant_RejectsMalformedIds_IncludingATrailingNewline(string tenantId)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScopeContext.Tenant(tenantId));

        Assert.Equal("tenantId", ex.ParamName);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("acme_1-x")]
    [InlineData("5b2a3c1e-0f4d-4c1b-9a5e-1f2e3d4c5b6a")]
    public void Tenant_AcceptsWellFormedIds(string tenantId) =>
        Assert.Equal(tenantId, ScopeContext.Tenant(tenantId).TenantId);

    [Theory]
    [InlineData("Order\n")]
    [InlineData("Order\r\n")]
    [InlineData("1Order")]
    [InlineData("O")]
    [InlineData("Or der")]
    public void DocumentType_RejectsMalformedNames_IncludingATrailingNewline(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => InputValidator.ValidateDocumentType(name));

        Assert.Equal("documentType", ex.ParamName);
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("Order_Line2")]
    public void DocumentType_AcceptsWellFormedNames(string name) => InputValidator.ValidateDocumentType(name);

    // ── C-10: control characters in a document id ──────────────────────────────

    [Theory]
    [InlineData("a\0b")]
    [InlineData("\0")]
    [InlineData("a\nb")]
    [InlineData("a\r")]
    [InlineData("a\tb")]
    [InlineData("a\u001Fb")]
    [InlineData("a\u007Fb")] // DEL
    [InlineData("a\u0085b")] // NEL, a C1 control
    public void DocumentId_RejectsControlCharacters(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => InputValidator.ValidateDocumentId(id));

        Assert.Equal("documentId", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DocumentId_RejectsEmptyAndBlank(string id) =>
        Assert.Throws<ArgumentException>(() => InputValidator.ValidateDocumentId(id));

    [Fact]
    public void DocumentId_RejectsMoreThan200Characters()
    {
        InputValidator.ValidateDocumentId(new string('x', 200));

        Assert.Throws<ArgumentException>(() => InputValidator.ValidateDocumentId(new string('x', 201)));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("a/b")] // legal: the kernel parses no reference strings
    [InlineData("Müller 7")]
    [InlineData("😀")]
    [InlineData("5b2a3c1e-0f4d-4c1b-9a5e-1f2e3d4c5b6a")]
    public void DocumentId_AcceptsOrdinaryText(string id) => InputValidator.ValidateDocumentId(id);

    // ── Q-4: the blamed parameter is the one the caller passed ─────────────────

    [Fact]
    public void DocumentNamed_BlamesName_NotDocumentType()
    {
        var builder = new KernelModelBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Document<Doc>(d => d.Named("not valid")));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void EventNamed_BlamesName_NotDocumentType()
    {
        var builder = new KernelModelBuilder();

        var ex = Assert.Throws<ArgumentException>(() => builder.Event<Happened>(e => e.Named("not valid")));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void Named_AcceptsAValidName() =>
        _ = new KernelModelBuilder().Document<Doc>(d => d.Named("valid_name")).Build();

    [Fact]
    public void PolicyAndKeyBuilders_BlameProperty_WhenTheExpressionIsNotAPropertyChain()
    {
        var builder = new KernelModelBuilder();

        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => builder.Document<Doc>(d => d.Property(x => x.Name.ToUpperInvariant()))).ParamName);
        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => builder.Document<Doc>(d => d.UniqueKey(x => x.Name.ToUpperInvariant()))).ParamName);
        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => builder.Document<Doc>(d => d.LookupKey(x => x.Name.ToUpperInvariant()))).ParamName);
        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => builder.Event<Happened>(e => e.Property(x => x.Who.ToUpperInvariant()))).ParamName);
    }

    [Fact]
    public void PatchBuilder_BlamesProperty_WhenTheExpressionIsNotAPropertyChain()
    {
        var patch = new PatchBuilder<Doc>();

        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => patch.Set(x => x.Name.ToUpperInvariant(), "x")).ParamName);
        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => patch.Remove(x => x.Name.ToUpperInvariant())).ParamName);
        Assert.Equal(
            "property",
            Assert.Throws<ArgumentException>(() => patch.Increment(x => x.Name.ToUpperInvariant())).ParamName);
    }

    [Fact]
    public void FieldChanged_BlamesProperty_WhenTheExpressionIsNotAPropertyChain()
    {
        var change = new ChangeRecord(
            Seq: 1,
            ScopeContext.Tenant("acme"),
            "Doc",
            "1",
            Version: 1,
            SchemaVersion: 1,
            ChangeOperation.Insert,
            DocumentDiff.Empty,
            ActorId: string.Empty,
            new System.Text.Json.Nodes.JsonObject(),
            DateTimeOffset.UnixEpoch);

        var ex = Assert.Throws<ArgumentException>(() => change.FieldChanged<Doc>(x => x.Name.ToUpperInvariant()));

        Assert.Equal("property", ex.ParamName);
    }

    [Fact]
    public void ResolveKey_BlamesTheNameTheCallerGives()
    {
        Expression<Func<Doc, object?>> notAChain = x => x.Name.ToUpperInvariant();
        Expression<Func<Doc, object?>> emptyComposite = x => new { };
        Expression<Func<Doc, object?>> duplicate = x => new { x.Name, Again = x.Name };

        Assert.Equal("key", Assert.Throws<ArgumentException>(() => JsonPathResolver.ResolveKey(notAChain, "key")).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => JsonPathResolver.ResolveKey(emptyComposite, "key")).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => JsonPathResolver.ResolveKey(duplicate, "key")).ParamName);
    }
}
