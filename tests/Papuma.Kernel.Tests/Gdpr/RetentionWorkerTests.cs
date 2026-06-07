// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Papuma.Kernel.Gdpr;

namespace Papuma.Kernel.Tests.Gdpr;

public class RetentionWorkerTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new RetentionWorker(
            dataSource: null!,
            NullLogger<RetentionWorker>.Instance));
    }

    [Fact]
    public void Constructor_ThrowsForNullLogger()
    {
        using var dataSource = CreateDataSource();

        Assert.Throws<ArgumentNullException>(() => new RetentionWorker(
            dataSource,
            logger: null!));
    }

    [Fact]
    public void Constructor_ThrowsForInvalidBatchSize()
    {
        using var dataSource = CreateDataSource();

        var options = new RetentionWorkerOptions { BatchSize = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionWorker(
            dataSource,
            NullLogger<RetentionWorker>.Instance,
            options));
    }

    [Fact]
    public void Constructor_ThrowsForNonPositiveRetentionWindow()
    {
        using var dataSource = CreateDataSource();

        var options = new RetentionWorkerOptions { RetentionWindow = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionWorker(
            dataSource,
            NullLogger<RetentionWorker>.Instance,
            options));
    }

    [Fact]
    public void Options_Defaults_AreSafetyOriented()
    {
        var options = new RetentionWorkerOptions();

        Assert.Equal(TimeSpan.FromHours(1), options.PollInterval);
        Assert.Equal(TimeSpan.FromDays(365), options.RetentionWindow);
        Assert.Equal(1000, options.BatchSize);
    }

    private static NpgsqlDataSource CreateDataSource() =>
        NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
}
