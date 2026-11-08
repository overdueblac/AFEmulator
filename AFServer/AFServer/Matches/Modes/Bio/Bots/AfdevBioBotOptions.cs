namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Capacity-based filling settings for ordinary BIO. Human slots come from the frozen snapshot; actual
/// shared-play behavior requires client testing.
/// </summary>
public sealed class AfdevBioBotOptions
{
    /// <summary>
    /// Serially prepares capacity-minus-human bots after real binding. Full-human rooms prepare none.
    /// Disabled by default; changes require Server restart.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Records human walking demonstrations for offline route completion, without automatic training or
    /// replay. Limited to 30 minutes and 16 MiB per match.
    /// </summary>
    public bool RecordPlayerRoutes { get; set; }
}
