using System.Buffers.Binary;

namespace AFServer.AssaultFire.Matches.Bots;
sealed partial class BioResourceInspector
{
    /// <summary>
    /// Conservative precheck for base-class loading completion. LoadingTimerAbsent requires no TimerData
    /// with the matching name; never infer active semantics from Rate/Paused. A true result excludes
    /// automatic Restart. This check and notification must run consecutively through the same native-thread
    /// gate; external snapshots are not locks.
    /// </summary>
    public ResourceLoadingState CaptureLoadingState()
    {
        ResourceBinding current = CheckBinding();
        uint controller = current.Controller.Address;
        uint pri = current.Pri.Address;
        var bits = new List<ResourceBool>
        {
            Boolean(controller, "UTGame.TGPlayerController.bInitialProcessingComplete"),
            Boolean(controller, "UTGame.TGPlayerController.bWaitingLoadingProcessComplete"),
            Boolean(pri, "UTGame.TGPlayerReplicationInfo.bLoadingComplete"),
            Boolean(pri, "Engine.PlayerReplicationInfo.bOnlySpectator"),
            Boolean(pri, "Engine.PlayerReplicationInfo.bIsSpectator"),
            Boolean(pri, "Engine.PlayerReplicationInfo.bWaitingPlayer")
        };
        ResourceArray array = Array(new ResourceValue(controller, current.Controller.Class), "Engine.Actor.Timers", 256);
        uint type = StructElementType(array, "Engine.Actor.TimerData");
        uint stride = reader.U32(type + 0x50);
        ResourceProperty name = Property(type, "Engine.Actor.TimerData.FuncName", "NameProperty", 8);
        ResourceProperty obj = Property(type, "Engine.Actor.TimerData.TimerObj", "ObjectProperty", 4);
        ResourceProperty rate = Property(type, "Engine.Actor.TimerData.Rate", "FloatProperty", 4);
        var timers = new List<ResourceTimer>();

        for (uint index = 0; index < array.Count; index++)
        {
            uint address = checked(array.Data + index * stride);
            uint nameIndex = reader.U32(checked(address + name.Offset));
            uint nameNumber = reader.U32(checked(address + name.Offset + 4));
            uint owner = reader.U32(checked(address + obj.Offset));
            float seconds = BinaryPrimitives.ReadSingleLittleEndian(process.ReadMemory(checked(address + rate.Offset), 4));

            if (!float.IsFinite(seconds))
                throw new InvalidDataException("Timer rate is not finite.");
            timers.Add(new ResourceTimer(index, address, nameIndex, nameNumber, reader.Name(nameIndex), owner, seconds));
        }

        ArrayInner(array, "StructProperty", stride);
        CheckBinding();
        return new ResourceLoadingState(bits, array, timers, timers.All(timer => timer.Name != "TimerCheckClientLoadingProcessComplete"));
    }

    private ResourceBool Boolean(uint instance, string path)
    {
        QueryObject identity = reader.Identity(instance);
        ResourceProperty property = Property(identity.Class, path, "BoolProperty", 4);
        uint mask = reader.U32(property.Identity.Address + 0x84);
        uint address = checked(instance + property.Offset);
        uint word = reader.U32(address);

        if (mask == 0 || (mask & (mask - 1)) != 0)
            throw new InvalidDataException("Reflected Boolean mask is not a single bit: " + path);
        return new ResourceBool(path, address, mask, word, (word & mask) != 0);
    }
}

sealed record ResourceBool(string Path, uint Address, uint Mask, uint Word, bool Value);
sealed record ResourceTimer(uint Index, uint Address, uint NameIndex, uint NameNumber, string Name, uint Owner, float Rate);
sealed record ResourceLoadingState(IReadOnlyList<ResourceBool> Bits, ResourceArray TimerArray, IReadOnlyList<ResourceTimer> Timers, bool LoadingTimerAbsent);
