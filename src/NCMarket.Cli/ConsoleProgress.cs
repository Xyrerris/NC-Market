using NCMarket.Core;

namespace NCMarket.Cli;

/// <summary>
/// Shows a capture as it happens: one line per equipment type, rewritten in place with a
/// carriage return while the type downloads, so a capture of five types leaves five lines
/// behind instead of hundreds.
/// </summary>
internal class ConsoleCaptureProgress : ICaptureProgress
{
    private readonly string? _header;
    private bool _announced;

    /// <param name="header">
    /// Line to print once, immediately before the first equipment type. It is deferred to
    /// that moment on purpose: a command that stops earlier — nothing in the history to
    /// compare against — must not announce a download it never starts.
    /// </param>
    public ConsoleCaptureProgress(string? header = null) => _header = header;

    public void TypeStarted(EquipmentType type)
    {
        if (!_announced && _header is not null)
        {
            Console.WriteLine(_header);
            _announced = true;
        }

        Console.Write($"  {type,-9}: recupero...");
    }

    public void TypeProgress(EquipmentType type, int fetched, int total) =>
        Console.Write(
            $"\r  {type,-9}: {fetched}{(total > 0 ? "/" + total : "")} scaricate...      ");

    public virtual void TypeCompleted(EquipmentType type, int listings) =>
        Console.WriteLine($"\r  {type,-9}: {listings} inserzioni      ");
}

/// <summary>
/// The same, for a capture that is being stored: it announces the snapshot the listings
/// are going into, and reports how many of them the database kept — a listing already
/// known from a previous snapshot is recorded, not stored again.
/// </summary>
internal sealed class ConsoleSnapshotProgress : ConsoleCaptureProgress, ISnapshotProgress
{
    private readonly Planet _planet;
    private readonly int? _maxPerType;

    public ConsoleSnapshotProgress(Planet planet, int? maxPerType)
    {
        _planet = planet;
        _maxPerType = maxPerType;
    }

    public void SnapshotCreated(long snapshotId, DateTime takenAtUtc) =>
        Console.WriteLine(
            $"Snapshot #{snapshotId} — pianeta {_planet.Name}, " +
            takenAtUtc.ToString("u", ConsoleReport.Culture) +
            (_maxPerType is int limit ? $", limite {limit} inserzioni per tipo" : ""));

    public void SnapshotInterrupted(long snapshotId)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(
            $"Snapshot #{snapshotId} interrotto: resta marcato come parziale e non verrà " +
            "usato come ultimo snapshot da stats, deals ed export.");
    }

    public override void TypeCompleted(EquipmentType type, int listings) =>
        Console.WriteLine($"\r  {type,-9}: salvate {listings} inserzioni      ");
}
