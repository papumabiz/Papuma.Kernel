// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Papuma.Kernel.Core.Tests.Robustness;
using Papuma.Kernel.Local.Tests.Infrastructure;
using Papuma.Kernel.Model;

namespace Papuma.Kernel.Local.Tests.Robustness;

/// <summary>Several sessions writing one document at once: no lost update, no gap in the versions.</summary>
[Collection(SqliteCollection.Name)]
public sealed class SqliteConcurrentSessionTests(SqliteFixture fixture)
{
    [Fact]
    public async Task ConcurrentWriters_LoseNoUpdateAndLeaveNoGap()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        var store = new SqliteStoreAdapter(await fixture.CreateStoreAsync(model));

        for (var round = 0; round < 3; round++)
        {
            await new ConcurrencyRunner().RunAsync(store);
        }
    }

    [Fact]
    public async Task ConcurrentDeletes_HaveExactlyOneWinnerPerRound()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        var store = new SqliteStoreAdapter(await fixture.CreateStoreAsync(model));
        await new ConcurrencyRunner().RunContendedDeleteAsync(store);
    }

    [Fact]
    public async Task ConcurrentRollbacks_HaveExactlyOneWinnerPerRound()
    {
        var model = new KernelModelBuilder().Document<FreeDoc>().Build();
        var store = new SqliteStoreAdapter(await fixture.CreateStoreAsync(model));
        await new ConcurrencyRunner().RunContendedRollbackAsync(store);
    }
}
