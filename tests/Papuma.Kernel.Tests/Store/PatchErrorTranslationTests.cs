// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Npgsql;

using Papuma.Kernel.Store;
using Papuma.Kernel.Tests.Infrastructure;

namespace Papuma.Kernel.Tests.Store;

/// <summary>
/// The patch error translation (review S-9) keys on the server's <c>SqlState</c> and
/// <c>Routine</c>, never on the message text — which follows <c>lc_messages</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PatchErrorTranslationTests(PostgresFixture fixture)
{
    private static PostgresException Error(string sqlState, string? routine, string message) =>
        new(message, "ERROR", "ERROR", sqlState, routine: routine);

    [Fact]
    public void NumericCast_IsRecognized_WhateverTheMessageLanguage()
    {
        var german = Error(PostgresErrorCodes.InvalidTextRepresentation, "numeric_in",
            "ungültige Eingabesyntax für Typ numeric: »abc«");

        var translated = DocumentSession.TranslatePatchError(german, "Doc", "1");

        var typed = Assert.IsType<NonNumericIncrementException>(translated);
        Assert.Equal("Doc", typed.DocumentType);
        Assert.Same(german, typed.InnerException);
    }

    [Fact]
    public void ArrayPathElement_IsRecognized_WhateverTheMessageLanguage()
    {
        var german = Error(PostgresErrorCodes.InvalidTextRepresentation, "setPathArray",
            "Pfadelement an Position 2 ist keine ganze Zahl: »x«");

        Assert.IsType<PatchTargetMissingException>(DocumentSession.TranslatePatchError(german, "Doc", "1"));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.InvalidTextRepresentation, "some_other_routine", "invalid input syntax for type numeric: \"x\"")]
    [InlineData(PostgresErrorCodes.InvalidTextRepresentation, null, "path element at position 2 is not an integer")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "numeric_in", "type numeric")]
    [InlineData(PostgresErrorCodes.UniqueViolation, "setPathArray", "path element")]
    public void OtherErrors_AreLeftAlone_EvenWithATellingMessage(string sqlState, string? routine, string message)
    {
        Assert.Null(DocumentSession.TranslatePatchError(Error(sqlState, routine, message), "Doc", "1"));
    }

    // Canary for server upgrades: the routine names are internal to PostgreSQL, so pin the real ones.
    [Fact]
    public async Task TheRealServerErrors_CarryTheRoutinesTheTranslationKeysOn()
    {
        var numeric = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("SELECT 'abc'::numeric"));
        Assert.IsType<NonNumericIncrementException>(DocumentSession.TranslatePatchError(numeric, "Doc", null));

        var arrayPath = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """SELECT jsonb_set('{"a":[1,2]}'::jsonb, ARRAY['a','x'], '1'::jsonb, true)"""));
        Assert.IsType<PatchTargetMissingException>(DocumentSession.TranslatePatchError(arrayPath, "Doc", null));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = fixture.DataSource.CreateCommand(sql);
        await cmd.ExecuteScalarAsync();
    }
}
