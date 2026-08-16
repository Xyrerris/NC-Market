namespace NCMarket.Core;

/// <summary>
/// Cross-process mutual exclusion between the commands that need the database to
/// themselves. <c>prune</c> ends with a VACUUM, which requires exclusive access, while a
/// <c>snapshot</c> holds write transactions for as long as the download takes: scheduled
/// on the same machine (snapshot every few hours, prune weekly) the two eventually
/// overlap, and the loser fails on the 5 s busy timeout. Holders queue up here instead.
///
/// The lock is the exclusive open of a sentinel file next to the database, so it is
/// released by the operating system even if the process is killed. The file itself is
/// left behind on purpose: it is the open that locks, not the existence, so a leftover
/// from a previous run means nothing.
/// </summary>
public sealed class DbLock : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly FileStream _stream;

    private DbLock(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Path of the sentinel file backing this lock.</summary>
    public string Path { get; }

    /// <summary>
    /// Acquires the lock for <paramref name="dbPath"/>, waiting up to
    /// <paramref name="timeout"/> for whoever holds it. <paramref name="onWait"/> is
    /// invoked once, only if the lock is not free on the first try, so callers can tell
    /// the user why they are waiting.
    /// </summary>
    /// <exception cref="TimeoutException">The lock was still held when the wait ran out.</exception>
    public static DbLock Acquire(string dbPath, TimeSpan timeout, Action? onWait = null)
    {
        var path = dbPath + ".lock";
        var deadline = DateTime.UtcNow + timeout;
        var notified = false;

        while (true)
        {
            try
            {
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new DbLock(stream, path);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Un altro comando NC-Market sta usando il database ({dbPath}): " +
                        $"attesa di {timeout.TotalSeconds:N0} s scaduta. Riprova quando lo " +
                        "snapshot o il prune in corso è terminato.");
                }

                if (!notified)
                {
                    onWait?.Invoke();
                    notified = true;
                }

                Thread.Sleep(PollInterval);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
