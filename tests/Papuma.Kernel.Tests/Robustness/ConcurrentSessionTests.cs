// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Testing;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Robustness;

/// <summary>Several sessions writing one document at once: no lost update, no gap in the versions.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrentSessionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ConcurrentWriters_LoseNoUpdateAndLeaveNoGap()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        await SchemaManager.EnsureSchemaAsync(fixture.DataSource, model);
        var store = new PostgresStoreAdapter(new DocumentStore(fixture.DataSource, model, logger: null, TestHashKeys.Provider));

        for (var round = 0; round < 3; round++)
        {
            await new ConcurrencyRunner().RunAsync(store);
        }
    }
}
