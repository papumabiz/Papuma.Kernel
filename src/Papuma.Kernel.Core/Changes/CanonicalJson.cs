// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Changes;

/// <summary>
/// Thrown when a value has no canonical form and therefore cannot be hashed by the keyed
/// <c>Hash</c> policy (ADR-025): a number whose canonical text would be longer than 1024
/// characters, a malformed number, a string with an unpaired surrogate, or a value that has no JSON form. The write is rolled back.
/// </summary>
public sealed class CanonicalJsonException : Exception
{
    /// <summary>Gets the stable error code (<c>number-too-large</c>, <c>invalid-number</c>, <c>invalid-string</c>, <c>invalid-value</c>, <c>too-deep</c>).</summary>
    public string Code { get; }

    internal CanonicalJsonException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// The canonical JSON form of <c>docs/spec/canonical-json.md</c>: a byte-exact serialization
/// of a JSON value that depends only on the value, not on the serializer — the input of the
/// keyed hash (ADR-025). Sorted keys (UTF-16 code units, as RFC 8785), no whitespace, minimal
/// string escaping, and numbers normalized by value (no exponent, no redundant zeros), so
/// <c>1</c>, <c>1.0</c> and <c>1e0</c> serialize alike — consistent with the diff rule that
/// they are equal.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>The longest canonical number text accepted, sign included. Bounds the work an exponent such as <c>1e1000000000</c> can cause.</summary>
    public const int MaxNumberLength = 1024;

    // An exponent with more digits than this cannot yield a number within the limit.
    private const int MaxExponentDigits = 12;

    /// <summary>Returns the canonical text of <paramref name="node"/> (<c>null</c> is JSON null).</summary>
    public static string Serialize(JsonNode? node)
    {
        if (JsonLimits.ExceedsMaxDepth(node))
        {
            throw new CanonicalJsonException(
                "too-deep", $"The value is nested deeper than {JsonLimits.MaxDepth} levels.");
        }

        var builder = new StringBuilder();
        Append(builder, node);
        return builder.ToString();
    }

    /// <summary>Returns the canonical text as UTF-8 bytes.</summary>
    public static byte[] ToUtf8(JsonNode? node) => Encoding.UTF8.GetBytes(Serialize(node));

    private static void Append(StringBuilder builder, JsonNode? node)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;

            case JsonObject obj:
                var keys = obj.Select(p => p.Key).ToList();
                keys.Sort(StringComparer.Ordinal);
                builder.Append('{');
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    AppendString(builder, keys[i]);
                    builder.Append(':');
                    Append(builder, obj[keys[i]]);
                }

                builder.Append('}');
                break;

            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    Append(builder, array[i]);
                }

                builder.Append(']');
                break;

            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        if (value.TryGetValue<string>(out var text))
                        {
                            AppendString(builder, text);
                        }
                        else
                        {
                            Append(builder, Reparse(value)); // JsonValue.Create(guid): a string kind with a non-string payload
                        }

                        break;
                    case JsonValueKind.Number:
                        builder.Append(NormalizeNumber(value.ToJsonString()));
                        break;
                    case JsonValueKind.True:
                        builder.Append("true");
                        break;
                    case JsonValueKind.False:
                        builder.Append("false");
                        break;
                    case JsonValueKind.Null:
                        builder.Append("null");
                        break;
                    case JsonValueKind.Object:
                    case JsonValueKind.Array:
                        Append(builder, Reparse(value)); // JsonValue.Create(poco): a container hiding in a leaf node
                        break;
                    default:
                        throw new CanonicalJsonException(
                            "invalid-value", "A JSON value has no JSON form (kind Undefined) and cannot be canonicalized.");
                }

                break;
        }
    }

    // A JsonValue built from a CLR object is canonicalized by what it serializes to. The parse (default depth
    // limit 64) yields plain JsonObject/JsonArray/JsonValue nodes, so this cannot recurse into itself.
    private static JsonNode? Reparse(JsonValue value)
    {
        try
        {
            return JsonNode.Parse(value.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new CanonicalJsonException(
                "invalid-value", $"A JSON value of CLR type {value.GetType().Name} cannot be serialized to canonical JSON.");
        }
    }

    private static void AppendString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(ch).Append(value[++i]);
                continue;
            }

            if (char.IsSurrogate(ch))
            {
                throw new CanonicalJsonException("invalid-string", "A string contains an unpaired surrogate.");
            }

            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                case < ' ': builder.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: builder.Append(ch); break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Normalizes the text of a JSON number to its value: no exponent, no leading or trailing
    /// zeros, <c>-0</c> is <c>0</c>. Works on the text, so it has arbitrary precision.
    /// </summary>
    internal static string NormalizeNumber(string text)
    {
        var pos = 0;
        var negative = pos < text.Length && text[pos] == '-';
        if (negative)
        {
            pos++;
        }

        var integer = ReadDigits(text, ref pos);
        if (integer.Length == 0 || (integer.Length > 1 && integer[0] == '0'))
        {
            throw Invalid(text);
        }

        var fraction = string.Empty;
        if (pos < text.Length && text[pos] == '.')
        {
            pos++;
            fraction = ReadDigits(text, ref pos);
            if (fraction.Length == 0)
            {
                throw Invalid(text);
            }
        }

        var exponentText = string.Empty;
        var exponentNegative = false;
        if (pos < text.Length && text[pos] is 'e' or 'E')
        {
            pos++;
            if (pos < text.Length && text[pos] is '+' or '-')
            {
                exponentNegative = text[pos] == '-';
                pos++;
            }

            exponentText = ReadDigits(text, ref pos);
            if (exponentText.Length == 0)
            {
                throw Invalid(text);
            }
        }

        if (pos != text.Length)
        {
            throw Invalid(text);
        }

        var digits = (integer + fraction).TrimStart('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        var exponentDigits = exponentText.TrimStart('0');
        if (exponentDigits.Length > MaxExponentDigits)
        {
            throw TooLarge();
        }

        var exponent = exponentDigits.Length == 0 ? 0L : long.Parse(exponentDigits, System.Globalization.CultureInfo.InvariantCulture);
        exponent = (exponentNegative ? -exponent : exponent) - fraction.Length;

        var stripped = digits.TrimEnd('0');
        exponent += digits.Length - stripped.Length;
        digits = stripped;

        // Exact length of the canonical text, computed before it is built.
        long length;
        if (exponent >= 0)
        {
            length = digits.Length + exponent;
        }
        else if (digits.Length > -exponent)
        {
            length = digits.Length + 1;
        }
        else
        {
            length = 2 - exponent;
        }

        if (length + (negative ? 1 : 0) > MaxNumberLength)
        {
            throw TooLarge();
        }

        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }

        if (exponent >= 0)
        {
            builder.Append(digits).Append('0', (int)exponent);
        }
        else if (digits.Length > -exponent)
        {
            var point = digits.Length + (int)exponent;
            builder.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point);
        }
        else
        {
            builder.Append("0.").Append('0', (int)(-exponent - digits.Length)).Append(digits);
        }

        return builder.ToString();
    }

    private static string ReadDigits(string text, ref int pos)
    {
        var start = pos;
        while (pos < text.Length && text[pos] is >= '0' and <= '9')
        {
            pos++;
        }

        return text[start..pos];
    }

    private static CanonicalJsonException Invalid(string text) =>
        new("invalid-number", $"'{text}' is not a JSON number.");

    private static CanonicalJsonException TooLarge() =>
        new("number-too-large", $"The canonical text of the number would exceed {MaxNumberLength} characters.");
}
