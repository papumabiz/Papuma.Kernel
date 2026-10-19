// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using Papuma.Kernel.Core.Tests.Spec;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Spec;

/// <summary>
/// Runs <c>docs/spec/vectors/patch.json</c> against the PostgreSQL kernel's real patch
/// expression (<see cref="DocumentSession.BuildPatchExpression"/>) on a real PostgreSQL —
/// no session or table needed, the expression only reads the <c>data</c> column.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PatchVectorPostgresTests(PostgresFixture fixture)
{
    private const string Engine = "postgres";

    public static TheoryData<string> Cases() => PatchVectors.CaseNames();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Patch_MatchesVector(string name)
    {
        var vector = PatchVectors.Load(name);
        var (after, error) = await RunAsync(vector);
        PatchVectors.Assert(vector, Engine, after, error);
    }

    private async Task<(JsonNode? After, string? Error)> RunAsync(JsonObject vector)
    {
        var operations = PatchVectors.Operations(vector);
        await using var cmd = fixture.DataSource.CreateCommand();
        var expression = DocumentSession.BuildPatchExpression(cmd, operations);
        cmd.CommandText = $"SELECT ({expression})::text FROM (SELECT @doc AS data) AS t";
        cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Jsonb)
        {
            Value = ((JsonObject)vector["before"]!).ToJsonString(),
        });

        try
        {
            var text = (string?)await cmd.ExecuteScalarAsync();

            // As in the session: SQL cannot raise for a missing parent, the old state is checked.
            JsonPatchApplier.EnsureTargetsExist((JsonObject)vector["before"]!, operations, "Doc", "1");
            return (JsonNode.Parse(text!), null);
        }
        catch (PostgresException ex) when (DocumentSession.TranslatePatchError(ex, "Doc", "1") is { } translated)
        {
            return (null, translated switch
            {
                NonNumericIncrementException => "not-numeric",
                _ => "parent-missing",
            });
        }
        catch (PatchTargetMissingException)
        {
            return (null, "parent-missing");
        }
    }
}
