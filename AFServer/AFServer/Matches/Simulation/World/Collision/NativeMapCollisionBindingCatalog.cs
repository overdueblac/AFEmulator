using System.Collections.Immutable;
using System.Numerics;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Support check for one source convex shape in local coordinates, excluding Actor world transforms or
/// collision decisions.
/// </summary>
public sealed record NativeMapLocalShapeAssessment(NativeConvexCollisionResource Resource, string? UnsupportedReason)
{
    /// <summary>
    /// Whether original vertices/faces pass current strict convex checks. Revalidate actual transforms in
    /// world coordinates.
    /// </summary>
    public bool IsSupportedInResourceCoordinates => UnsupportedReason is null;
}

/// <summary>
/// Binds an actual Level member's component chain to same-version BodySetup and all source convex shapes.
/// Retains missing resources, unsupported shapes, and unmet world conditions separately. This record is not
/// a world-collision instance.
/// </summary>
public sealed record NativeMapCollisionBinding(NativeMapStaticMeshBinding Instance, NativeCollisionBodySource? BodySource,
    ImmutableArray<NativeMapLocalShapeAssessment> Shapes, ImmutableArray<string> WorldInstantiationBlockers);

/// <summary>
/// Cross-validates loaded, source-pinned map/collision catalogs without reading clients, creating worlds,
/// or activating streaming levels. Validates every BodySetup source/shape before immutable publication; any
/// contradiction rejects the whole binding.
/// </summary>
public sealed class NativeMapCollisionBindingCatalog
{
    private NativeMapCollisionBindingCatalog(ImmutableArray<NativeMapLocalShapeAssessment> shapes,
        ImmutableArray<NativeMapCollisionBinding> bindings)
    {
        Shapes = shapes;
        Bindings = bindings;
    }

    /// <summary>Complete local-shape support results, including unreferenced/unsupported resources.</summary>
    public ImmutableArray<NativeMapLocalShapeAssessment> Shapes { get; }

    /// <summary>
    /// One source binding per map-instance chain, preserving original order and missing results.
    /// </summary>
    public ImmutableArray<NativeMapCollisionBinding> Bindings { get; }

    /// <summary>
    /// Binds only when map contents, complete BodySetup inventory, and every object's physical
    /// version/serial sources match exactly.
    /// </summary>
    /// <param name="map">Map-instance catalog validated against an out-of-band manifest.</param>
    /// <param name="collision">
    /// Local collision catalog validated against an out-of-band manifest and source serials.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Map sources, BodySetup inventory, or physical-object provenance differ.
    /// </exception>
    public static NativeMapCollisionBindingCatalog Bind(NativeMapInstanceCatalog map, NativeCollisionResourceCatalog collision)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(collision);
        Require(map.CandidatesSha256 == collision.SourceMapCandidatesSha256
            && map.SelectedPhysicalMap == collision.SelectedPhysicalMap,
            "地图实例与碰撞资源不是同一源地图产物。");

        var sourceBodies = collision.Bodies.ToDictionary(x => x.Id);

        Require(sourceBodies.Count == map.BodySetups.Length, "地图与碰撞目录的完整 BodySetup 库存不一致。");
        foreach (NativeMapObject body in map.BodySetups)
        {
            Require(sourceBodies.TryGetValue(body.Id, out NativeCollisionBodySource? source)
                && source.ObjectPath == body.ObjectPath && source.Source == body.Source
                && source.NativeTail == body.NativeTailSource,
                $"BodySetup 物理包版本、export、路径或 serial 来源不一致：{body.Id.PhysicalPackage}#{body.Id.ExportIndex}。");
        }

        var assessments = ImmutableArray.CreateBuilder<NativeMapLocalShapeAssessment>(collision.Resources.Length);

        foreach (NativeConvexCollisionResource resource in collision.Resources)
        {
            string? unsupportedReason = null;

            try
            {
                // Identity matrices check original resources only; they never substitute for missing instance world transforms.
                _ = resource.CreateCollider(Matrix4x4.Identity);
            }
            catch (InvalidDataException error)
            {
                unsupportedReason = error.Message;
            }

            assessments.Add(new(resource, unsupportedReason));
        }

        ImmutableArray<NativeMapLocalShapeAssessment> allShapes = assessments.MoveToImmutable();
        var shapeById = allShapes.ToDictionary(x => x.Resource.Id);
        var bindings = ImmutableArray.CreateBuilder<NativeMapCollisionBinding>(map.StaticMeshBindings.Length);

        foreach (NativeMapStaticMeshBinding instance in map.StaticMeshBindings)
        {
            NativeCollisionBodySource? source = instance.BodySetup is null ? null : sourceBodies[instance.BodySetup.Id];
            ImmutableArray<NativeMapLocalShapeAssessment> shapes = source is null ? []
                : source.Resources.Select(x => shapeById[x.Id]).ToImmutableArray();
            var blockers = instance.WorldInstantiationBlockers.ToBuilder();

            if (source is not null)
            {
                if (source.NativeTail.Bytes > 0) blockers.Add("body_setup_native_tail_unresolved");
                if (!source.MissingShapeArrays.IsEmpty) blockers.Add("other_shape_array_defaults_unresolved");
                if (shapes.IsEmpty) blockers.Add("explicit_convex_geometry_empty");
                if (shapes.Any(x => !x.IsSupportedInResourceCoordinates)) blockers.Add("explicit_convex_geometry_unsupported");
            }

            bindings.Add(new(instance, source, shapes, blockers.ToImmutable()));
        }

        return new(allShapes, bindings.MoveToImmutable());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
