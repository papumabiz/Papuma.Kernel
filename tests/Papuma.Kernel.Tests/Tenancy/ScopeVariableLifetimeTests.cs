// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Tenancy;

/// <summary>
/// Pins the PostgreSQL behavior the <c>ScopeConnectionExtensions</c> documentation relies on (review S-16):
/// a transaction-local setting made outside a transaction does not turn into a session-level one, so the
/// scope can be lost but never leaks to the next user of a pooled connection — RLS then fails closed.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ScopeVariableLifetimeTests(PostgresFixture fixture)
{
    private static async Task<string?> CurrentScopeAsync(NpgsqlConnection connection)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_setting('app.current_scope', true)", connection);
        var value = (string?)await cmd.ExecuteScalarAsync();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [Fact]
    public async Task SetConfigLocal_OutsideATransaction_DoesNotOutliveItsStatement()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_scope', 'All', true)", connection))
        {
            await set.ExecuteNonQueryAsync();
        }

        Assert.Null(await CurrentScopeAsync(connection));
    }

    [Fact]
    public async Task SetLocal_OutsideATransaction_DoesNotBecomeASessionSetting()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        await using (var set = new NpgsqlCommand("SET LOCAL app.current_scope = 'All'", connection))
        {
            await set.ExecuteNonQueryAsync(); // PostgreSQL only warns: "can only be used in transaction blocks"
        }

        Assert.Null(await CurrentScopeAsync(connection));
    }
}
