// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Local.Tests.Robustness;

/// <summary>
/// Random operation sequences against real SQLite sessions and an in-memory reference model — the same
/// <see cref="SessionModelRunner"/> that drives the PostgreSQL kernel, so both engines are held to one model.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteSessionModelTests(SqliteFixture fixture)
{
    [Fact]
    public async Task RandomSequences_MatchTheReferenceModel()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        var store = new SqliteStoreAdapter(await fixture.CreateStoreAsync(model));
        var runner = new SessionModelRunner(model, TestHashKeys.Provider);

        for (var seed = 1; seed <= 40; seed++)
        {
            await runner.RunAsync(seed, store);
        }
    }
}
