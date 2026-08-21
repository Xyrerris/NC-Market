namespace NCMarket.Core;

/// <summary>
/// Somewhere a message can be delivered. The alerts are one-way and text-only — a job
/// that found something says so, and nothing answers back — so this is the whole
/// contract, and adding a second destination (Discord, a webhook of one's own) means
/// implementing it, not touching what decides that there is something to say.
/// </summary>
public interface INotificationChannel
{
    /// <summary>
    /// The destination, as it is named to the user. Appears in the line the CLI prints
    /// after an alert, so a job's log says where the message went.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Delivers <paramref name="message"/>, splitting it if the destination has a size
    /// limit. Returns once it is accepted; throws if it is not, so a caller that must
    /// record what was announced learns that it was not.
    /// </summary>
    Task SendAsync(string message, CancellationToken ct = default);
}
