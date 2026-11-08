using AFConnect.Proxy;

namespace AFServer.AssaultFire.Hosting;

/// <summary>
/// Compile-time JSON metadata for server configuration, native-host diagnostics, and route archives.
/// </summary>
internal static class ServerJson
{
    internal static AotJsonSerializer Serializer { get; } = new(
        global::AFServer.AssaultFire.Game.ProgressionCatalog.JsonMetadata,
        global::AFServer.AssaultFire.Hosting.ClientPreflight.JsonMetadata,
        global::AFServer.AssaultFire.Hosting.ServerOptions.JsonMetadata,
        global::AFServer.AssaultFire.Hosting.MaintenanceCommands.JsonMetadata,
        global::AFServer.AssaultFire.Matches.RuntimeManifest.JsonMetadata,
        global::AFServer.AssaultFire.Matches.AfdevInstanceWorkspace.JsonMetadata,
        global::AFServer.AssaultFire.Matches.AfdevLeaseRelay.JsonMetadata,
        global::AFServer.AssaultFire.Matches.AfdevNativeBootstrap.JsonMetadata,
        global::AFServer.AssaultFire.Matches.AfdevProcessHost.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Objects.Ue3SubobjectDefaultsLoader.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRuntime.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkRoutes.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioPlayerRouteRecorder.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotPatrol.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotCombat.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotControlInitialization.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotEngagement.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotMotionRun.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigation.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotNavigationStop.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotReachQuery.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotRouteRun.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.AfdevBioBotWalkGraph.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.FactoryInspector.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.FactoryRun.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.LifecycleCallRun.JsonMetadata,
        global::AFServer.AssaultFire.Matches.Bots.ResourceCallRun.JsonMetadata);
}
