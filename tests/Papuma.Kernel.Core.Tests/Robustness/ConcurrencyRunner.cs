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
    /// <summary>
    /// Workers race to delete the same version of one document, round after round. Exactly one wins each
    /// round, the others see a conflict or "not found", and recreating keeps the version counting up.
    /// </summary>
    public async Task RunContendedDeleteAsync(IStoreAdapter store, int workers = 6, int rounds = 8)
    {
        const string id = "doomed";
        var scope = ScopeContext.Tenant($"cd{Guid.NewGuid():N}");
        long version = 0;

        for (var round = 0; round < rounds; round++)
        {
            await WriteAsync(store, scope, s => s.SaveAsync(new FreeDoc(id, new JsonObject()), expectedVersion: 0));
            await WriteAsync(store, scope, s => s.PatchAsync(id, new PatchSpec(PatchKind.IncrementCounter, null, 1), expectedVersion: null));
            version += 2;
            var target = version;

            // Every other worker holds a stale version and must never win, whatever the interleaving.
            var outcomes = await RaceAsync(store, scope, workers, (s, n) => s.DeleteAsync(id, n % 2 == 0 ? target : target - 1));
            AssertOneWinner(outcomes, $"delete of v{target}");
            version++;

            await using var check = store.Open(scope);
            Assert.Null(await check.LoadAsync(id));
            var last = (await check.HistoryAsync(id)).OrderBy(h => h.Version).Last();
            Assert.Equal((version, ChangeOperation.Delete), (last.Version, last.Operation));
        }
    }

    /// <summary>
    /// Workers race to roll the same document back from the same version. Exactly one wins, and the
    /// document ends up at the restored content in the next version.
    /// </summary>
    public async Task RunContendedRollbackAsync(IStoreAdapter store, int workers = 6, int rounds = 8)
    {
        const string id = "undo";
        var scope = ScopeContext.Tenant($"cr{Guid.NewGuid():N}");
        await WriteAsync(store, scope, s => s.SaveAsync(new FreeDoc(id, new JsonObject(), Counter: 0), expectedVersion: 0));
        long version = 1;

        for (var round = 0; round < rounds; round++)
        {
            await WriteAsync(store, scope, s => s.PatchAsync(id, new PatchSpec(PatchKind.IncrementCounter, null, 5), expectedVersion: null));
            version++;
            var from = version;

            var outcomes = await RaceAsync(store, scope, workers, (s, _) => s.RollbackAsync(id, toVersion: 1, expectedVersion: from));
            AssertOneWinner(outcomes, $"rollback from v{from}");
            version++;

            await using var check = store.Open(scope);
            var loaded = await check.LoadAsync(id) ?? throw new Xunit.Sdk.XunitException("document vanished");
            Assert.Equal(version, loaded.Version);
            Assert.Equal(0, loaded.Document.Counter);
        }
    }

    private static async Task WriteAsync(IStoreAdapter store, ScopeContext scope, Func<ISessionAdapter, Task<SaveResult>> action)
    {
        await using var session = store.Open(scope);
        await action(session);
        await session.CommitAsync();
    }

    // Starts every worker behind one gate so the writes overlap as much as the engine allows.
    private static async Task<List<Exception?>> RaceAsync(IStoreAdapter store, ScopeContext scope, int workers, Func<ISessionAdapter, int, Task<SaveResult>> action)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, workers).Select(n => Task.Run(async () =>
        {
            await gate.Task;
            await using var session = store.Open(scope);
            try
            {
                await action(session, n);
                await session.CommitAsync();
                return (Exception?)null;
            }
            catch (Exception ex) when (ex is ConcurrencyException or DocumentNotFoundException)
            {
                return ex;
            }
        })).ToList();

        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    private static void AssertOneWinner(List<Exception?> outcomes, string what) =>
        Assert.True(outcomes.Count(e => e is null) == 1, $"{what}: {outcomes.Count(e => e is null)} winners of {outcomes.Count}");

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
