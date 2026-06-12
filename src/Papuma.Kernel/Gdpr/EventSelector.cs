// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// Selects events of one type by a payload field value (ADR-015) — e.g. all
/// <c>UserLoggedIn</c> events whose <c>userId</c> equals the data subject's id.
/// </summary>
/// <param name="EventType">The logical event type name.</param>
/// <param name="PayloadPath">The dot-separated payload path to compare (e.g. <c>userId</c> or <c>order.customerId</c>).</param>
/// <param name="Value">The value to match (text comparison, as produced by the <c>#&gt;&gt;</c> operator).</param>
public sealed record EventSelector(string EventType, string PayloadPath, string Value);
