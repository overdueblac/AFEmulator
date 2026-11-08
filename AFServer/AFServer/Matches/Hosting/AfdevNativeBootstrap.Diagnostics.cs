using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AFServer.AssaultFire.Matches;

public sealed partial class AfdevNativeBootstrap
{
    private long nextDiagnosticTick;
    private int diagnosticChanges;
    private string? lastDiagnostic;
    private string? lastStableDiagnostic;
    private DateTimeOffset? lastStableObservedUtc;
    private DateTimeOffset? openStartedUtc;
    private DateTimeOffset? openCompletedUtc;
    private bool firstWaitingCaptured;
    private bool readyDiagnosticCaptured;

    /// <summary>
    /// Retains read-only diagnostics before process shutdown, sampling at most once per second and 128
    /// changes. The final snapshot is not limited by the change cap.
    /// </summary>
    /// <remarks>
    /// Diagnostics do not determine readiness. Read/log failures cannot block revocation or process
    /// cleanup. Record only PendingURL length to avoid exposing URL parameters.
    /// </remarks>
    internal void CaptureReadinessDiagnostic(string phase, bool force = false)
    {
        if (phase == "strict-ready" && readyDiagnosticCaptured) return;
        if (phase == "strict-ready") readyDiagnosticCaptured = true;

        long now = Stopwatch.GetTimestamp();

        if (!force && now < nextDiagnosticTick) return;

        nextDiagnosticTick = now + Stopwatch.Frequency;

        try
        {
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var observed = new Dictionary<uint, uint>();
            bool namesStable = true;

            uint Read(string label, uint address)
            {
                try
                {
                    uint value = ReadUInt32(address);

                    values[label] = $"0x{value:X8}";
                    observed[address] = value;

                    return value;
                }
                catch (Exception failure)
                {
                    values[label] = $"read-failed:{failure.GetType().Name}:address=0x{address:X8}";

                    return 0;
                }
            }

            values["condition"] = WaitingCondition;
            values["expected"] = $"stage=7 mask=0x7F pending_url_count=0 map={Validation.Target.MapName} game={Validation.Target.GameClass}";
            values["expected_target_port"] = Workspace.TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

            uint engine = Read("engine", GEngine);
            uint world = Read("world", GWorld);

            Read("log", GLog);
            Read("is_client", 0x01FD69C8);
            Read("is_server", 0x01FD69CC);
            Read("is_editor", 0x01FD69AC);
            if (marker != 0)
            {
                Read("loadmap_stage", marker);
                Read("loadmap_mask", marker + 4);
            }
            if (trace != 0)
            {
                Read("setgameinfo_calls", trace);
                Read("setgameinfo_world", trace + 4);
            }
            if (engine >= 0x10000 && engine <= uint.MaxValue - 0x680)
            {
                Read("pending_url_data", engine + 0x674);
                Read("pending_url_count", engine + 0x678);
            }
            if (world != 0)
            {
                try
                {
                    uint package = Outermost(world);

                    values["world_outermost"] = $"0x{package:X8}";
                    var mapName = DescribeNameSnapshot(Process.ReadMemory, package, Validation.Target.MapName);

                    values["map_name"] = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(mapName);
                    namesStable = mapName.Stable && Outermost(world) == package;

                    uint level = Read("persistent_level", world + 0x50);
                    uint actors = level == 0 ? 0 : Read("actors", level + 0x3C);
                    uint worldInfo = actors == 0 ? 0 : Read("world_info", actors);
                    uint gameInfo = worldInfo == 0 ? 0 : Read("game_info", worldInfo + 0x414);
                    uint gameClass = gameInfo == 0 ? 0 : Read("game_class", gameInfo + 0x34);
                    uint driver = Read("net_driver", world + 0xD8);
                    uint socket = driver == 0 ? 0 : Read("socket", driver + 0x1A8);

                    if (socket != 0)
                    {
                        Read("socket_type", socket);
                        Read("socket_handle", socket + 0x14);
                        Read("socket_crypto_enabled", socket + 0x18);
                    }

                    if (gameClass != 0)
                    {
                        var className = DescribeNameSnapshot(Process.ReadMemory, gameClass, "TGBioMatch");
                        var packageName = DescribeNameSnapshot(Process.ReadMemory, Outermost(gameClass), "TGBioGame");

                        values["game_class_name"] = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(className);
                        values["game_package_name"] = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(packageName);
                        namesStable &= className.Stable && packageName.Stable;
                    }
                }
                catch (Exception failure)
                {
                    values["world_outermost"] = "read-or-outer-chain-failed:" + failure.GetType().Name;
                    namesStable = false;
                }
            }

            bool stable = namesStable && !values.Values.Any(value => value.StartsWith("read-failed:", StringComparison.Ordinal));

            foreach (var item in observed)
            {
                try
                {
                    if (ReadUInt32(item.Key) != item.Value) stable = false;
                }
                catch (Exception failure)
                {
                    values["snapshot_check"] = $"read-failed:{failure.GetType().Name}:address=0x{item.Key:X8}";
                    stable = false;
                }
            }

            string snapshot = global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(values);

            if (stable)
            {
                lastStableDiagnostic = snapshot;
                lastStableObservedUtc = DateTimeOffset.UtcNow;
            }
            if (!force && (snapshot == lastDiagnostic || diagnosticChanges >= 128)) return;

            lastDiagnostic = snapshot;
            diagnosticChanges++;
            log("NATIVE_READINESS_DIAGNOSTIC " + global::AFServer.AssaultFire.Hosting.ServerJson.Serializer.Serialize(new CaptureReadinessDiagnosticJson1
            {
                instance = Workspace.InstanceId, allocation = Workspace.AllocationId, attempt = AttemptId, pid = Process.ProcessId,
                process_created_utc = Process.CreationTimeUtc, observed_utc = DateTimeOffset.UtcNow,
                phase = phase, open_started_utc = openStartedUtc, open_completed_utc = openCompletedUtc,
                stable = stable, changes = diagnosticChanges, snapshot = values,
                last_stable_observed_utc = force ? lastStableObservedUtc : null,
                last_stable_snapshot = force ? lastStableDiagnostic : null
            }));
        }
        catch (Exception)
        {
            // Final failure logging and host cleanup must still run. Diagnostic exceptions cannot imply native success or replace the original failure.
        }
    }

    /// <summary>
    /// Name diagnostics independent of acceptance predicates, retaining actual values and rejection
    /// categories with at most 1,024 total characters read.
    /// </summary>
    internal static NativeNameDiagnostic DescribeNameSnapshot(Func<uint, int, byte[]> read, uint value, string expected)
    {
        var result = new NativeNameDiagnostic { Object = value, Expected = expected };
        uint readAddress = 0;

        byte[] Read(uint address, int length)
        {
            readAddress = address;

            return read(address, length);
        }

        uint UInt(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Read(address, 4));

        try
        {
            if (value < 0x10000 || value > uint.MaxValue - 0x34)
                return result with { Reason = "invalid-object" };

            byte[] identity = Read(value + 0x2C, 8);
            byte[] table = Read(GNames, 12);

            result = result with
            {
                Index = BinaryPrimitives.ReadUInt32LittleEndian(identity),
                Number = BinaryPrimitives.ReadUInt32LittleEndian(identity.AsSpan(4)),
                Data = BinaryPrimitives.ReadUInt32LittleEndian(table),
                Count = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4)),
                Capacity = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8))
            };

            if (result.Index == 0 || result.Index >= result.Count || result.Count > result.Capacity ||
                result.Capacity > 1048576 || result.Data < 0x10000 || (result.Data & 3) != 0 ||
                (ulong)result.Data + (ulong)result.Capacity * 4 > uint.MaxValue)
                return result with { Reason = "name-table-boundary" };

            uint slot = result.Data + result.Index * 4;

            result = result with { Entry = UInt(slot) };

            // Entries come from an unpadded name pool; do not require four-byte alignment.
            if (result.Entry < 0x10000 || result.Entry > uint.MaxValue - 2064)
                return result with { Reason = "invalid-entry-pointer" };

            result = result with { EncodedIndex = UInt(result.Entry + 8) };

            int width = (result.EncodedIndex & 1) == 0 ? 1 : 2;
            var name = new StringBuilder();
            bool terminated = false;

            result = result with { Encoding = width == 1 ? "ANSI" : "UTF-16" };

            if ((result.EncodedIndex >> 1) != result.Index)
                return result with { Reason = "entry-encoded-index-mismatch" };

            for (int position = 0; position < 1024; position++)
            {
                byte[] unit = Read(result.Entry + 16 + (uint)(position * width), width);
                char character = (char)(width == 1 ? unit[0] : BinaryPrimitives.ReadUInt16LittleEndian(unit));

                if (character == 0)
                {
                    terminated = true;
                    break;
                }

                name.Append(character);
            }

            result = result with { Actual = name.ToString(), Truncated = !terminated };

            if (!Read(value + 0x2C, 8).AsSpan().SequenceEqual(identity) ||
                !Read(GNames, 12).AsSpan().SequenceEqual(table) || UInt(slot) != result.Entry ||
                UInt(result.Entry + 8) != result.EncodedIndex)
                return result with { Reason = "snapshot-changed" };
            if (result.Number != 0) return result with { Reason = "fname-number-nonzero", Stable = true };
            if (!terminated) return result with { Reason = "name-exceeds-bound", Stable = true };

            bool matches = result.Actual.Length == expected.Length;

            for (int position = 0; matches && position < expected.Length; position++)
            {
                char actualCharacter = result.Actual[position];
                char expectedCharacter = expected[position];

                if (actualCharacter is >= 'A' and <= 'Z') actualCharacter = (char)(actualCharacter + 'a' - 'A');
                if (expectedCharacter is >= 'A' and <= 'Z') expectedCharacter = (char)(expectedCharacter + 'a' - 'A');

                matches = actualCharacter == expectedCharacter;
            }

            return result with
            {
                Reason = matches ? "matched" : "string-mismatch",
                Stable = true
            };
        }
        catch (Exception failure)
        {
            return result with { Reason = "read-failed:" + failure.GetType().Name, FailedReadAddress = readAddress };
        }
    }

    internal sealed record NativeNameDiagnostic
    {
        public uint Object { get; init; }
        public string Expected { get; init; } = "";
        public uint Index { get; init; }
        public uint Number { get; init; }
        public uint Data { get; init; }
        public uint Count { get; init; }
        public uint Capacity { get; init; }
        public uint Entry { get; init; }
        public uint EncodedIndex { get; init; }
        public string Encoding { get; init; } = "unknown";
        public string Actual { get; init; } = "";
        public string Reason { get; init; } = "unobserved";
        public bool Truncated { get; init; }
        public bool Stable { get; init; }
        public uint FailedReadAddress { get; init; }
    }
}
