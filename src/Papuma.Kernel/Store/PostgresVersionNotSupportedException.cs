// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.Store;

/// <summary>
/// Thrown when the connected PostgreSQL server does not meet the minimum supported version.
/// </summary>
/// <remarks>
/// Papuma.Kernel vNEXT requires PostgreSQL 18 or later: the write path relies on
/// <c>RETURNING OLD/NEW</c> as a hard requirement (ADR-001), with no fallback code path.
/// </remarks>
public sealed class PostgresVersionNotSupportedException : InvalidOperationException
{
    /// <summary>
    /// Gets the reported <c>server_version_num</c> of the connected server.
    /// </summary>
    public int ServerVersionNum { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresVersionNotSupportedException"/> class.
    /// </summary>
    /// <param name="serverVersionNum">The reported <c>server_version_num</c> of the connected server.</param>
    public PostgresVersionNotSupportedException(int serverVersionNum)
        : base($"Papuma.Kernel requires PostgreSQL 18 or later (server_version_num >= {SchemaManager.MinimumServerVersionNum}), " +
               $"but the connected server reports {serverVersionNum}. " +
               "PostgreSQL 18 is a hard requirement because the write path depends on RETURNING OLD/NEW (ADR-001).")
    {
        ServerVersionNum = serverVersionNum;
    }
}
