// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Papuma.Kernel.Benchmarks;

/// <summary>The input pairs of the diff pipeline benchmarks.</summary>
public enum DiffScenario
{
    // Representative: both sides in PostgreSQL jsonb text form, as the sessions read them.
    Small1Change,
    Medium2Changes,
    LargeFewChanges,
    LargeManyChanges,
    Deep60,
    LargeScalarArray,
    ArrayOfObjectsFieldChange,
    ArrayInsertFront,
    Identical,
    Payload20Kb,
    Unrelated,
    Insert,

    // Adversarial: the after-side is spelled so the byte comparison cannot skip unchanged parts.
    AdvReorderedKeys,
    AdvRespelledNumbers,
    AdvPrettyPrinted,
    AdvWideShuffled,
    AdvEscapedUnicode,
}

/// <summary>
/// Deterministic before/after texts. Representative scenarios are written in the text form PostgreSQL gives back
/// for <c>jsonb</c> (keys by UTF-8 length then bytes, <c>", "</c>/<c>": "</c> separators, literal non-ASCII —
/// pinned by <c>Utf8DiffJsonbTests</c>); adversarial ones defeat the prototype's fast paths on purpose.
/// </summary>
public static class DiffScenarios
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    /// <summary>Gets whether the scenario is an insert (no before-state).</summary>
    public static bool IsInsert(DiffScenario scenario) => scenario == DiffScenario.Insert;

    /// <summary>Builds the texts; <c>Before</c> is <c>null</c> for an insert.</summary>
    public static (string? Before, string After) Build(DiffScenario scenario)
    {
        var random = new Random(42);
        switch (scenario)
        {
            case DiffScenario.Small1Change:
            {
                var before = Flat(random, 20);
                var after = Clone(before);
                after["f3"] = "changed";
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.Medium2Changes:
                return Changed(Wide(random, 200), 2);
            case DiffScenario.LargeFewChanges:
                return Changed(Wide(random, 5_000), 3);
            case DiffScenario.LargeManyChanges:
                return Changed(Wide(random, 5_000), 2_500);
            case DiffScenario.Deep60:
            {
                var before = new JsonObject();
                var current = before;
                for (var i = 0; i < 59; i++)
                {
                    current["v"] = i;
                    current["s"] = $"level-{i}";
                    var child = new JsonObject();
                    current["child"] = child;
                    current = child;
                }

                current["leaf"] = "x";
                var after = Clone(before);
                var target = after;
                for (var i = 0; i < 59; i++)
                {
                    target = target["child"]!.AsObject();
                }

                target["leaf"] = "y";
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.LargeScalarArray:
            {
                var before = Flat(random, 10);
                var values = new JsonArray();
                for (var i = 0; i < 5_000; i++)
                {
                    values.Add(random.Next());
                }

                before["values"] = values;
                var after = Clone(before);
                after["values"]![2_500] = -1;
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.ArrayOfObjectsFieldChange:
            {
                var before = WithItems(random, 1_000);
                var after = Clone(before);
                after["items"]![500]!["qty"] = 12_345;
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.ArrayInsertFront:
            {
                var before = WithItems(random, 1_000);
                var after = Clone(before);
                after["items"]!.AsArray().Insert(0, Item(random, 99_999));
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.Identical:
            {
                var document = Wide(random, 5_000);
                return (Jsonb(document), Jsonb(Clone(document)));
            }

            case DiffScenario.Payload20Kb:
            {
                var bytes = new byte[15_000];
                random.NextBytes(bytes);
                var before = Flat(random, 8);
                before["counter"] = 1;
                before["payload"] = Convert.ToBase64String(bytes);
                var after = Clone(before);
                after["counter"] = 2;
                return (Jsonb(before), Jsonb(after));
            }

            case DiffScenario.Unrelated:
                return (Jsonb(Wide(random, 2_000)), Jsonb(Wide(new Random(7), 2_000, prefix: "g")));
            case DiffScenario.Insert:
                return (null, Jsonb(Wide(random, 5_000)));
            case DiffScenario.AdvReorderedKeys:
            {
                var before = Wide(random, 5_000);
                var after = Clone(before);
                Touch(after, 3);
                return (Jsonb(before), Shuffled(after, random).ToJsonString(Relaxed));
            }

            case DiffScenario.AdvRespelledNumbers:
            {
                var before = Wide(random, 5_000);
                var after = Clone(before);
                Touch(after, 3);
                return (Jsonb(before), Respelled(Jsonb(after)));
            }

            case DiffScenario.AdvPrettyPrinted:
            {
                var before = Wide(random, 5_000);
                var after = Clone(before);
                Touch(after, 3);
                return (Jsonb(before), after.ToJsonString(Pretty));
            }

            case DiffScenario.AdvWideShuffled:
            {
                var before = Flat(random, 20_000);
                var after = Clone(before);
                after["f777"] = "changed";
                return (Jsonb(before), Shuffled(after, random).ToJsonString(Relaxed));
            }

            case DiffScenario.AdvEscapedUnicode:
            {
                var before = new JsonObject();
                for (var i = 0; i < 3_000; i++)
                {
                    before[$"schlüssel-{i}"] = $"wert-ä-ö-ü-日本-{random.Next(1_000_000)}";
                }

                var after = Clone(before);
                after["schlüssel-17"] = "geändert";
                return (Jsonb(before), after.ToJsonString()); // default encoder: every non-ASCII char as \uXXXX
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    /// <summary>Writes a node the way PostgreSQL prints <c>jsonb</c>.</summary>
    public static string Jsonb(JsonNode? node)
    {
        var builder = new StringBuilder();
        WriteJsonb(node, builder);
        return builder.ToString();
    }

    private static void WriteJsonb(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var (key, value) in obj.OrderBy(p => p.Key, JsonbKeyOrder.Instance))
                {
                    builder.Append(first ? string.Empty : ", ").Append(JsonSerializer.Serialize(key, Relaxed)).Append(": ");
                    WriteJsonb(value, builder);
                    first = false;
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    builder.Append(i == 0 ? string.Empty : ", ");
                    WriteJsonb(array[i], builder);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(node.ToJsonString(Relaxed));
                break;
        }
    }

    private static (string, string) Changed(JsonObject before, int changes)
    {
        var after = Clone(before);
        Touch(after, changes);
        return (Jsonb(before), Jsonb(after));
    }

    // Integers become "n.0": the same number (no diff entry), but different bytes on every numeric leaf.
    private static string Respelled(string jsonb)
    {
        var node = JsonNode.Parse(jsonb)!;
        Respell(node);
        return node.ToJsonString(Relaxed);
    }

    private static void Respell(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
                    {
                        obj[key] = JsonNode.Parse(value.ToJsonString() + ".0");
                    }
                    else if (obj[key] is { } child)
                    {
                        Respell(child);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
                    {
                        array[i] = JsonNode.Parse(value.ToJsonString() + ".0");
                    }
                    else if (array[i] is { } child)
                    {
                        Respell(child);
                    }
                }

                break;
        }
    }

    private static JsonObject Shuffled(JsonObject source, Random random)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source.OrderBy(_ => random.Next()))
        {
            result[key] = value is JsonObject nested ? Shuffled(nested, random) : value?.DeepClone();
        }

        return result;
    }

    private static JsonObject Clone(JsonObject source) => source.DeepClone().AsObject();

    private static JsonObject Flat(Random random, int count)
    {
        var result = new JsonObject();
        for (var i = 0; i < count; i++)
        {
            result[$"f{i}"] = Scalar(random, i);
        }

        return result;
    }

    // ~60% flat leaves, ~20% in objects nested three levels, ~20% in small atomic arrays.
    private static JsonObject Wide(Random random, int leaves, string prefix = "f")
    {
        var result = new JsonObject();
        for (var i = 0; i < leaves * 6 / 10; i++)
        {
            result[$"{prefix}{i}"] = Scalar(random, i);
        }

        for (var i = 0; i < leaves / 25; i++)
        {
            result[$"{prefix}n{i}"] = new JsonObject
            {
                ["a0"] = Scalar(random, 0),
                ["a1"] = Scalar(random, 1),
                ["inner"] = new JsonObject
                {
                    ["b0"] = Scalar(random, 2),
                    ["b1"] = Scalar(random, 0),
                    ["deep"] = new JsonObject { ["c0"] = Scalar(random, 1) },
                },
            };
        }

        for (var i = 0; i < leaves / 50; i++)
        {
            var array = new JsonArray();
            for (var j = 0; j < 10; j++)
            {
                array.Add(Scalar(random, j));
            }

            result[$"{prefix}a{i}"] = array;
        }

        return result;
    }

    private static void Touch(JsonObject document, int count)
    {
        foreach (var key in document.Where(p => p.Value is JsonValue).Select(p => p.Key).Take(count).ToList())
        {
            document[key] = $"changed-{key}";
        }
    }

    private static JsonObject WithItems(Random random, int count)
    {
        var result = Flat(random, 10);
        var items = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            items.Add(Item(random, i));
        }

        result["items"] = items;
        return result;
    }

    private static JsonObject Item(Random random, int id) => new()
    {
        ["id"] = $"item-{id}",
        ["sku"] = $"SKU-{random.Next(100_000)}",
        ["qty"] = random.Next(100),
        ["price"] = JsonNode.Parse((random.Next(10_000) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)),
        ["tags"] = new JsonArray("a", "b"),
        ["active"] = random.Next(2) == 1,
    };

    private static JsonNode Scalar(Random random, int salt) => (salt % 3) switch
    {
        0 => JsonValue.Create($"value-{random.Next(1_000_000)}"),
        1 => JsonValue.Create(random.Next(1_000_000)),
        _ => JsonValue.Create(random.NextDouble() > 0.5),
    };

    private sealed class JsonbKeyOrder : IComparer<string>
    {
        public static readonly JsonbKeyOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Encoding.UTF8.GetBytes(x!);
            var b = Encoding.UTF8.GetBytes(y!);
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : a.AsSpan().SequenceCompareTo(b);
        }
    }
}
