// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using Microsoft.Data.Sqlite;
using Papuma.Kernel.Hosting;

namespace Papuma.Kernel.Local.Tests.Hosting;

/// <summary>
/// <see cref="PapumaKernelLocalOptions"/> validation (review P-11): bad values fail at
/// startup, not later in a connection attempt or a <see cref="PeriodicTimer"/>.
/// </summary>
public sealed class PapumaKernelLocalOptionsTests
{
    // PeriodicTimer accepts at most 0xFFFFFFFE milliseconds (~49.7 days).
    private static readonly TimeSpan MaxTimerPeriod = TimeSpan.FromMilliseconds(0xFFFFFFFE);

    private sealed record OptionsDoc(string Id);

    private static PapumaKernelLocalOptions Valid()
    {
        var options = new PapumaKernelLocalOptions { ConnectionString = "Data Source=app.db" };
        options.Model(m => m.Document<OptionsDoc>());
        return options;
    }

    [Fact]
    public void Validate_AcceptsAConnectionStringAndTheDefaults()
    {
        Valid().Validate();
    }

    [Fact]
    public void Validate_AcceptsADbPath()
    {
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = "app.db";

        options.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Validate_RejectsABlankConnectionString(string connectionString)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.ConnectionString), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsABlankDbPath(string dbPath)
    {
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = dbPath;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.DbPath), ex.ParamName);
    }

    // Review L-7: each of these would give every connection its own private, empty database.
    [Theory]
    [InlineData(":memory:")]
    [InlineData(":MEMORY:")]
    [InlineData("file::memory:")]
    [InlineData("file:private?mode=memory")]
    public void Validate_RejectsAPrivateInMemoryDbPath(string dbPath)
    {
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = dbPath;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.DbPath), ex.ParamName);
        Assert.Contains("in-memory", ex.Message);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:memory:;Cache=Private")]
    [InlineData("Data Source=named;Mode=Memory")]
    [InlineData("Data Source=")]
    [InlineData("Cache=Shared")]
    [InlineData("Mode=ReadOnly")]
    public void Validate_RejectsAConnectionStringWithoutAUsableDataSource(string connectionString)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.ConnectionString), ex.ParamName);
    }

    [Theory]
    [InlineData("Data Source=:memory:;Cache=Shared")]
    [InlineData("Data Source=named;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=file:shared?mode=memory&cache=shared")]
    [InlineData("Data Source=app.db;Cache=Shared;Pooling=False")]
    public void Validate_AcceptsASharedInMemoryOrFileConnectionString(string connectionString)
    {
        var options = Valid();
        options.ConnectionString = connectionString;

        options.Validate();
    }

    [Fact]
    public void Validate_RejectsAnUnparsableConnectionString()
    {
        var options = Valid();
        options.ConnectionString = "this is not a connection string";

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(nameof(PapumaKernelLocalOptions.ConnectionString), ex.ParamName);
    }

    // Review L-7: the path is data, never connection-string grammar.
    [Theory]
    [InlineData("app.db")]
    [InlineData("app.db;Mode=ReadOnly")]
    [InlineData("a=b;c=d.db")]
    [InlineData("semi;colon.db")]
    [InlineData("quote\"d'.db")]
    [InlineData("C:\\Users\\me\\My App\\app.db")]
    public void ResolveConnectionString_KeepsTheDbPathAsADataSourceWhatEverItContains(string dbPath)
    {
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = dbPath;

        options.Validate();
        var resolved = new SqliteConnectionStringBuilder(options.ResolveConnectionString());

        Assert.Equal(dbPath, resolved.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, resolved.Mode);
        Assert.Equal(SqliteCacheMode.Default, resolved.Cache);
    }

    [Fact]
    public async Task ResolveConnectionString_OpensTheFileNamedByAPathWithSemicolonAndEquals()
    {
        var directory = Path.Combine(Path.GetTempPath(), "papuma-l7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "a=b;Mode=ReadOnly.db");
        var options = Valid();
        options.ConnectionString = null;
        options.DbPath = dbPath;
        var connectionString = options.ResolveConnectionString();

        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE t (x INTEGER);";
                await command.ExecuteNonQueryAsync(); // would fail with Mode=ReadOnly
            }

            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsDbPathAndConnectionStringTogether()
    {
        var options = Valid();
        options.DbPath = "x.db";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsRetentionIntervalsUpToTheTimerLimit()
    {
        var options = Valid();

        options.RetentionInterval = TimeSpan.FromMilliseconds(1);
        options.Validate();
        options.RetentionInterval = MaxTimerPeriod;
        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsANonPositiveRetentionInterval(int seconds)
    {
        var options = Valid();
        options.RetentionInterval = TimeSpan.FromSeconds(seconds);

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsARetentionIntervalBeyondWhatThePeriodicTimerAccepts()
    {
        var options = Valid();
        options.RetentionInterval = MaxTimerPeriod + TimeSpan.FromMilliseconds(1);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("RetentionInterval", ex.Message);
    }
}
