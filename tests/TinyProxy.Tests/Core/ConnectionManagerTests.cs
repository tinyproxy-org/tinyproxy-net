namespace TinyProxy.Tests.Core;

public class ConnectionManagerTests
{
    [Fact]
    public async Task TryAcquireSlotAsync_RejectsWhenPerIpLimitReached()
    {
        var config = new Configuration
        {
            MaxClients = 10,
            MaxClientsPerIp = 2
        };
        var manager = new ConnectionManager(config, new NullLogger());

        using var slot1 = await manager.TryAcquireSlotAsync("10.0.0.1", CancellationToken.None);
        using var slot2 = await manager.TryAcquireSlotAsync("10.0.0.1", CancellationToken.None);
        var slot3 = await manager.TryAcquireSlotAsync("10.0.0.1", CancellationToken.None);

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3);
        Assert.Equal(2, manager.ActiveCount);
    }

    [Fact]
    public async Task TryAcquireSlotAsync_AllowsSameIpAfterRelease()
    {
        var config = new Configuration
        {
            MaxClients = 10,
            MaxClientsPerIp = 1
        };
        var manager = new ConnectionManager(config, new NullLogger());

        var firstSlot = await manager.TryAcquireSlotAsync("10.0.0.2", CancellationToken.None);
        Assert.NotNull(firstSlot);

        var blockedSlot = await manager.TryAcquireSlotAsync("10.0.0.2", CancellationToken.None);
        Assert.Null(blockedSlot);

        firstSlot!.Dispose();

        var secondSlot = await manager.TryAcquireSlotAsync("10.0.0.2", CancellationToken.None);
        Assert.NotNull(secondSlot);
        secondSlot!.Dispose();
    }

    [Fact]
    public async Task TryAcquireSlotAsync_TracksDifferentIpsIndependently()
    {
        var config = new Configuration
        {
            MaxClients = 10,
            MaxClientsPerIp = 1
        };
        var manager = new ConnectionManager(config, new NullLogger());

        using var slot1 = await manager.TryAcquireSlotAsync("10.0.0.3", CancellationToken.None);
        using var slot2 = await manager.TryAcquireSlotAsync("10.0.0.4", CancellationToken.None);

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Equal(2, manager.ActiveCount);
    }

    [Fact]
    public async Task TryAcquireSlotAsync_DisablesPerIpLimitWhenConfiguredAsZero()
    {
        var config = new Configuration
        {
            MaxClients = 3,
            MaxClientsPerIp = 0
        };
        var manager = new ConnectionManager(config, new NullLogger());

        using var slot1 = await manager.TryAcquireSlotAsync("10.0.0.5", CancellationToken.None);
        using var slot2 = await manager.TryAcquireSlotAsync("10.0.0.5", CancellationToken.None);
        using var slot3 = await manager.TryAcquireSlotAsync("10.0.0.5", CancellationToken.None);
        var slot4 = await manager.TryAcquireSlotAsync("10.0.0.5", CancellationToken.None);

        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.NotNull(slot3);
        Assert.Null(slot4);
    }

    private sealed class NullLogger : ILogger
    {
        public void LogInfo(string message) { }
        public void LogError(string message) { }
        public void LogWarning(string message) { }
        public void LogConnect(string message) { }
        public void LogCritical(string message) { }
    }
}
