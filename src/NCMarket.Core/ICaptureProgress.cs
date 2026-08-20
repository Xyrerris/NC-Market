namespace NCMarket.Core;

/// <summary>
/// A capture reported while it happens. Downloading a listing is minutes of network I/O,
/// so a caller handed only the final result would have nothing to show meanwhile — and
/// the services themselves must stay free of any assumption about where that goes: the
/// CLI rewrites one line per equipment type, a scheduled job would append to a log.
/// Callers that report nothing pass null instead.
/// </summary>
public interface ICaptureProgress
{
    /// <summary>The download of an equipment type is about to start.</summary>
    void TypeStarted(EquipmentType type);

    /// <summary>
    /// <paramref name="fetched"/> listings downloaded so far for the type;
    /// <paramref name="total"/> is what the service announced, 0 when it announces none.
    /// </summary>
    void TypeProgress(EquipmentType type, int fetched, int total);

    /// <summary>The type is done and contributed <paramref name="listings"/> of them.</summary>
    void TypeCompleted(EquipmentType type, int listings);
}
