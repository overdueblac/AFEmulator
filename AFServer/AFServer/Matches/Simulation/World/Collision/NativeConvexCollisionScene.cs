using System.Collections.Immutable;
using System.Numerics;

namespace AFServer.AssaultFire.Matches.Simulation;

/// <summary>
/// Explicit closed convex triangle resource. Vertices/FaceTriData may come from offline BodySetup
/// extraction, but callers provide confirmed world transforms. Never infer transforms/collision enablement
/// from candidates, package names, or missing component fields. Does not replace PhysX cooked meshes.
/// </summary>
public sealed class NativeConvexCollider
{
    public const int MaximumVertices = 4096;
    public const int MaximumTriangles = 8192;
    internal readonly ImmutableArray<Face> Faces;
    internal readonly ImmutableArray<(Vector3 A, Vector3 B)> Edges;
    internal readonly ImmutableArray<Vector3> Vertices;
    internal readonly Vector3 Minimum;
    internal readonly Vector3 Maximum;
    internal readonly float Tolerance;

    public NativeConvexCollider(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangleIndices,
        Matrix4x4 localToWorld)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangleIndices);
        if (vertices.Count is < 4 or > MaximumVertices || triangleIndices.Count < 12 ||
            triangleIndices.Count % 3 != 0 || triangleIndices.Count / 3 > MaximumTriangles)
            throw new InvalidDataException("凸碰撞顶点或三角面数量无效。");
        if (!Finite(localToWorld) || localToWorld.M14 != 0 || localToWorld.M24 != 0 ||
            localToWorld.M34 != 0 || localToWorld.M44 != 1 ||
            !Matrix4x4.Invert(localToWorld, out Matrix4x4 inverse) || !Finite(inverse))
            throw new InvalidDataException("碰撞变换必须是有限可逆仿射矩阵。");

        var points = vertices.Select(v => Vector3.Transform(v, localToWorld)).ToImmutableArray();

        if (vertices.Any(v => !Finite(v)) || points.Any(v => !Finite(v) || AbsMax(v) > 1_000_000))
            throw new InvalidDataException("碰撞顶点非有限或超出支持的世界范围。");
        Minimum = points.Aggregate(Vector3.Min);
        Maximum = points.Aggregate(Vector3.Max);

        float span = AbsMax(Maximum - Minimum);

        if (span < 0.0001f) throw new InvalidDataException("碰撞几何退化。");
        Tolerance = Math.Max(0.00001f, span * 0.000002f);

        double centerX = 0, centerY = 0, centerZ = 0;

        foreach (Vector3 point in points) { centerX += point.X; centerY += point.Y; centerZ += point.Z; }

        Vector3 center = new((float)(centerX / points.Length), (float)(centerY / points.Length), (float)(centerZ / points.Length));
        var faces = ImmutableArray.CreateBuilder<Face>();
        var edgeCounts = new Dictionary<(int, int), (int Count, int Winding)>();
        var uniqueTriangles = new HashSet<(int, int, int)>();

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            int a = triangleIndices[i], b = triangleIndices[i + 1], c = triangleIndices[i + 2];

            if ((uint)a >= points.Length || (uint)b >= points.Length || (uint)c >= points.Length || a == b || b == c || a == c)
                throw new InvalidDataException("凸面包含越界或重复顶点索引。");

            int[] sorted = [a, b, c]; Array.Sort(sorted);
            if (!uniqueTriangles.Add((sorted[0], sorted[1], sorted[2])))
                throw new InvalidDataException("凸面包含重复三角形。");

            Vector3 cross = Vector3.Cross(points[b] - points[a], points[c] - points[a]);

            if (cross.Length() <= Tolerance * Tolerance)
                throw new InvalidDataException("凸面包含退化三角形。");

            Vector3 normal = Vector3.Normalize(cross);

            // Source winding reconstructs closed surfaces only. Use all-vertex centroid to orient outward normals consistently, including mirrored transforms.
            if (Vector3.Dot(normal, center - points[a]) > 0) { (b, c) = (c, b); normal = -normal; }
            if (Vector3.Dot(normal, center - points[a]) >= -Tolerance)
                throw new InvalidDataException("凸体没有可分辨的内部体积。");
            foreach (Vector3 point in points)
                if (Vector3.Dot(normal, point - points[a]) > Tolerance)
                    throw new InvalidDataException("三角面并非凸体支撑面。");
            faces.Add(new Face(points[a], points[b], points[c], normal));
            AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
        }
        if (edgeCounts.Values.Any(e => e.Count != 2 || e.Winding != 0))
            throw new InvalidDataException("凸体三角面不是闭合二维流形。");
        Faces = faces.ToImmutable();
        Edges = edgeCounts.Keys.Select(e => (points[e.Item1], points[e.Item2])).ToImmutableArray();
        Vertices = points;

        void AddEdge(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));

            edgeCounts.TryGetValue(key, out var previous);
            edgeCounts[key] = (previous.Count + 1, previous.Winding + (a < b ? 1 : -1));
        }
    }

    public int VertexCount => Vertices.Length;
    public int TriangleCount => Faces.Length;

    internal bool Inside(Vector3 point) => Faces.All(f =>
        (double)f.Normal.X * (point.X - f.A.X) + (double)f.Normal.Y * (point.Y - f.A.Y) +
        (double)f.Normal.Z * (point.Z - f.A.Z) <= 0);
    internal bool BoundsOverlap(Vector3 minimum, Vector3 maximum) =>
        minimum.X <= Maximum.X && minimum.Y <= Maximum.Y && minimum.Z <= Maximum.Z &&
        maximum.X >= Minimum.X && maximum.Y >= Minimum.Y && maximum.Z >= Minimum.Z;
    internal static float AbsMax(Vector3 value) => Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    internal readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal);
}

/// <summary>
/// Immutable static convex scene. Ray queries clip against faces; continuous sphere sweeps intersect faces,
/// edges, and vertices to prevent thin-wall tunneling. Independent spherical motion/hit queries only,
/// without UE3 capsule steps, gravity, dynamic bodies, terrain/BSP, or streaming semantics.
/// </summary>
public sealed class NativeConvexCollisionScene : INativeCollisionScene
{
    private readonly ImmutableArray<NativeConvexCollider> colliders;

    public NativeConvexCollisionScene(IReadOnlyList<NativeConvexCollider> colliders)
    {
        ArgumentNullException.ThrowIfNull(colliders);
        if (colliders.Count > 100_000 || colliders.Any(c => c is null) || colliders.Sum(c => (long)c.TriangleCount) > 2_000_000)
            throw new InvalidDataException("碰撞场景为空对象或超过资源上限。");
        this.colliders = colliders.ToImmutableArray();
    }

    public bool OverlapsSphere(Vector3 center, float radius)
    {
        ValidatePoint(center); ValidateRadius(radius);
        foreach (var collider in colliders)
        {
            if (!collider.BoundsOverlap(center - new Vector3(radius), center + new Vector3(radius))) continue;
            if (collider.Inside(center)) return true;

            // Require strict overlap. Exact contact permits sliding/departure rather than counting stationary contact as penetration.
            float effective = Math.Max(0, radius - Math.Min(collider.Tolerance, radius * 0.001f));

            if (collider.Faces.Any(f => Vector3.DistanceSquared(center, Closest(center, f)) < effective * effective)) return true;
        }
        return false;
    }

    public bool Raycast(Vector3 origin, Vector3 normalizedDirection, float maxDistance, out NativeCollisionHit hit)
    {
        ValidatePoint(origin); ValidateDirection(normalizedDirection); ValidateDistance(maxDistance);

        float nearest = maxDistance;
        bool found = false;

        hit = default;
        foreach (var collider in colliders)
        {
            Vector3 end = origin + normalizedDirection * nearest;

            if (!collider.BoundsOverlap(Vector3.Min(origin, end), Vector3.Max(origin, end))) continue;
            if (collider.Inside(origin))
            {
                // Interior/surface starts hit at distance zero, but normals still come from the actual nearest support face, never fabricated opposite the ray.
                NativeConvexCollider.Face closestFace = collider.Faces.MaxBy(face => Dot(face.Normal, origin - face.A));

                nearest = 0; found = true;
                hit = new NativeCollisionHit(0, origin, closestFace.Normal);
                continue;
            }

            double enter = 0, leave = nearest;
            Vector3 enterNormal = -normalizedDirection;
            bool intersects = true;

            foreach (var face in collider.Faces)
            {
                double distance = Dot(face.Normal, origin - face.A);
                double speed = Dot(face.Normal, normalizedDirection);

                // Small velocity is not parallelism: long rays can still enter thin faces, which fixed angular epsilon would miss.
                if (speed == 0)
                {
                    if (distance > 0) { intersects = false; break; }
                    continue;
                }

                double t = -distance / speed;

                if (speed < 0) { if (t > enter) { enter = t; enterNormal = face.Normal; } }
                else leave = Math.Min(leave, t);
                if (enter > leave) { intersects = false; break; }
            }
            if (!intersects || enter < 0 || enter > nearest || leave < 0) continue;
            nearest = (float)enter; found = true;
            hit = new NativeCollisionHit(nearest, origin + normalizedDirection * nearest, enterNormal);
        }
        return found;
    }

    public bool SweepSphere(Vector3 start, Vector3 end, float radius, out NativeCollisionHit hit)
    {
        ValidatePoint(start); ValidatePoint(end); ValidateRadius(radius);

        Vector3 movement = end - start;
        float length = movement.Length();

        ValidateDistance(length);

        Vector3 direction = length > 0 ? movement / length : Vector3.UnitX;
        float nearest = length;
        bool found = false;
        NativeCollisionHit result = default;

        foreach (var collider in colliders)
        {
            if (!collider.BoundsOverlap(Vector3.Min(start, end) - new Vector3(radius), Vector3.Max(start, end) + new Vector3(radius))) continue;

            Vector3 closest = default;
            Vector3 closestFaceNormal = default;
            float distanceSquared = float.PositiveInfinity;

            foreach (var face in collider.Faces)
            {
                Vector3 candidate = Closest(start, face);
                float distance = Vector3.DistanceSquared(start, candidate);

                if (distance < distanceSquared) { distanceSquared = distance; closest = candidate; closestFaceNormal = face.Normal; }
            }

            float effective = Math.Max(0, radius - Math.Min(collider.Tolerance, radius * 0.001f));
            bool inside = collider.Inside(start);

            if (inside || distanceSquared < effective * effective)
            {
                Vector3 delta = start - closest;
                Vector3 normal = !inside && delta.LengthSquared() > 0 ? Vector3.Normalize(delta) : closestFaceNormal;

                hit = new NativeCollisionHit(0, closest, normal); return true;
            }
            if (length == 0) continue;
            if (distanceSquared <= radius * radius)
            {
                // OverlapsSphere contact tolerance permits only tangential/departing motion. After slight penetration with inward movement, the actual entry root is negative;
                // return zero distance here rather than discard that root and miss the whole sweep.
                Vector3 delta = start - closest;

                if (delta.LengthSquared() > 0 && Dot(direction, delta) < 0)
                {
                    hit = new NativeCollisionHit(0, closest, Vector3.Normalize(delta));
                    return true;
                }
            }
            foreach (var face in collider.Faces)
            {
                double speed = Dot(direction, face.Normal);

                if (speed >= 0) continue;

                double distance = Dot(start - face.A, face.Normal);
                double entry = (radius - distance) / speed;

                if (entry < -collider.Tolerance || entry > nearest) continue;

                float t = (float)Math.Max(0, entry);
                Vector3 point = start + direction * t - face.Normal * radius;

                if (InTriangle(point, face, collider.Tolerance)) Accept(t, point, face.Normal);
            }
            if (radius == 0) continue;
            foreach (var edge in collider.Edges)
            {
                Vector3 axis = edge.B - edge.A;
                float edgeLength = axis.Length(); axis /= edgeLength;

                Vector3 relative = start - edge.A;
                float axialVelocity = Vector3.Dot(direction, axis), axialStart = Vector3.Dot(relative, axis);
                Vector3 radialVelocity = direction - axis * axialVelocity;
                Vector3 radialStart = relative - axis * axialStart;

                if (!FirstRoot(Dot(radialVelocity, radialVelocity), 2 * Dot(radialStart, radialVelocity),
                        Dot(radialStart, radialStart) - (double)radius * radius, out float t)) continue;
                if (t < -collider.Tolerance || t > nearest) continue;
                t = Math.Max(0, t);

                float along = axialStart + t * axialVelocity;

                if (along < 0 || along > edgeLength) continue;

                Vector3 point = edge.A + axis * along;
                Vector3 delta = start + direction * t - point;

                if (delta.LengthSquared() > 0) Accept(t, point, Vector3.Normalize(delta));
            }
            foreach (Vector3 point in collider.Vertices)
            {
                Vector3 relative = start - point;

                if (!FirstRoot(1, 2 * Dot(relative, direction), Dot(relative, relative) - (double)radius * radius, out float t)) continue;
                if (t < -collider.Tolerance || t > nearest) continue;
                t = Math.Max(0, t);

                Vector3 delta = start + direction * t - point;

                if (delta.LengthSquared() > 0) Accept(t, point, Vector3.Normalize(delta));
            }
        }
        hit = result;
        return found;

        void Accept(float distance, Vector3 point, Vector3 normal)
        {
            // Accept only entering intersections. Tangential/departing contacts do not block; interior starts are handled separately.
            if (Dot(direction, normal) >= 0 || distance > nearest) return;
            nearest = distance; found = true;
            result = new NativeCollisionHit(distance, point, normal);
        }
    }

    private static bool FirstRoot(double a, double b, double c, out float root)
    {
        root = 0;
        if (a <= 0) return false;

        double discriminant = b * b - 4 * a * c;

        if (discriminant < 0) return false;

        // Use stable quadratic roots to reduce cancellation error for long sweeps and small radii.
        double q = -0.5 * (b + Math.CopySign(Math.Sqrt(discriminant), b));
        double value = q == 0 ? -b / (2 * a) : Math.Min(q / a, c / q);

        root = (float)value;
        return float.IsFinite(root);
    }

    private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;
    private static bool InTriangle(Vector3 p, NativeConvexCollider.Face f, float tolerance) =>
        Vector3.Dot(Vector3.Cross(f.B - f.A, p - f.A), f.Normal) >= -tolerance * (f.B - f.A).Length() &&
        Vector3.Dot(Vector3.Cross(f.C - f.B, p - f.B), f.Normal) >= -tolerance * (f.C - f.B).Length() &&
        Vector3.Dot(Vector3.Cross(f.A - f.C, p - f.C), f.Normal) >= -tolerance * (f.A - f.C).Length();

    // Nearest point in triangle Voronoi regions, including faces, edges, and vertices.
    private static Vector3 Closest(Vector3 p, NativeConvexCollider.Face f)
    {
        Vector3 ab = f.B - f.A, ac = f.C - f.A, ap = p - f.A;
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);

        if (d1 <= 0 && d2 <= 0) return f.A;

        Vector3 bp = p - f.B;
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);

        if (d3 >= 0 && d4 <= d3) return f.B;

        double vc = d1 * d4 - d3 * d2;

        if (vc <= 0 && d1 >= 0 && d3 <= 0) return f.A + ab * (float)(d1 / (d1 - d3));

        Vector3 cp = p - f.C;
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);

        if (d6 >= 0 && d5 <= d6) return f.C;

        double vb = d5 * d2 - d1 * d6;

        if (vb <= 0 && d2 >= 0 && d6 <= 0) return f.A + ac * (float)(d2 / (d2 - d6));

        double va = d3 * d6 - d5 * d4;

        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return f.B + (f.C - f.B) * (float)((d4 - d3) / (d4 - d3 + d5 - d6));

        double denominator = 1 / (va + vb + vc);

        return f.A + ab * (float)(vb * denominator) + ac * (float)(vc * denominator);
    }

    private static void ValidatePoint(Vector3 point)
    {
        if (!NativeConvexCollider.Finite(point) || NativeConvexCollider.AbsMax(point) > 1_000_000)
            throw new InvalidDataException("碰撞查询位置非有限或越界。");
    }
    private static void ValidateDirection(Vector3 direction)
    {
        if (!NativeConvexCollider.Finite(direction) || Math.Abs(direction.LengthSquared() - 1) > 0.0001f)
            throw new InvalidDataException("射线方向必须是有限单位向量。");
    }
    private static void ValidateDistance(float distance)
    {
        if (!float.IsFinite(distance) || distance < 0 || distance > 4_000_000)
            throw new InvalidDataException("碰撞查询距离越界。");
    }
    private static void ValidateRadius(float radius)
    {
        if (!float.IsFinite(radius) || radius < 0 || radius > 100_000)
            throw new InvalidDataException("碰撞球半径越界。");
    }
}
