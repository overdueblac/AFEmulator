namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Native bot-filling settings for ordinary PvP, configured per mode without accounts or admission grants.
/// </summary>
public sealed class AfdevPvpBotOptions
{
    /// <summary>
    /// Whether native TGame AddBot fills frozen vacancies. Team modes fill each team separately; enabled by
    /// default.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>TGBot AI_Easy/Normal/Hard/Brutal values 0–3, defaulting to Normal.</summary>
    public byte Difficulty { get; set; } = 1;
}
