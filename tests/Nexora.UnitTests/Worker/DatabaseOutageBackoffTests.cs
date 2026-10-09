using System.Net.Sockets;
using Nexora.Business.Common;
using Nexora.Data.Persistence;
using Nexora.Worker;
using Npgsql;

namespace Nexora.UnitTests.Worker;

public sealed class DatabaseOutageBackoffTests
{
    [Fact]
    public void BackoffIncreasesIsBoundedAndReportsOnceUntilRecovery()
    {
        var backoff = new DatabaseOutageBackoff(TimeProvider.System);
        Assert.Equal((TimeSpan.FromSeconds(5), true), backoff.Next(1));
        Assert.Equal((TimeSpan.FromSeconds(10), false), backoff.Next(1));
        Assert.Equal((TimeSpan.FromSeconds(20), false), backoff.Next(1));
        Assert.Equal((TimeSpan.FromSeconds(40), false), backoff.Next(1));
        for (var i = 0; i < 100; i++)
            Assert.InRange(backoff.Next(0.5).Delay.TotalSeconds, 30, 60);
        backoff.Reset();
        Assert.Equal((TimeSpan.FromSeconds(5), true), backoff.Next(0));
    }

    [Fact]
    public void OnlyDatabaseConnectivityIsClassifiedAsTransient()
    {
        Assert.True(DatabaseConnectivityFailure.IsTransient(new NpgsqlException("synthetic", new SocketException())));
        Assert.True(DatabaseConnectivityFailure.IsTransient(new NpgsqlException("synthetic", new TimeoutException())));
        Assert.False(DatabaseConnectivityFailure.IsTransient(new InvalidOperationException("programming error")));
        Assert.False(DatabaseConnectivityFailure.IsTransient(new PostgresException("synthetic", "ERROR", "ERROR", "23505")));
        Assert.True(DatabaseConnectivityFailure.IsTransient(new PostgresException("synthetic", "ERROR", "ERROR", "57P03")));
    }
}
