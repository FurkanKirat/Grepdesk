using Grepdesk.UI.Startup;

namespace Grepdesk.Tests;

public class SingleInstanceHostTests
{
    private static string UniqueChannel() => $"Grepdesk.Test.{Guid.NewGuid():N}";

    [Fact]
    public void Only_one_primary_acquires_the_channel_and_second_sends_commands()
    {
        var channel = UniqueChannel();
        using var dir = new TempDir();
        var file = dir.WriteFile("readme.txt", "hello");

        using var primary = SingleInstanceHost.TryAcquire(channel);
        Assert.NotNull(primary);
        // Mutex is reentrant on the same thread; another thread/process sees it as taken.
        SingleInstanceHost? contender = null;
        var thread = new Thread(() => contender = SingleInstanceHost.TryAcquire(channel));
        thread.Start();
        thread.Join();
        using (contender)
            Assert.Null(contender);

        primary.StartListening();

        Assert.True(SingleInstanceHost.TrySend(AppCommand.Parse([]), channel));
        Assert.True(SingleInstanceHost.TrySend(AppCommand.Parse([dir.Path]), channel));
        Assert.True(SingleInstanceHost.TrySend(AppCommand.Parse([file]), channel));

        var received = new List<AppCommand>();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (primary.Commands.TryRead(out var cmd))
                received.Add(cmd);
            return received.Count >= 3;
        }, TimeSpan.FromSeconds(5)));

        Assert.Equal(CommandKind.OpenSearch, received[0].Kind);
        Assert.Equal(CommandKind.OpenFolder, received[1].Kind);
        Assert.Equal(dir.Path, received[1].Paths.Single());
        Assert.Equal(CommandKind.OpenFile, received[2].Kind);
        Assert.Equal(file, received[2].Paths.Single());
    }

    [Fact]
    public void StopAccepting_releases_mutex_and_rejects_further_sends()
    {
        var channel = UniqueChannel();
        using var primary = SingleInstanceHost.TryAcquire(channel);
        Assert.NotNull(primary);
        primary.StartListening();

        primary.StopAccepting();
        Assert.False(SingleInstanceHost.TrySend(AppCommand.Parse([]), channel));

        using var next = SingleInstanceHost.TryAcquire(channel);
        Assert.NotNull(next);
    }
}
