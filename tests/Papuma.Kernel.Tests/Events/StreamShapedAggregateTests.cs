// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Globalization;

using Papuma.Kernel.Model;
using Papuma.Kernel.Store;
using Papuma.Kernel.Tenancy;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Events;

/// <summary>
/// The stream-shaped-aggregates recipe (ADR-023), run as written: the document is the
/// state, every posting is a fact in the event log, both written in one commit — save
/// first, append second, so a concurrency conflict never leaves an orphaned fact.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StreamShapedAggregateTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private DocumentStore _store = null!;

    public StreamShapedAggregateTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // --- The recipe's code, verbatim ------------------------------------------------

    public sealed record Account(string Id, string Owner, decimal Balance = 0m);

    public sealed record Posted(
        string AccountId,
        decimal Amount,
        string Reference,
        long AccountVersion,
        decimal BalanceAfter);

    public static class Ledger
    {
        public static Posted Decide(Account account, long version, decimal amount, string reference)
        {
            if (amount == 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), "A posting must move money.");
            }

            var balanceAfter = account.Balance + amount;
            if (balanceAfter < 0m)
            {
                throw new InvalidOperationException($"Account {account.Id} would be overdrawn.");
            }

            return new Posted(account.Id, amount, reference, version + 1, balanceAfter);
        }

        public static Account Evolve(Account account, Posted posted) =>
            account with { Balance = account.Balance + posted.Amount };

        public static async Task<Posted> PostAsync(
            DocumentSession session, string accountId, decimal amount, string reference, CancellationToken ct = default)
        {
            var current = await session.LoadAsync<Account>(accountId, ct)
                ?? throw new DocumentNotFoundException(nameof(Account), accountId);

            var posted = Decide(current.Document, current.Version, amount, reference);

            await session.SaveAsync(Evolve(current.Document, posted), current.Version, ct); // 1. state + concurrency check
            await session.AppendAsync(posted, ct);                                           // 2. the fact
            return posted;
        }
    }

    // ----------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        var model = new KernelModelBuilder()
            .Document<Account>()
            .Event<Posted>()
            .Build();

        await SchemaManager.EnsureSchemaAsync(_fixture.DataSource, model);
        _store = new DocumentStore(_fixture.DataSource, model);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static ScopeContext NewTenant() => ScopeContext.Tenant($"t{Guid.NewGuid():N}");

    private async Task<string> OpenAccountAsync(ScopeContext scope)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var session = _store.OpenSession(scope);
        await session.SaveAsync(new Account(id, "Harry"), 0);
        await session.CommitAsync();
        return id;
    }

    [Fact]
    public void Decide_RejectsOverdraftAndZeroAmount_WithoutADatabase()
    {
        var account = new Account("a1", "Harry", Balance: 10m);

        Assert.Throws<InvalidOperationException>(() => Ledger.Decide(account, 1, -11m, "rent"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger.Decide(account, 1, 0m, "noop"));

        var posted = Ledger.Decide(account, 1, -10m, "rent");
        Assert.Equal(new Posted("a1", -10m, "rent", 2, 0m), posted);
        Assert.Equal(0m, Ledger.Evolve(account, posted).Balance);
    }

    [Fact]
    public async Task Postings_WriteStateAndFactsInOneCommit_AndReconcile()
    {
        var scope = NewTenant();
        var id = await OpenAccountAsync(scope);

        await using (var session = _store.OpenSession(scope))
        {
            await Ledger.PostAsync(session, id, 100m, "salary");
            await session.CommitAsync();
            await Ledger.PostAsync(session, id, -30m, "groceries");
            await session.CommitAsync();
        }

        await using var reader = _store.OpenSession(scope);
        var account = await reader.LoadAsync<Account>(id);
        Assert.NotNull(account);
        Assert.Equal(70m, account.Document.Balance);
        Assert.Equal(3, account.Version);

        // One fact per state version, in order.
        var versions = await LoadScalarAsync(scope, """
            SELECT string_agg(payload ->> 'accountVersion', ',' ORDER BY seq)
            FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId
            """);
        Assert.Equal("2,3", versions);

        // Reconciliation — verify, never rebuild: the facts add up to the state.
        var sum = await LoadScalarAsync(scope, """
            SELECT sum((payload ->> 'amount')::numeric)::text
            FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId
            """);
        Assert.Equal(account.Document.Balance, decimal.Parse(sum, CultureInfo.InvariantCulture));
    }

    /// <summary>The recipe's reconciliation query, verbatim.</summary>
    private const string ReconciliationSql = """
        SELECT d.scope, d.tenant_id, d.id,
               (d.data ->> 'balance')::numeric                  AS balance,
               coalesce(sum((e.payload ->> 'amount')::numeric), 0) AS facts
        FROM papuma.document d
        LEFT JOIN papuma.event e
               ON e.scope = d.scope AND e.tenant_id = d.tenant_id
              AND e.event_type = 'Posted' AND e.payload ->> 'accountId' = d.id
        WHERE d.document_type = 'Account'
        GROUP BY d.scope, d.tenant_id, d.id, d.data
        HAVING (d.data ->> 'balance')::numeric <> coalesce(sum((e.payload ->> 'amount')::numeric), 0);
        """;

    [Fact]
    public async Task ReconciliationQuery_FindsDrift_OnlyWhereStateAndFactsDisagree()
    {
        var scope = NewTenant();
        var clean = await OpenAccountAsync(scope);
        var drifted = await OpenAccountAsync(scope);

        await using (var session = _store.OpenSession(scope))
        {
            await Ledger.PostAsync(session, clean, 40m, "deposit");
            await Ledger.PostAsync(session, drifted, 40m, "deposit");
            await session.CommitAsync();

            // Drift: a state change without its fact.
            var current = await session.LoadAsync<Account>(drifted);
            await session.SaveAsync(current!.Document with { Balance = 55m }, current.Version);
            await session.CommitAsync();
        }

        var findings = new List<string>();
        await using (var conn = await _fixture.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = ReconciliationSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == scope.TenantId)
                {
                    findings.Add($"{reader.GetString(2)}:{reader.GetDecimal(3)}:{reader.GetDecimal(4)}");
                }
            }
        }

        Assert.Equal([$"{drifted}:55:40"], findings);
    }

    [Fact]
    public async Task ConcurrentPosting_LoserAppendsNothing()
    {
        var scope = NewTenant();
        var id = await OpenAccountAsync(scope);

        await using var loser = _store.OpenSession(scope);
        var stale = await loser.LoadAsync<Account>(id);

        await using (var winner = _store.OpenSession(scope))
        {
            await Ledger.PostAsync(winner, id, 50m, "deposit");
            await winner.CommitAsync();
        }

        // The loser decided on version 1; saving first surfaces the conflict before any append.
        var posted = Ledger.Decide(stale!.Document, stale.Version, 20m, "late deposit");
        await Assert.ThrowsAsync<ConcurrencyException>(
            () => loser.SaveAsync(Ledger.Evolve(stale.Document, posted), stale.Version));

        var count = await LoadScalarAsync(
            scope, "SELECT count(*)::text FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId");
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task AppendBeforeSave_SurvivesTheConflict_WhenTheCallerCommitsAnyway()
    {
        // The pitfall the recipe's ordering avoids: each write runs under its own savepoint,
        // so a failed save does not undo an earlier append in the same session.
        var scope = NewTenant();
        var id = await OpenAccountAsync(scope);

        await using (var bump = _store.OpenSession(scope))
        {
            await Ledger.PostAsync(bump, id, 5m, "bump");
            await bump.CommitAsync();
        }

        await using (var session = _store.OpenSession(scope))
        {
            var orphan = new Posted(id, 20m, "orphan", 2, 20m);
            await session.AppendAsync(orphan);
            await Assert.ThrowsAsync<ConcurrencyException>(
                () => session.SaveAsync(new Account(id, "Harry", 20m), expectedVersion: 1));
            await session.CommitAsync();
        }

        var references = await LoadScalarAsync(scope, """
            SELECT string_agg(payload ->> 'reference', ',' ORDER BY seq)
            FROM papuma.event WHERE scope = @scope AND tenant_id = @tenantId
            """);
        Assert.Equal("bump,orphan", references);
    }

    private async Task<string> LoadScalarAsync(ScopeContext scope, string sql)
    {
        await using var conn = await _fixture.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("scope", scope.Scope.ToString());
        cmd.Parameters.AddWithValue("tenantId", scope.TenantId ?? string.Empty);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
