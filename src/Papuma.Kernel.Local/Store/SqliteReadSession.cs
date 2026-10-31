// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Linq.Expressions;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Store;

/// <summary>
/// A session that only reads, from <see cref="SqliteDocumentStore.OpenReadSession"/> (review L-5). It runs in a deferred
/// transaction: under SQLite's WAL mode it takes no write lock and blocks no writer — not the application's write
/// sessions, not the feed workers — so it may stay open while a user looks at a screen. It has the reads of
/// <see cref="SqliteDocumentSession"/> and no write method at all; save in a short <see cref="SqliteDocumentSession"/>,
/// with the version read here as <c>expectedVersion</c>.
/// </summary>
/// <remarks>
/// Every read sees one consistent snapshot, taken by the first read, until the session is disposed. While it is open
/// the WAL file cannot be checkpointed past that snapshot and grows: dispose it when the reads are done, and open a
/// new one for fresh data.
/// </remarks>
public sealed class SqliteReadSession : IAsyncDisposable
{
    private readonly SqliteDocumentSession _session;

    internal SqliteReadSession(SqliteDocumentSession session) => _session = session;

    /// <summary>Gets the scope this session is bound to.</summary>
    public ScopeContext Scope => _session.Scope;

    /// <inheritdoc cref="SqliteDocumentSession.LoadAsync{T}(string, CancellationToken)"/>
    public Task<DocumentResult<T>?> LoadAsync<T>(string id, CancellationToken ct = default)
        where T : class =>
        _session.LoadAsync<T>(id, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadManyAsync{T}(IEnumerable{string}, CancellationToken)"/>
    public Task<IReadOnlyDictionary<string, DocumentResult<T>>> LoadManyAsync<T>(
        IEnumerable<string> ids, CancellationToken ct = default)
        where T : class =>
        _session.LoadManyAsync<T>(ids, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadByKeyAsync{T}(Expression{Func{T, object}}, object, CancellationToken)"/>
    public Task<DocumentResult<T>?> LoadByKeyAsync<T>(
        Expression<Func<T, object?>> key, object value, CancellationToken ct = default)
        where T : class =>
        _session.LoadByKeyAsync(key, value, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadByKeyAsync{T}(Expression{Func{T, object}}, IReadOnlyList{object}, CancellationToken)"/>
    public Task<DocumentResult<T>?> LoadByKeyAsync<T>(
        Expression<Func<T, object?>> key, IReadOnlyList<object> values, CancellationToken ct = default)
        where T : class =>
        _session.LoadByKeyAsync(key, values, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadAllByKeyAsync{T}(Expression{Func{T, object}}, object, int, string, CancellationToken)"/>
    public Task<IReadOnlyList<DocumentResult<T>>> LoadAllByKeyAsync<T>(
        Expression<Func<T, object?>> key, object value, int limit, string? afterId = null, CancellationToken ct = default)
        where T : class =>
        _session.LoadAllByKeyAsync(key, value, limit, afterId, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadAllByKeyAsync{T}(Expression{Func{T, object}}, IReadOnlyList{object}, int, string, CancellationToken)"/>
    public Task<IReadOnlyList<DocumentResult<T>>> LoadAllByKeyAsync<T>(
        Expression<Func<T, object?>> key, IReadOnlyList<object> values, int limit, string? afterId = null,
        CancellationToken ct = default)
        where T : class =>
        _session.LoadAllByKeyAsync(key, values, limit, afterId, ct);

    /// <inheritdoc cref="SqliteDocumentSession.LoadMaskedAsync{T}(string, CancellationToken)"/>
    public Task<MaskedDocumentResult?> LoadMaskedAsync<T>(string id, CancellationToken ct = default)
        where T : class =>
        _session.LoadMaskedAsync<T>(id, ct);

    /// <inheritdoc cref="SqliteDocumentSession.GetHistoryAsync{T}(string, long?, long?, CancellationToken)"/>
    public Task<IReadOnlyList<ChangeRecord>> GetHistoryAsync<T>(
        string id, long? fromVersion = null, long? toVersion = null, CancellationToken ct = default)
        where T : class =>
        _session.GetHistoryAsync<T>(id, fromVersion, toVersion, ct);

    /// <inheritdoc cref="SqliteDocumentSession.GetChangesByCorrelationAsync(Guid, CancellationToken)"/>
    public Task<IReadOnlyList<ChangeRecord>> GetChangesByCorrelationAsync(Guid correlationId, CancellationToken ct = default) =>
        _session.GetChangesByCorrelationAsync(correlationId, ct);

    /// <summary>Ends the snapshot and returns the connection.</summary>
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
