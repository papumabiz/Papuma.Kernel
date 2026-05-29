using Npgsql;

using Papuma.Kernel.Transactions;

namespace Papuma.Kernel.Tests.Transactions;

public class NpgsqlUnitOfWorkTests
{
    [Fact]
    public void Constructor_ThrowsForNullDataSource()
    {
        Assert.Throws<ArgumentNullException>(() => new NpgsqlUnitOfWork(dataSource: null!));
    }

    [Fact]
    public void Constructor_ThrowsForInvalidMaxRetries()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

        var options = new UnitOfWorkOptions { MaxRetries = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new NpgsqlUnitOfWork(dataSource, options));
    }

    [Fact]
    public void Constructor_ThrowsForNegativeBaseRetryDelay()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");

        var options = new UnitOfWorkOptions { BaseRetryDelay = TimeSpan.FromMilliseconds(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new NpgsqlUnitOfWork(dataSource, options));
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsForNullAction()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=test;Username=test;Password=test");
        var sut = new NpgsqlUnitOfWork(dataSource);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.ExecuteAsync(action: null!));
    }
}