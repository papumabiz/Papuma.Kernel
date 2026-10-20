// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.RegularExpressions;

namespace Papuma.Kernel.Tenancy;

/// <summary>
/// Represents the current data scope for platform- or tenant-scoped operations.
/// </summary>
public sealed record ScopeContext
{
    // A whitelist, not an SQL-safety requirement: tenant ids only ever reach SQL as
    // parameters. It admits GUID strings ("D"/"N") and keeps ids free of whitespace
    // and punctuation that would make them awkward in logs, URLs and connection strings.
    // \z, not $: a trailing newline must not pass (it would be a second, phantom tenant).
    private static readonly Regex ValidTenantPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9_-]{1,100}\z",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Gets the resolved scope kind.
    /// </summary>
    public ScopeType Scope { get; }

    /// <summary>
    /// Gets the tenant identifier for <see cref="ScopeType.Tenant"/> scope; otherwise <c>null</c>.
    /// </summary>
    public string? TenantId { get; }

    private ScopeContext(ScopeType scope, string? tenantId)
    {
        Scope = scope;
        TenantId = tenantId;
    }

    /// <summary>
    /// Creates a platform scope.
    /// </summary>
    public static ScopeContext Platform() => new(ScopeType.Platform, null);

    /// <summary>
    /// Creates a validated tenant scope.
    /// </summary>
    /// <param name="tenantId">
    /// The tenant identifier: a letter or digit followed by 1–100 letters, digits,
    /// underscores or dashes (<c>^[A-Za-z0-9][A-Za-z0-9_-]{1,100}$</c>, matched against the whole string:
    /// a trailing newline is rejected). GUID strings match; ids are compared case-sensitively, so prefer <see cref="Tenant(Guid)"/>
    /// for GUIDs to get one canonical form.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tenantId"/> does not match the pattern above. The message
    /// echoes at most the first 40 characters of the rejected value, with control characters
    /// replaced, because the value is typically client input that ends up in logs.
    /// </exception>
    public static ScopeContext Tenant(string tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);

        if (!ValidTenantPattern.IsMatch(tenantId))
        {
            throw new ArgumentException(
                $"Invalid tenantId '{Describe(tenantId)}'. Must match [A-Za-z0-9][A-Za-z0-9_-]{{1,100}}.",
                nameof(tenantId));
        }

        return new ScopeContext(ScopeType.Tenant, tenantId);
    }

    // The rejected value usually comes straight from a request (header, claim) and ends up in
    // an exception message and from there in logs: cut it short and neutralise line breaks and
    // other control characters so it cannot forge log lines or flood them.
    private static string Describe(string value)
    {
        const int MaxShown = 40;

        var shown = value.AsSpan(0, Math.Min(value.Length, MaxShown));
        if (value.Length > MaxShown && char.IsHighSurrogate(shown[^1]))
        {
            shown = shown[..^1];
        }

        var buffer = new char[shown.Length];
        for (var i = 0; i < shown.Length; i++)
        {
            var c = shown[i];
            buffer[i] = char.IsControl(c) || c is '\u2028' or '\u2029' ? '\uFFFD' : c;
        }

        var text = new string(buffer);
        return value.Length > MaxShown ? $"{text}… ({value.Length} characters)" : text;
    }

    /// <summary>
    /// Creates a tenant scope from a GUID in its canonical form: lowercase with dashes
    /// (<c>Guid.ToString("D")</c>), so every caller derives the same tenant id.
    /// </summary>
    /// <param name="tenantId">The tenant identifier; must not be <see cref="Guid.Empty"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="tenantId"/> is <see cref="Guid.Empty"/>.</exception>
    public static ScopeContext Tenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The empty GUID is not a tenant id.", nameof(tenantId));
        }

        return Tenant(tenantId.ToString("D"));
    }
}
