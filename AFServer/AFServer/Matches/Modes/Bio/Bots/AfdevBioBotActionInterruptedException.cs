namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Normal action-generation change before native execution or after confirmed return. The caller must still
/// retire current navigation.
/// </summary>
internal sealed class AfdevBioBotActionInterruptedException(string message) : Exception(message);
