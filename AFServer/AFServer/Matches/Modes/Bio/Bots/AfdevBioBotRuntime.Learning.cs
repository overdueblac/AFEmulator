namespace AFServer.AssaultFire.Matches.Bots;

public sealed partial class AfdevBioBotRuntime
{
    private Task<AfdevBioBotWalkRoutes>? walkRoutes;

    /// <summary>
    /// For demonstration planning only. The same RouteRun queries, moves, and retires it; no separate
    /// action slot exists.
    /// </summary>
    internal Task<AfdevBioBotRouteResult> RunLearnedWalkAsync(AfdevBioBotPreparation participant, AfdevBioBotWalkStep step,
        CancellationToken token) => RunNavigationRouteCoreAsync(participant, 0, step.To, token,
            AfdevBioBotNavigationMode.Point, step);

    /// <summary>
    /// After reachability precheck rejection, revalidates complete frozen Pawn identity, walking state, and
    /// actual start position.
    /// </summary>
    internal bool CanStartWalkStep(AfdevBioBotWalkStep step, AfdevBioBotNavigationState state)
    {
        if (state.Pawn != step.Pawn.Address)
            return false;

        var reader = new QueryInspector(process, cacheMetadata: true);
        uint pawnField = reader.FieldAddress(state.Controller, "Pawn", "Engine.Controller.Pawn", "ObjectProperty", 4);

        try
        {
            if (reader.U32(pawnField) != state.Pawn || reader.Identity(state.Pawn) != step.Pawn)
                return false;

            uint physics = reader.FieldAddress(state.Pawn, "Physics", "Engine.Actor.Physics", "ByteProperty", 1);
            uint location = reader.FieldAddress(state.Pawn, "Location", "Engine.Actor.Location", "StructProperty", 12);
            byte[] position = process.ReadMemory(location, 12);
            var origin = new AfdevBioBotPoint(BitConverter.ToSingle(position, 0), BitConverter.ToSingle(position, 4),
                BitConverter.ToSingle(position, 8));

            return process.ReadMemory(physics, 1)[0] == 1 && AfdevBioBotWalkStep.CanStartAt(step.From, step.To, origin) &&
                reader.U32(pawnField) == state.Pawn && reader.Identity(state.Pawn) == step.Pawn;
        }
        catch (Exception) when (process.IsAlive && reader.U32(pawnField) != state.Pawn)
        {
            return false;
        }
    }

    /// <summary>
    /// Bots in the same world share anonymous geometry snapshots; each slot still owns native actions and
    /// failure caches.
    /// </summary>
    private Task<AfdevBioBotWalkRoutes> GetWalkRoutesAsync(CancellationToken token)
    {
        AfdevBioBotRuntime owner = rosterOwner ?? this;
        Task<AfdevBioBotWalkRoutes> loading;

        lock (owner.navigationGate)
        {
            loading = owner.walkRoutes ??= AfdevBioBotWalkRoutes.LoadAsync(owner.workspace,
                world ?? throw new InvalidOperationException("路线学习尚未绑定本局地图。"), owner.log, owner.workStopping.Token);
        }

        return loading.WaitAsync(token);
    }
}
