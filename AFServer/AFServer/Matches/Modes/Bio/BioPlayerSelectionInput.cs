namespace AFServer.AssaultFire.Matches.Modes.Bio;

/// <summary>
/// Trusted selection input read from current BIO PRI and Owner. PlayerId is a caller-maintained unique
/// identity, never client-reported or a replacement for exact-session/round grants. Preserve PRIArray
/// order; null entries represent missing PRI or failed casts.
/// </summary>
/// <param name="HasBioController">
/// Whether PRI.Owner casts to TGBioPlayerController, not whether a Pawn exists.
/// </param>
/// <param name="LastTurnZombieTime">
/// Original single-precision PRI world time; selection initializes zero values.
/// </param>
/// <param name="CurrentSpecifyZombieType">
/// Original PRI type value, without assumed defaults or replacement of unknown types.
/// </param>
public sealed record BioPlayerSelectionInput(int PlayerId, bool HasTeam, bool IsGamePlayer,
    bool OnlySpectator, bool HasBioController, float LastTurnZombieTime, byte CurrentSpecifyZombieType);
