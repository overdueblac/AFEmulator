namespace AFServer.AssaultFire.Matches.Bots;

/// <summary>
/// Merges nearby demonstration points by foot height. Forward/reverse edges propose action candidates only,
/// not proof that the current character can pass.
/// </summary>
internal sealed partial class AfdevBioBotWalkGraph(AfdevBioBotPoint[] points, int[][] neighbors, int revision)
{
    public IReadOnlyList<AfdevBioBotPoint> Points { get; } = Array.AsReadOnly(points);
    public int Revision { get; } = revision;

    public static AfdevBioBotWalkGraph Build(IEnumerable<AfdevBioBotWalkPath> paths, int revision)
    {
        var points = new List<AfdevBioBotPoint>();
        var edges = new List<HashSet<int>>();
        var cells = new Dictionary<(int X, int Y, int Z), List<int>>();

        int Node(AfdevBioBotPoint point)
        {
            (int X, int Y, int Z) cell = ((int)Math.Floor(point.X / 48), (int)Math.Floor(point.Y / 48), (int)Math.Floor(point.Z / 24));

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cell.X + dx, cell.Y + dy, cell.Z + dz), out List<int>? nearby))
                            foreach (int index in nearby)
                                if (Math.Abs(points[index].Z - point.Z) <= 12 &&
                                    AfdevBioBotWalkRoutes.DistanceSquared(points[index], point) <= 24 * 24)
                                    return index;

            int result = points.Count;

            points.Add(point);
            edges.Add([]);

            if (!cells.TryGetValue(cell, out List<int>? bucket))
                cells[cell] = bucket = [];

            bucket.Add(result);
            return result;
        }

        foreach (AfdevBioBotWalkPath path in paths)
        {
            int previous = -1;

            foreach (AfdevBioBotPoint center in path.Points)
            {
                int current = Node(center with { Z = center.Z - path.Height });

                if (previous >= 0 && previous != current &&
                    AfdevBioBotWalkRoutes.DistanceSquared(points[previous], points[current]) <= 280 * 280)
                {
                    edges[previous].Add(current);
                    edges[current].Add(previous);
                }

                previous = current;
            }
        }

        return new AfdevBioBotWalkGraph(points.ToArray(), edges.Select(set => set.Order().ToArray()).ToArray(), revision);
    }

    /// <summary>
    /// Plans backward from near the enemy and proposes a few nearby candidates for the caller to check
    /// natively.
    /// </summary>
    public IReadOnlyList<AfdevBioBotWalkCandidate> Recommend(AfdevBioBotPoint origin, float height,
        AfdevBioBotPoint target, ISet<int> excluded)
    {
        AfdevBioBotPoint feet = origin with { Z = origin.Z - height };
        AfdevBioBotPoint targetFeet = target with { Z = target.Z - height };
        double[] costs = Enumerable.Repeat(double.PositiveInfinity, points.Length).ToArray();
        var pending = new PriorityQueue<int, double>();
        int[] ends = Enumerable.Range(0, points.Length).Where(index => !excluded.Contains(index) &&
                Math.Abs(points[index].Z - targetFeet.Z) <= 72 &&
                AfdevBioBotWalkRoutes.DistanceSquared(points[index], targetFeet) <= 600 * 600)
            .OrderBy(index => AfdevBioBotWalkRoutes.DistanceSquared(points[index], targetFeet)).Take(6).ToArray();

        foreach (int index in ends)
        {
            costs[index] = Math.Sqrt(AfdevBioBotWalkRoutes.DistanceSquared(points[index], targetFeet));
            pending.Enqueue(index, costs[index]);
        }

        while (pending.TryDequeue(out int current, out double cost))
        {
            if (cost > costs[current])
                continue;

            foreach (int next in neighbors[current])
            {
                if (excluded.Contains(next))
                    continue;

                double candidate = cost + Math.Sqrt(AfdevBioBotWalkRoutes.DistanceSquared(points[current], points[next]));

                if (candidate >= costs[next])
                    continue;

                costs[next] = candidate;
                pending.Enqueue(next, candidate);
            }
        }

        AfdevBioBotPoint? Source(int destination)
        {
            int source = neighbors[destination].Where(index => !excluded.Contains(index) && costs[index] > costs[destination] &&
                    AfdevBioBotWalkStep.CanStartAt(points[index], points[destination], feet))
                .OrderBy(index => AfdevBioBotWalkRoutes.DistanceSquared(points[index], feet)).FirstOrDefault(-1);

            return source < 0 ? null : points[source] with { Z = points[source].Z + height };
        }

        return Enumerable.Range(0, points.Length).Where(index => !excluded.Contains(index) && double.IsFinite(costs[index]))
            .Select(index => new RecommendJson1 { Index = index, Distance = Math.Sqrt(AfdevBioBotWalkRoutes.DistanceSquared(points[index], feet)) })
            .Where(item => item.Distance is >= 32 and <= 420 && Math.Abs(points[item.Index].Z - feet.Z) <= 192)
            .Select(item => new RecommendJson2 { Index = item.Index, Distance = item.Distance, From = Source(item.Index) })
            .OrderBy(item => item.From.HasValue ? 0 : 1)
            .ThenBy(item => item.Distance + costs[item.Index] * 1.08).Take(8)
            .Select(item => new AfdevBioBotWalkCandidate(item.Index, points[item.Index] with { Z = points[item.Index].Z + height },
                costs[item.Index], item.Distance + costs[item.Index], item.From)).ToArray();
    }
}

internal sealed record AfdevBioBotWalkCandidate(int Node, AfdevBioBotPoint Point, double RemainingCost, double TotalCost,
    AfdevBioBotPoint? From = null);
