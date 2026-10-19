// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Robustness;

/// <summary>
/// Random operation sequences against real PostgreSQL sessions and an in-memory reference model
/// (<see cref="SessionModelRunner"/>): state, version numbers, concurrency and not-found errors, rollback,
/// abandoned sessions and the complete change history must match the model after every sequence.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SessionModelTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RandomSequences_MatchTheReferenceModel()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        var store = new PostgresStoreAdapter(new DocumentStore(fixture.DataSource, model, logger: null, TestHashKeys.Provider));
        var runner = new SessionModelRunner(model, TestHashKeys.Provider);

        for (var seed = 1; seed <= 25; seed++)
        {
            await runner.RunAsync(seed, store);
        }
    }
}
