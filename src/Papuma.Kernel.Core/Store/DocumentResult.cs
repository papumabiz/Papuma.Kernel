// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// A loaded document together with its stored version (required for the next write, ADR-003).
/// </summary>
/// <typeparam name="T">The document CLR type.</typeparam>
/// <param name="Document">The deserialized document.</param>
/// <param name="Version">The stored document version.</param>
public sealed record DocumentResult<T>(T Document, long Version);
