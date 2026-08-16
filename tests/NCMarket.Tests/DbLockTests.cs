using NCMarket.Core;

namespace NCMarket.Tests;

public sealed class DbLockTests
{
    [Fact]
    public void The_lock_is_free_when_nobody_holds_it()
    {
        using var temp = new TempDatabase();

        using var first = DbLock.Acquire(temp.DbPath, TimeSpan.FromSeconds(1));

        Assert.Equal(temp.DbPath + ".lock", first.Path);
    }

    [Fact]
    public void A_second_holder_waits_and_then_gives_up()
    {
        using var temp = new TempDatabase();
        using var held = DbLock.Acquire(temp.DbPath, TimeSpan.FromSeconds(1));

        var waited = false;
        Assert.Throws<TimeoutException>(
            () => DbLock.Acquire(temp.DbPath, TimeSpan.FromSeconds(1), () => waited = true));

        Assert.True(waited, "Il secondo tentativo doveva segnalare l'attesa prima di arrendersi.");
    }

    [Fact]
    public void Releasing_the_lock_lets_the_next_one_in()
    {
        using var temp = new TempDatabase();

        var first = DbLock.Acquire(temp.DbPath, TimeSpan.FromSeconds(1));
        first.Dispose();

        using var second = DbLock.Acquire(temp.DbPath, TimeSpan.FromSeconds(1));
        Assert.NotNull(second);
    }
}
