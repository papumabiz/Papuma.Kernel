// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// The result of <see cref="Utf8JsonDiffEngine"/>: the entries ordered by path, and the raw UTF-8 text of every
/// changed value in one compact buffer that this instance owns. Nothing refers to the input documents.
/// </summary>
internal sealed class Utf8Diff
{
    private readonly Utf8DiffEntry[] _entries;
    private readonly byte[] _values;

    private Utf8Diff(Utf8DiffEntry[] entries, byte[] values)
    {
        _entries = entries;
        _values = values;
    }

    /// <summary>Gets the entries, ordered ordinally by path.</summary>
    public IReadOnlyList<Utf8DiffEntry> Entries => _entries;

    /// <summary>Gets whether the documents are equal.</summary>
    public bool IsEmpty => _entries.Length == 0;

    /// <summary>Gets the number of bytes held for the changed values.</summary>
    public int ValueBytes => _values.Length;

    /// <summary>Gets the raw JSON text of the old value. Only valid when <see cref="Utf8DiffEntry.HasOld"/>.</summary>
    public ReadOnlySpan<byte> OldValue(in Utf8DiffEntry entry) => Slice(entry.HasOld, entry.OldStart, entry.OldLength);

    /// <summary>Gets the raw JSON text of the new value. Only valid when <see cref="Utf8DiffEntry.HasNew"/>.</summary>
    public ReadOnlySpan<byte> NewValue(in Utf8DiffEntry entry) => Slice(entry.HasNew, entry.NewStart, entry.NewLength);

    /// <summary>Writes the tracked wire form (ADR-004) — <c>{"path": {"old": …, "new": …}}</c> — without policies.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        foreach (var entry in _entries)
        {
            writer.WritePropertyName(entry.Path);
            writer.WriteStartObject();
            if (entry.HasOld)
            {
                writer.WritePropertyName("old");
                // The buffer is private and immutable, filled from validated documents: no re-validation needed.
                writer.WriteRawValue(OldValue(entry), skipInputValidation: true);
            }

            if (entry.HasNew)
            {
                writer.WritePropertyName("new");
                writer.WriteRawValue(NewValue(entry), skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Returns the tracked wire form as UTF-8 JSON.</summary>
    public byte[] ToUtf8Json()
    {
        var buffer = new ArrayBufferWriter<byte>(256 + _values.Length);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Materializes the public <see cref="DocumentDiff"/>, parsing every value into its own node.</summary>
    public DocumentDiff ToDocumentDiff()
    {
        var options = new JsonDocumentOptions { MaxDepth = JsonLimits.MaxDepth };
        var entries = new SortedDictionary<string, DiffEntry>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            var old = entry.HasOld ? JsonNode.Parse(OldValue(entry), documentOptions: options) : null;
            var @new = entry.HasNew ? JsonNode.Parse(NewValue(entry), documentOptions: options) : null;
            entries[entry.Path] = (entry.HasOld, entry.HasNew) switch
            {
                (true, true) => DiffEntry.Changed(old, @new),
                (true, false) => DiffEntry.Removed(old),
                _ => DiffEntry.Added(@new),
            };
        }

        return new DocumentDiff(entries);
    }

    /// <summary>Copies the pending values into one exactly sized buffer, ordered like the entries.</summary>
    internal static Utf8Diff Create(List<Utf8JsonDiffEngine.PendingEntry> pending)
    {
        pending.Sort(static (x, y) => string.CompareOrdinal(x.Path, y.Path));

        var total = 0;
        foreach (var entry in pending)
        {
            total += RawLength(entry.Old) + RawLength(entry.New);
        }

        var values = total == 0 ? [] : new byte[total];
        var entries = new Utf8DiffEntry[pending.Count];
        var offset = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var (oldStart, oldLength) = Copy(pending[i].Old, values, ref offset);
            var (newStart, newLength) = Copy(pending[i].New, values, ref offset);
            entries[i] = new Utf8DiffEntry(pending[i].Path, oldStart, oldLength, newStart, newLength);
        }

        return new Utf8Diff(entries, values);
    }

    private static int RawLength(JsonElement? value) =>
        value is { } element ? JsonMarshal.GetRawUtf8Value(element).Length : 0;

    private static (int Start, int Length) Copy(JsonElement? value, byte[] into, ref int offset)
    {
        if (value is not { } element)
        {
            return (0, -1);
        }

        var raw = JsonMarshal.GetRawUtf8Value(element);
        var start = offset;
        raw.CopyTo(into.AsSpan(offset));
        offset += raw.Length;
        return (start, raw.Length);
    }

    private ReadOnlySpan<byte> Slice(bool present, int start, int length)
    {
        if (!present)
        {
            throw new InvalidOperationException("The entry has no value on this side.");
        }

        return _values.AsSpan(start, length);
    }
}

/// <summary>One entry of a <see cref="Utf8Diff"/>; a length of <c>-1</c> marks an absent side.</summary>
internal readonly record struct Utf8DiffEntry(string Path, int OldStart, int OldLength, int NewStart, int NewLength)
{
    /// <summary>Gets whether the field existed before.</summary>
    public bool HasOld => OldLength >= 0;

    /// <summary>Gets whether the field exists after.</summary>
    public bool HasNew => NewLength >= 0;
}
