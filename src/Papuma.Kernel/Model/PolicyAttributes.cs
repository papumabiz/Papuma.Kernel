// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Model;

/// <summary>
/// Marks a property as sensitive: diffs record only that the field changed, never its
/// values (<see cref="FieldPolicy.Redact"/>). Fluent configuration may override this
/// default per deployment (ADR-007).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveDataAttribute : Attribute;

/// <summary>
/// Marks a property whose diffs record a reference to the value's location instead of
/// the value itself (<see cref="FieldPolicy.Reference"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TrackReferenceAttribute : Attribute;

/// <summary>
/// Marks a property whose diffs record a change marker plus a SHA-256 hash of the new
/// value (<see cref="FieldPolicy.Hash"/>) — comparable without exposing content.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TrackHashAttribute : Attribute;

/// <summary>
/// Marks a property that never appears in diffs (<see cref="FieldPolicy.DoNotTrack"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DoNotTrackAttribute : Attribute;

/// <summary>
/// Declares a unique key on a property: the kernel materializes a partial unique
/// expression index per document type (ADR-006).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UniqueKeyAttribute : Attribute;

/// <summary>
/// Declares a non-unique lookup key on a property: the kernel materializes a partial
/// expression index per document type (ADR-006).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LookupKeyAttribute : Attribute;
