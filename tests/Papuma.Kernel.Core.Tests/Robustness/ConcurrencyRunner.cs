// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Text.Json.Nodes;

using Papuma.Kernel.Changes;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;

namespace Papuma.Kernel.Core.Tests.Robustness;

/// <summary>
/// Several workers write one document at the same time, each in its own session: some with optimistic
/// versions (and retry on <see cref="ConcurrencyException"/>), some with unconditional increments. Every
/// accepted write adds exactly one to <c>Counter</c>, so the outcome is checkable without knowing the
/// interleaving: no lost update, no gap or duplicate in the version numbers, and a history that matches.
/// </summary>
internal sealed class ConcurrencyRunner
{
    public async Task RunAsync(IStoreAdapter store, int workers = 6, int writesPerWorker = 12)
    {
        const string id = "hot";
        var scope = ScopeContext.Tenant($"c{Guid.NewGuid():N}");
        var accepted = 0;
        var conflicts = 0;

        async Task Worker(int number)
        {
            for (var done = 0; done < writesPerWorker;)
            {
                await using var session = store.Open(scope);
                try
                {
                    if (number % 3 == 0)
                    {
                        // Unconditional patch: the database must serialize it, never lose it.
                        await session.PatchAsync(id, new PatchSpec(PatchKind.IncrementCounter, null, 1), expectedVersion: null);
                    }
                    else
                    {
                        var loaded = await session.LoadAsync(id);
                        if (loaded is null)
                        {
                            await session.SaveAsync(new FreeDoc(id, new JsonObject()), expectedVersion: 0);
                        }
                        else
                        {
                            await session.SaveAsync(loaded.Document with { Counter = loaded.Document.Counter + 1 }, loaded.Version);
                        }
                    }

                    await session.CommitAsync();
                    Interlocked.Increment(ref accepted);
                    done++;
                }
                catch (ConcurrencyException)
                {
                    Interlocked.Increment(ref conflicts);
                }
                catch (DocumentNotFoundException)
                {
                    // An unconditional patch ran before the document existed: try again.
                }
            }
        }

        // The first write creates the document; the rest race.
        await using (var first = store.Open(scope))
        {
            await first.SaveAsync(new FreeDoc(id, new JsonObject()), expectedVersion: 0);
            await first.CommitAsync();
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(n => Task.Run(() => Worker(n))));

        await using var check = store.Open(scope);
        var final = await check.LoadAsync(id) ?? throw new Xunit.Sdk.XunitException("document vanished");
        var history = await check.HistoryAsync(id);

        // The creating save is version 1 with Counter 0; each accepted write adds one version and one count.
        Assert.Equal(1 + accepted, final.Version);
        Assert.Equal(accepted, final.Document.Counter);
        Assert.Equal(Enumerable.Range(1, (int)final.Version).Select(v => (long)v), history.Select(h => h.Version).Order());
        Assert.Equal(ChangeOperation.Insert, history.OrderBy(h => h.Version).First().Operation);
        Assert.All(history.Where(h => h.Version > 1), h => Assert.Equal(ChangeOperation.Update, h.Operation));
        Assert.True(accepted == workers * writesPerWorker, $"accepted {accepted}, conflicts {conflicts}");
    }
}
