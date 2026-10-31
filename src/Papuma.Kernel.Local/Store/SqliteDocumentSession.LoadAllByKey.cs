// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Linq.Expressions;
using System.Text;

using Papuma.Kernel.Model;
using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Store;

public sealed partial class SqliteDocumentSession
{
    /// <summary>
    /// Loads the documents behind a declared single-field key (ADR-006), one page at a time:
    /// the multi-document counterpart of <c>LoadByKeyAsync</c>, for lookup keys that are not
    /// unique. Unique and lookup keys are both allowed; an undeclared key throws.
    /// Documents come back in ascending ordinal (<c>BINARY</c>, byte-wise) <c>id</c> order,
    /// deserialized exactly as <see cref="LoadAsync{T}"/> does and in clear text. The read runs in
    /// the session's transaction, so it sees the session's own uncommitted writes.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="key">The key property (must be declared via attribute or fluent config).</param>
    /// <param name="value">The key value to match.</param>
    /// <param name="limit">The page size, from 1 to <see cref="KeyMetadata.MaxLoadAllLimit"/>.</param>
    /// <param name="afterId">The last id of the previous page, or <c>null</c> for the first page.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<DocumentResult<T>>> LoadAllByKeyAsync<T>(
        Expression<Func<T, object?>> key,
        object value,
        int limit,
        string? afterId = null,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return LoadAllByKeyCoreAsync<T>(ResolveDeclaredKey(key), [value], limit, afterId, ct);
    }

    /// <summary>
    /// Composite-key overload of <c>LoadAllByKeyAsync</c> (ADR-020): one value per key
    /// component, in declaration order.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="key">The declared key, as declared: an anonymous type of its properties.</param>
    /// <param name="values">One value per key component, in declaration order.</param>
    /// <param name="limit">The page size, from 1 to <see cref="KeyMetadata.MaxLoadAllLimit"/>.</param>
    /// <param name="afterId">The last id of the previous page, or <c>null</c> for the first page.</param>
    /// <param name="ct">A cancellation token.</param>
    public Task<IReadOnlyList<DocumentResult<T>>> LoadAllByKeyAsync<T>(
        Expression<Func<T, object?>> key,
        IReadOnlyList<object> values,
        int limit,
        string? afterId = null,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values);
        var keyMetadata = ResolveDeclaredKey(key, singleFieldOnly: false);
        if (values.Count != keyMetadata.Paths.Count)
        {
            throw new ArgumentException(
                $"Key {keyMetadata.Path} has {keyMetadata.Paths.Count} components; {values.Count} values were given.",
                nameof(values));
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
            {
                throw new ArgumentException($"Key component {keyMetadata.Paths[i]} has no value.", nameof(values));
            }
        }

        return LoadAllByKeyCoreAsync<T>(keyMetadata, values, limit, afterId, ct);
    }

    private async Task<IReadOnlyList<DocumentResult<T>>> LoadAllByKeyCoreAsync<T>(
        KeyMetadata keyMetadata,
        IReadOnlyList<object> values,
        int limit,
        string? afterId,
        CancellationToken ct)
        where T : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, KeyMetadata.MaxLoadAllLimit);
        if (afterId is not null)
        {
            InputValidator.ValidateDocumentId(afterId);
        }

        var metadata = _model.GetRequired<T>();
        var (conn, tx) = await EnsureTransactionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = BuildLoadAllByKeySql(
            metadata.Name, [.. keyMetadata.ComponentSegments.Select(ValidatedKeyJsonPath)], afterId is not null);
        AddScopeParameters(cmd, metadata.Name);
        for (var i = 0; i < values.Count; i++)
        {
            cmd.Parameters.AddWithValue($"value{i}", ToKeyComparisonValue(values[i]));
        }

        if (afterId is not null)
        {
            cmd.Parameters.AddWithValue("afterId", afterId);
        }

        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<DocumentResult<T>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var document = DeserializeDocument<T>(
                metadata, reader.GetString(0), reader.GetString(1), reader.GetInt32(3));
            results.Add(new DocumentResult<T>(document, reader.GetInt64(2)));
        }

        return results;
    }

    /// <summary>
    /// The paged key read. Three things keep SQLite on the declared key index (verified by
    /// <c>SqliteLoadAllByKeyTests.QueryShape_UsesTheDeclaredKeyIndex</c>): the JSON paths are
    /// literals textually identical to the index expressions (see <c>LoadByKeyCoreAsync</c>); the
    /// document type is a validated literal too, because the key index is partial
    /// (<c>WHERE document_type = '…'</c>) and the planner cannot match it against a bound
    /// parameter; and the id is wrapped in a unary plus, otherwise <c>id &gt; ?</c> / <c>ORDER BY id</c>
    /// pull the planner onto the primary key and the key predicate degrades to a filter.
    /// The index does not deliver id order, so the engine sorts the matches — <c>BINARY</c>
    /// (byte-wise) here, <c>COLLATE "C"</c> on PostgreSQL.
    /// </summary>
    internal static string BuildLoadAllByKeySql(string documentType, IReadOnlyList<string> jsonPaths, bool hasAfterId)
    {
        InputValidator.ValidateDocumentType(documentType);

        var predicates = new StringBuilder();
        for (var i = 0; i < jsonPaths.Count; i++)
        {
            predicates.Append(CultureInfo.InvariantCulture, $" AND json_extract(data, '{jsonPaths[i]}') = @value{i}");
        }

        if (hasAfterId)
        {
            predicates.Append(" AND +id > @afterId");
        }

        return $"""
            SELECT id, data, version, schema_version
            FROM document
            WHERE scope = @scope AND tenant_id = @tenantId
              AND document_type = '{documentType}'{predicates}
            ORDER BY +id
            LIMIT @limit
            """;
    }
}
