// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Gdpr;

/// <summary>
/// A non-generic reference to one document for GDPR tooling (ADR-015). Which documents
/// belong to a data subject is domain knowledge — the application assembles these refs
/// from its keys and projections.
/// </summary>
/// <param name="DocumentType">The logical document type name.</param>
/// <param name="Id">The document identifier.</param>
public sealed record DocumentRef(string DocumentType, string Id);
