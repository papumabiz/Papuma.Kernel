// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Robustness;

/// <summary>
/// The two implementations of patch semantics — the in-process <c>JsonPatchApplier</c> (SQLite) and the
/// nested <c>jsonb_set</c> expression plus <c>EnsureTargetsExist</c> (PostgreSQL) — fed the same thousands of
/// random documents and operation lists must agree. This is how the first divergences were found by hand
/// (lost increments, overwritten strings); here it runs on every build.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PatchDifferentialTests(PostgresFixture fixture)
{
    // "0" matters: below an array it addresses an element in PostgreSQL, but a property in the contract.
    private static readonly string[] Segments = ["a", "b", "c", "0"];

    private static JsonNode? RandomValue(JsonGenerator g, int depth)
    {
        if (depth > 0 && g.Chance(0.35))
        {
            var obj = new JsonObject();
            foreach (var key in Segments.Where(_ => g.Chance(0.6)))
            {
                obj[key] = RandomValue(g, depth - 1);
            }

            return obj;
        }

        return g.Next(8) switch
        {
            0 => JsonValue.Create(g.Next(-5, 6)),
            1 => JsonNode.Parse(g.Pick(new[] { "1.5", "0.1", "-2.25", "100", "1e3", "12345678901234567890" })),
            2 => JsonValue.Create(g.Pick(new[] { "5", "abc", "", "é" })),
            3 => JsonValue.Create(g.Chance(0.5)),
            4 => null,
            5 => new JsonArray(JsonValue.Create(1), JsonValue.Create("x")),
            _ => JsonValue.Create(g.Next(0, 100)),
        };
    }

    private static JsonObject RandomDocument(JsonGenerator g)
    {
        var doc = new JsonObject();
        foreach (var key in Segments.Where(_ => g.Chance(0.7)))
        {
            doc[key] = RandomValue(g, 2);
        }

        return doc;
    }

    private static List<PatchOperation> RandomOperations(JsonGenerator g)
    {
        var operations = new List<PatchOperation>();
        for (var i = g.Next(1, 5); i > 0; i--)
        {
            var path = Enumerable.Range(0, g.Next(1, 4)).Select(_ => g.Pick(Segments)).ToArray();
            operations.Add(g.Next(3) switch
            {
                0 => new PatchOperation(PatchOperationKind.Set, path, RandomValue(g, 1), IncrementBy: 0),
                1 => new PatchOperation(PatchOperationKind.Remove, path, Value: null, IncrementBy: 0),
                _ => new PatchOperation(PatchOperationKind.Increment, path, Value: null, g.Next(-3, 4)),
            });
        }

        return operations;
    }

    private static (JsonNode? After, string? Error) InProcess(JsonObject before, List<PatchOperation> operations)
    {
        var document = (JsonObject)before.DeepClone();
        try
        {
            JsonPatchApplier.Apply(document, operations, "Doc", "1");
            return (document, null);
        }
        catch (NonNumericIncrementException)
        {
            return (null, "not-numeric");
        }
        catch (PatchTargetMissingException)
        {
            return (null, "parent-missing");
        }
    }

    private async Task<(JsonNode? After, string? Error)> InPostgres(JsonObject before, List<PatchOperation> operations)
    {
        await using var cmd = fixture.DataSource.CreateCommand();
        var expression = DocumentSession.BuildPatchExpression(cmd, operations);
        cmd.CommandText = $"SELECT ({expression})::text FROM (SELECT @doc AS data) AS t";
        cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Jsonb) { Value = before.ToJsonString() });
        try
        {
            var text = (string?)await cmd.ExecuteScalarAsync();

            // As in the session: SQL cannot raise for a missing parent, the old state is checked afterwards.
            JsonPatchApplier.EnsureTargetsExist(before, operations, "Doc", "1");
            return (JsonNode.Parse(text!), null);
        }
        catch (PostgresException ex) when (DocumentSession.TranslatePatchError(ex, "Doc", "1") is { } translated)
        {
            return (null, translated is NonNumericIncrementException ? "not-numeric" : "parent-missing");
        }
        catch (PatchTargetMissingException)
        {
            return (null, "parent-missing");
        }
    }

    private static string Describe(JsonObject before, List<PatchOperation> operations) =>
        $"before={before.ToJsonString()}\nops=[{string.Join(", ", operations.Select(o => $"{o.Kind}({string.Join('.', o.PathSegments)}{(o.Kind == PatchOperationKind.Set ? "=" + (o.Value?.ToJsonString() ?? "null") : o.Kind == PatchOperationKind.Increment ? "+" + o.IncrementBy : string.Empty)})"))}]";

    [Fact]
    public async Task BothImplementations_AgreeOnSuccessAndOnTheResultingDocument_ForRandomPatches()
    {
        var mismatches = new List<string>();
        var errorKindDiffers = 0;
        var failures = 0;
        const int cases = 2500;

        for (var seed = 1; seed <= cases; seed++)
        {
            var g = new JsonGenerator(seed);
            var before = RandomDocument(g);
            var operations = RandomOperations(g);

            var (inProcessAfter, inProcessError) = InProcess(before, operations);
            var (postgresAfter, postgresError) = await InPostgres(before, operations);

            if (inProcessError is not null)
            {
                failures++;
            }

            if (inProcessError is null != (postgresError is null))
            {
                mismatches.Add($"seed {seed}: in-process {(inProcessError ?? "ok")} vs PostgreSQL {(postgresError ?? "ok")}\n{Describe(before, operations)}");
            }
            else if (inProcessError is null)
            {
                if (!JsonNode.DeepEquals(inProcessAfter, postgresAfter))
                {
                    mismatches.Add($"seed {seed}: results differ\nin-process={inProcessAfter?.ToJsonString()}\npostgres  ={postgresAfter?.ToJsonString()}\n{Describe(before, operations)}");
                }
            }
            else if (inProcessError != postgresError)
            {
                errorKindDiffers++;
            }
        }

        Assert.True(failures > cases / 10, $"the generator should exercise failures too (saw {failures} of {cases})");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches (error kind differed in {errorKindDiffers} more):\n{string.Join("\n---\n", mismatches.Take(5))}");
    }
}
