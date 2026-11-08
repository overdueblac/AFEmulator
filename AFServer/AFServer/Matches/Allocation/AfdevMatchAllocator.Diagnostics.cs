namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevMatchAllocator
{
    /// <summary>
    /// Reads actual allocator state. Warm readiness does not imply an open map; querying grants no
    /// eligibility and changes no lifetime.
    /// </summary>
    public string RuntimeStatus
    {
        get
        {
            lock (gate)
            {
                if (disposed) return "已停止";
                if (warmupFailed) return "预热失败";
                if (!warmupStarted) return "待机";

                var active = allocations.Values.Count(value => value.Ready && !value.Closing && !value.Failed);
                var available = instances.Values.Count(value => value.Available);
                var failed = allocations.Values.Count(value => value.Failed);

                if (failed > 0) return $"对局异常 {failed} / 对局 {active} / 预热就绪 {available}";
                if (active > 0) return $"对局 {active} / 预热就绪 {available}";
                if (available > 0) return $"就绪 · 预热 {available}/{validation.Options.MaxInstances}";
                if (warmFailureCount > 0) return $"预热失败 {warmFailureCount} 次 · 自动重试，详见日志";

                return "预热中";
            }
        }
    }
}
