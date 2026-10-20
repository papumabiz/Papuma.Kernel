// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Core.Tests.Changes;
using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Robustness;

/// <summary>
/// The UTF-8 diff prototype against real PostgreSQL <c>jsonb</c>: the states come back from <c>RETURNING</c>
/// exactly as the sessions read them, the feed is stored as <c>jsonb</c> and read back. Also pins what
/// <c>jsonb</c> normalizes — the assumptions the byte-comparison fast path profits from (it never depends on them).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class Utf8DiffJsonbTests(PostgresFixture fixture)
{
    private const string Setup = """
        CREATE TABLE IF NOT EXISTS public.utf8_diff_document (id int PRIMARY KEY, data jsonb NOT NULL);
        CREATE TABLE IF NOT EXISTS public.utf8_diff_feed (id int PRIMARY KEY, diff jsonb NOT NULL);
        """;

    private sealed record Returned(string OldText, string NewText, byte[] OldUtf8, byte[] NewUtf8, byte[] OldStream, byte[] NewStream);

    [Fact]
    public async Task OnRealJsonb_BothEnginesAgree_TheFeedSurvivesJsonb_AndBothStatesAreRebuilt()
    {
        await ExecuteAsync(Setup + "TRUNCATE public.utf8_diff_document, public.utf8_diff_feed;");
        var cases = DiffComparison.Cases(ciDefault: 300);
        var failures = new List<string>();
        var streamNotVersionedText = 0;

        for (var seed = 1; seed <= cases; seed++)
        {
            var g = new JsonGenerator(seed);
            var before = g.Object(3);
            var after = seed % 10 == 0 ? g.Object(3) : g.Mutate(before);
            var beforeText = DiffComparison.Spell(before, g.Pick(Enum.GetValues<JsonSpelling>()), g);
            var afterText = DiffComparison.Spell(after, g.Pick(Enum.GetValues<JsonSpelling>()), g);

            var returned = await UpdateReturningAsync(seed, beforeText, afterText);
            // The provider streams jsonb in its binary format: a version byte (1), then the UTF-8 text. A session
            // reading bytes instead of ::text has to skip that byte.
            if (returned.OldStream is not [1, ..] || !returned.OldStream.AsSpan(1).SequenceEqual(returned.OldUtf8)
                || returned.NewStream is not [1, ..] || !returned.NewStream.AsSpan(1).SequenceEqual(returned.NewUtf8))
            {
                streamNotVersionedText++;
            }

            var oldState = (JsonObject)JsonNode.Parse(returned.OldText)!;
            var newState = (JsonObject)JsonNode.Parse(returned.NewText)!;
            var expected = JsonDiffEngine.Diff(oldState, newState);

            var fromText = Utf8JsonDiffEngine.Diff(returned.OldText, returned.NewText);
            var fromUtf8 = Utf8JsonDiffEngine.Diff(returned.OldUtf8.AsMemory(), returned.NewUtf8.AsMemory());
            var fromStream = Utf8JsonDiffEngine.Diff(returned.OldStream.AsMemory(1), returned.NewStream.AsMemory(1));
            var storedFeed = await StoreFeedAsync(seed, fromUtf8.ToUtf8Json());

            var failure = Labeled("text", DiffComparison.Difference(expected, fromText.ToDocumentDiff()))
                ?? Labeled("utf8", DiffComparison.Difference(expected, fromUtf8.ToDocumentDiff()))
                ?? Labeled("stream", DiffComparison.Difference(expected, fromStream.ToDocumentDiff()))
                ?? Labeled("feed via jsonb", DiffComparison.Difference(expected, DocumentDiff.FromJson((JsonObject)JsonNode.Parse(storedFeed)!)))
                ?? DiffComparison.Reconstruction(oldState, newState, fromUtf8.ToDocumentDiff());
            if (failure is not null)
            {
                failures.Add($"seed {seed}: {failure}\n  old={returned.OldText}\n  new={returned.NewText}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} cases failed:\n{string.Join("\n", failures.Take(5))}");
        Assert.Equal(0, streamNotVersionedText);
    }

    [Fact]
    public async Task Jsonb_WritesTheSameDocumentTheSameWay_RegardlessOfKeyOrderWhitespaceAndEscaping()
    {
        var cases = DiffComparison.Cases(ciDefault: 200);
        var failures = new List<string>();
        var numberSpellingSurvived = 0;

        for (var seed = 1; seed <= cases; seed++)
        {
            var g = new JsonGenerator(seed);
            var document = g.Object(3);

            var compact = await AsJsonbTextAsync(DiffComparison.Spell(document, JsonSpelling.Compact, g));
            var shuffledPretty = await AsJsonbTextAsync(DiffComparison.Spell(Shuffled(document, g), JsonSpelling.Pretty, g));
            var respelled = await AsJsonbTextAsync(DiffComparison.Spell(document, JsonSpelling.Equivalent, g));

            if (compact != shuffledPretty)
            {
                failures.Add($"seed {seed}: key order / whitespace / escaping changed the jsonb text\n  {compact}\n  {shuffledPretty}");
            }

            if (compact != respelled)
            {
                numberSpellingSurvived++; // e.g. 1.0 vs 1: jsonb keeps the scale, so the text differs ...
            }

            if (!Utf8JsonDiffEngine.Diff(compact, respelled).IsEmpty)
            {
                failures.Add($"seed {seed}: respelled numbers produced a diff\n  {compact}\n  {respelled}"); // ... the diff does not
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
        Assert.True(numberSpellingSurvived > 0, "expected jsonb to keep some number spellings apart (scale)");
    }

    [Theory]
    [InlineData("""{"a":1.0}""", """{"a": 1.0}""")] // the scale is kept ...
    [InlineData("""{"a":0.10}""", """{"a": 0.10}""")]
    [InlineData("""{"a":1e2}""", """{"a": 100}""")] // ... the exponent is resolved
    [InlineData("""{"a":1.5e2}""", """{"a": 150}""")]
    [InlineData("""{"a":1E-2}""", """{"a": 0.01}""")]
    [InlineData("""{"a":-0}""", """{"a": 0}""")]
    [InlineData("""{"a":-0.0}""", """{"a": 0.0}""")]
    [InlineData("""{"a":12345678901234567890}""", """{"a": 12345678901234567890}""")]
    [InlineData("""{"a":1,"a":2}""", """{"a": 2}""")] // the last duplicate wins
    [InlineData("""{"bb":1,"a":2,"ccc":{"z":1,"y":2}}""", """{"a": 2, "bb": 1, "ccc": {"y": 2, "z": 1}}""")] // shorter keys first
    [InlineData("""{"é":1,"z":2,"ab":3}""", """{"z": 2, "ab": 3, "é": 1}""")] // length in UTF-8 bytes, then bytes
    [InlineData("""{"\u00e9":"\u00e9\/\n"}""", """{"é": "é/\n"}""")] // minimal escaping, literal non-ASCII
    [InlineData("""{"x" : [1 ,2]}""", """{"x": [1, 2]}""")]
    public async Task Jsonb_NormalizesTheTextLikeThis(string input, string stored)
    {
        Assert.Equal(stored, await AsJsonbTextAsync(input));
    }

    private async Task<Returned> UpdateReturningAsync(int id, string beforeText, string afterText)
    {
        await using (var insert = fixture.DataSource.CreateCommand(
            "INSERT INTO public.utf8_diff_document (id, data) VALUES (@id, @data)"))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = beforeText });
            await insert.ExecuteNonQueryAsync();
        }

        // The sessions read old.data::text / new.data::text; the UTF-8 forms are what a byte-reading session would get.
        await using var update = fixture.DataSource.CreateCommand("""
            UPDATE public.utf8_diff_document SET data = @data WHERE id = @id
            RETURNING old.data::text, new.data::text,
                      convert_to(old.data::text, 'UTF8'), convert_to(new.data::text, 'UTF8'),
                      old.data, new.data
            """);
        update.Parameters.AddWithValue("id", id);
        update.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb) { Value = afterText });
        await using var reader = await update.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Returned(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<byte[]>(2),
            reader.GetFieldValue<byte[]>(3),
            await ReadStreamAsync(reader, 4),
            await ReadStreamAsync(reader, 5));
    }

    private static async Task<byte[]> ReadStreamAsync(NpgsqlDataReader reader, int ordinal)
    {
        await using var stream = await reader.GetStreamAsync(ordinal);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private async Task<string> StoreFeedAsync(int id, byte[] feedUtf8)
    {
        await using var cmd = fixture.DataSource.CreateCommand(
            "INSERT INTO public.utf8_diff_feed (id, diff) VALUES (@id, @diff) RETURNING diff::text");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(new NpgsqlParameter("diff", NpgsqlDbType.Jsonb) { Value = Encoding.UTF8.GetString(feedUtf8) });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> AsJsonbTextAsync(string json)
    {
        await using var cmd = fixture.DataSource.CreateCommand("SELECT @value::text");
        cmd.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Jsonb) { Value = json });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = fixture.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    // Same JSON value, keys in a random order at every level (numbers untouched).
    private static JsonObject Shuffled(JsonObject source, JsonGenerator g)
    {
        var result = new JsonObject();
        foreach (var (key, value) in source.OrderBy(_ => g.Next(1_000_000)))
        {
            result[key] = value switch
            {
                JsonObject nested => Shuffled(nested, g),
                JsonArray array => new JsonArray(array.Select(item => item is JsonObject o ? Shuffled(o, g) : item?.DeepClone()).ToArray()),
                _ => value?.DeepClone(),
            };
        }

        return result;
    }

    private static string? Labeled(string label, string? failure) => failure is null ? null : $"{label}: {failure}";
}
