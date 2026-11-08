using AFServer.AssaultFire.Hosting;

namespace AFServer.AssaultFire.Matches;

/// <summary>
/// Deployment inputs that passed static preflight, without implying process, map-world, or UDP readiness.
/// </summary>
/// <remarks>
/// Created by preflight. Instance preparation still verifies private contents and shared-package identity.
/// Do not mutate settings, manifests, or deployment trees during calls.
/// </remarks>
public sealed class AfdevRuntimeValidation
{
    internal AfdevRuntimeValidation(MatchRuntimeOptions options, RuntimeManifest manifest, RoomTarget target,
        uint modeId, ushort mapId)
    {
        Options = options;
        Manifest = manifest;
        Target = target;
        ModeId = modeId;
        MapId = mapId;
    }

    /// <summary>Deployment settings resolved against the executable directory.</summary>
    public MatchRuntimeOptions Options { get; }

    /// <summary>The content-validated runtime-package manifest.</summary>
    public RuntimeManifest Manifest { get; }

    /// <summary>Map and GameInfo for the exact mode/map pair.</summary>
    public RoomTarget Target { get; }

    /// <summary>Mode ID selected by preflight.</summary>
    public uint ModeId { get; }

    /// <summary>Map ID selected by preflight.</summary>
    public ushort MapId { get; }

    /// <summary>
    /// Selects this round's route from the same verified complete manifest without rereading or copying the
    /// client.
    /// </summary>
    internal AfdevRuntimeValidation SelectRoute(uint modeId, ushort mapId)
    {
        RoomTarget target = AfdevMapCatalog.Resolve(modeId, mapId);

        AfdevRuntimePreflight.ValidateLayout(Manifest, target);

        return new AfdevRuntimeValidation(Options, Manifest, target, modeId, mapId);
    }
}
