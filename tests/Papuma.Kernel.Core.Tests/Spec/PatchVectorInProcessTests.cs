// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Store;

namespace Papuma.Kernel.Core.Tests.Spec;

/// <summary>
/// Runs <c>docs/spec/vectors/patch.json</c> against <see cref="JsonPatchApplier"/>, the
/// in-process definition of patch semantics (the SQLite kernel executes patches with it; the
/// PostgreSQL kernel's SQL expression is held to the same vectors in its own suite).
/// </summary>
public sealed class PatchVectorInProcessTests
{
    private const string Engine = "in-process";

    public static TheoryData<string> Cases() => PatchVectors.CaseNames();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Patch_MatchesVector(string name)
    {
        var vector = PatchVectors.Load(name);
        var document = (JsonObject)vector["before"]!.DeepClone();

        try
        {
            JsonPatchApplier.Apply(document, PatchVectors.Operations(vector), "Doc", "1");
        }
        catch (NonNumericIncrementException)
        {
            PatchVectors.Assert(vector, Engine, after: null, error: "not-numeric");
            return;
        }
        catch (PatchTargetMissingException)
        {
            PatchVectors.Assert(vector, Engine, after: null, error: "parent-missing");
            return;
        }

        PatchVectors.Assert(vector, Engine, document, error: null);
    }
}
