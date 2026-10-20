// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Core.Tests.Robustness;

/// <summary>
/// A seeded generator of hostile-but-valid JSON for property tests: keys that contain dots,
/// backslashes, unicode and control characters; strings that look like other values; numbers in many
/// spellings of the same value; nesting; and mutations of an existing document. The same seed always
/// produces the same documents, so a failing case is reproduced by its seed.
/// </summary>
internal sealed class JsonGenerator(int seed)
{
    private static readonly string[] KeyPool =
    [
        "a", "b", "c", "d", "Z", "0", "-", "_", "", "a.b", "a\\", "\\", ".", "..", "x.", ".x", "\\.", "\\\\",
        "é", "日本", "😀", "with space", "k\u0001", "quote\"", "slash/", "tilde~",
    ];

    private static readonly string[] StringPool =
    [
        "", "x", "é", "😀", "\\", "\"", "line\nbreak", "tab\t", "\u2028", "a.b", "0", "true", "null", "{}", "[]", "ÿ",
    ];

    private static readonly string[] NumberPool =
    [
        "0", "-0", "1", "1.0", "1e0", "100", "1E2", "0.1", "0.10", "12345678901234567890", "-1.5e-3", "3.14159",
        "1e21", "0.000001", "9007199254740993", "0.10000000000000001",
    ];

    private readonly Random _random = new(seed);

    public int Seed { get; } = seed;

    public int Next(int maxExclusive) => _random.Next(maxExclusive);

    public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);

    public bool Chance(double probability) => _random.NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    public string Key() => Pick(KeyPool);

    public IReadOnlyList<string> Keys => KeyPool;

    public JsonNode? Scalar() => _random.Next(5) switch
    {
        0 => JsonValue.Create(Pick(StringPool)),
        1 => JsonNode.Parse(Pick(NumberPool)),
        2 => JsonValue.Create(Chance(0.5)),
        3 => null,
        _ => JsonValue.Create(_random.Next(-1000, 1000)),
    };

    public JsonNode? Value(int depth)
    {
        if (depth <= 0)
        {
            return Scalar();
        }

        return _random.Next(6) switch
        {
            0 or 1 => Object(depth - 1),
            2 => Array(depth - 1),
            _ => Scalar(),
        };
    }

    public JsonObject Object(int depth)
    {
        var result = new JsonObject();
        var width = _random.Next(0, 5);
        for (var i = 0; i < width; i++)
        {
            result[Key()] = Value(depth);
        }

        return result;
    }

    public JsonArray Array(int depth)
    {
        var result = new JsonArray();
        var width = _random.Next(0, 4);
        for (var i = 0; i < width; i++)
        {
            result.Add(Value(depth));
        }

        return result;
    }

    /// <summary>Returns a deep copy of <paramref name="source"/> with one to three random edits.</summary>
    public JsonObject Mutate(JsonObject source)
    {
        var copy = (JsonObject)source.DeepClone();
        var edits = _random.Next(1, 4);
        for (var i = 0; i < edits; i++)
        {
            var objects = new List<JsonObject>();
            Collect(copy, objects);
            var target = Pick(objects);
            switch (_random.Next(4))
            {
                case 0: // add or overwrite a key
                    target[Key()] = Value(2);
                    break;
                case 1 when target.Count > 0: // remove a key
                    target.Remove(Pick(target.Select(p => p.Key).ToList()));
                    break;
                case 2 when target.Count > 0: // replace a value, possibly by another type
                    target[Pick(target.Select(p => p.Key).ToList())] = Value(2);
                    break;
                default: // touch a key with the same value (must stay a no-op)
                    if (target.Count > 0)
                    {
                        var key = Pick(target.Select(p => p.Key).ToList());
                        target[key] = target[key]?.DeepClone();
                    }

                    break;
            }
        }

        return copy;
    }

    /// <summary>
    /// Returns a document with the same meaning as <paramref name="source"/>: every object's keys in a
    /// shuffled order and every number written in another spelling of the same value.
    /// </summary>
    public JsonNode? Equivalent(JsonNode? source) => source switch
    {
        JsonObject obj => EquivalentObject(obj),
        JsonArray array => new JsonArray(array.Select(Equivalent).ToArray()),
        JsonValue value when value.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            JsonNode.Parse(Respell(value.ToJsonString())),
        _ => source?.DeepClone(),
    };

    private JsonObject EquivalentObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var (key, child) in source.OrderBy(_ => _random.Next()))
        {
            result[key] = Equivalent(child);
        }

        return result;
    }

    /// <summary>A number text and another spelling of the same value (point moved, zeros added, exponent changed).</summary>
    public string Respell(string number)
    {
        var (negative, digits, exponent) = Decompose(number);
        if (digits.Length == 0)
        {
            return Chance(0.5) ? "0" : "-0.0e5"; // zero in two spellings
        }

        return Spell(negative, digits, exponent);
    }

    /// <summary>Spells <c>±digits × 10^exponent</c> with a random decimal point, trailing zeros and exponent form.</summary>
    public string Spell(bool negative, string digits, long exponent)
    {
        var extraZeros = _random.Next(0, 4);
        var all = digits + new string('0', extraZeros);
        var exp = exponent - extraZeros;
        var fractionDigits = _random.Next(0, all.Length + 1);
        var integerPart = fractionDigits == all.Length ? "0" : all[..(all.Length - fractionDigits)];
        var fraction = all[(all.Length - fractionDigits)..];
        var adjusted = exp + fractionDigits;

        var builder = new StringBuilder();
        if (negative)
        {
            builder.Append('-');
        }

        builder.Append(integerPart);
        if (fraction.Length > 0)
        {
            builder.Append('.').Append(fraction);
        }

        if (adjusted != 0 || Chance(0.3))
        {
            builder.Append(Chance(0.5) ? 'e' : 'E');
            if (adjusted >= 0 && Chance(0.5))
            {
                builder.Append('+');
            }

            builder.Append(adjusted.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>A random non-zero number as <c>(negative, digits without leading zeros, exponent)</c>.</summary>
    public (bool Negative, string Digits, long Exponent) RandomNumber()
    {
        var length = _random.Next(1, 13);
        var digits = new StringBuilder();
        digits.Append((char)('1' + _random.Next(9)));
        for (var i = 1; i < length; i++)
        {
            digits.Append((char)('0' + _random.Next(10)));
        }

        return (Chance(0.4), digits.ToString(), _random.Next(-40, 41));
    }

    private static (bool Negative, string Digits, long Exponent) Decompose(string number)
    {
        var negative = number.StartsWith('-');
        var text = negative ? number[1..] : number;
        long exponent = 0;
        var e = text.IndexOfAny(['e', 'E']);
        if (e >= 0)
        {
            exponent = long.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..e];
        }

        var point = text.IndexOf('.');
        string integer = point < 0 ? text : text[..point];
        string fraction = point < 0 ? string.Empty : text[(point + 1)..];
        var digits = (integer + fraction).TrimStart('0');
        return (negative, digits, exponent - fraction.Length);
    }

    private static void Collect(JsonNode? node, List<JsonObject> into)
    {
        switch (node)
        {
            case JsonObject obj:
                into.Add(obj);
                foreach (var (_, child) in obj)
                {
                    Collect(child, into);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Collect(child, into);
                }

                break;
        }
    }
}
