namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Frozen coordinate target in the current map, not a UObject address or permission to alter character
/// position.
/// </summary>
public readonly record struct AfdevBioBotPoint(float X, float Y, float Z)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
        Math.Abs(X) <= 262144 && Math.Abs(Y) <= 262144 && Math.Abs(Z) <= 262144;

    public float[] ToArray() => [X, Y, Z];
}
