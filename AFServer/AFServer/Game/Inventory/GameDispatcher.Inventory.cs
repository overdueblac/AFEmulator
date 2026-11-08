using System.Text.Json.Nodes;

namespace AFServer.AssaultFire.Game;

public sealed partial class GameDispatcher
{
    private int DefaultLocation(uint item) => int.TryParse(catalog!["item_default_locations"]?[item.ToString()]?.ToString(), out var value) ? value : -1;
    private bool IsWeapon(uint item)
    {
        var ids = catalog!["weapon_commodities"]!.AsArray().Select(v => v!.ToString()).ToHashSet();

        return catalog["shop_item_map"]!.AsObject().Any(p => ids.Contains(p.Key) && p.Value!.ToString() == item.ToString());
    }

    private async ValueTask OperatePropAsync(GameSession session, PlayerState player, ushort command, byte[] body, CancellationToken cancellationToken)
    {
        var reader = new TdrReader(body);
        var operation = reader.U16();
        var subjectId = reader.U64();
        var target = reader.U64();
        var location = reader.U8();

        reader.End();

        var subject = player.Props.FirstOrDefault(p => PlayerState.Number(p, "gid") == subjectId);

        // This client encodes MK4 melee unequip as location=0; do not unequip the primary weapon based on that field.
        if (subject is null && subjectId == 0 && operation == 0
            && player.Props.Any(p => PlayerState.Number(p, "gid") == target && BackpackLoadouts.IsBackpack(PlayerState.Number(p, "item_id"))))
        {
            var candidates = player.Props.Where(p => PlayerState.Number(p, "owner_gid") == target
                // Slot unequip without SubjectGID refers only to weapons. Skills may share the same numeric location and must remain equipped.
                && IsWeapon((uint)PlayerState.Number(p, "item_id"))
                && (location == 0
                    ? PlayerState.Number(p, "item_id") == 100058 && PlayerState.Number(p, "location") == 2
                    : location is >= 1 and <= 5 && PlayerState.Number(p, "location") == location)).ToArray();

            if (candidates.Length == 1)
            {
                subject = candidates[0]; subjectId = PlayerState.Number(subject, "gid"); operation = 1;
            }
        }

        ushort result = GameWire.ZoneSuccess;

        if (subject is null || operation > 2) result = 0x8101;
        else
        {
            var item = (uint)PlayerState.Number(subject, "item_id");
            var slot = DefaultLocation(item);

            if (operation == 2)
            {
                if (subjectId == player.CurrentRole || subjectId == ((player.Uin << 32) | 1) || BackpackLoadouts.IsBackpack(item)) result = 0x8101;
                else
                {
                    foreach (var component in player.Props.Where(p => PlayerState.Number(p, "owner_gid") == subjectId).ToArray())
                        player.Inventory.Remove(component);
                    player.Inventory.Remove(subject);
                }
            }
            else if (operation == 1)
            {
                if (BackpackLoadouts.IsBackpack(item)) result = 0x8101;
                else
                {
                    target = 0; location = 12;
                    subject["owner_gid"] = 0UL; subject["location"] = 12;
                }
            }
            else if (slot is 10 or 11)
            {
                target = 1; location = (byte)slot;
                UnmountConflicts(player, subject, target, location);
                subject["owner_gid"] = target; subject["location"] = location;
                player.CurrentRole = subjectId;
            }
            else if (BackpackLoadouts.IsBackpack(item))
            {
                foreach (var bag in player.Props.Where(p => BackpackLoadouts.IsBackpack(PlayerState.Number(p, "item_id"))))
                {
                    bag["owner_gid"] = ReferenceEquals(bag, subject) ? 1UL : 0UL;
                    bag["location"] = 12;
                }
                target = 1; location = 12; player.CurrentBag = subjectId;
            }
            else if (FunctionalEquipment.TryGetLocation(item, out var functionalLocation))
            {
                // Functional items belong on the player root. The UI may return an old inventory location; recover the actual slot from the item table.
                // Attaching to the current backpack would make helmets and armor displace each other and consume grenade slots 3/4.
                target = 1;
                location = functionalLocation;
                UnmountConflicts(player, subject, target, location);
                subject["owner_gid"] = target;
                subject["location"] = location;
            }
            else
            {
                var accessory = AvatarAccessories.Contains(item);

                // TargetGID=1 is reserved for actual backpack and character root attachments. Bundles must not disrupt the unique backpack relationship.
                if (target == 0 || target == 1 || target == player.CurrentRole || (accessory && target == player.CurrentBag))
                    target = accessory ? player.CurrentRole : player.CurrentBag;

                var owner = player.Props.FirstOrDefault(p => PlayerState.Number(p, "gid") == target);

                if (owner is null || (accessory && DefaultLocation((uint)PlayerState.Number(owner, "item_id")) is not (10 or 11))) result = 0x8101;
                else
                {
                    if (location == 12 && IsWeapon(item) && slot is >= 0 and <= 5) location = (byte)slot;
                    if (location == 12 && accessory && slot is >= 0 and <= 2) location = (byte)slot;
                    if (location != 12) UnmountConflicts(player, subject, target, location);
                    subject["owner_gid"] = target; subject["location"] = location;
                }
            }
        }
        if (result == GameWire.ZoneSuccess) await repository.SavePlayerAsync(session.Uin, player.Document, cancellationToken);

        var effective = new TdrWriter().U16(operation).U64(subjectId).U64(target).U8(location).ToArray();

        await SendAsync(session, command == 0xA200 ? (ushort)0xA201 : (ushort)0xA009,
            new TdrWriter().U16(result).Bytes(effective), cancellationToken);
        if (command == 0xA008 && result == GameWire.ZoneSuccess)
        {
            await SendAsync(session, 0xA00A, new TdrWriter().Bytes(effective), cancellationToken);

            var item = subject is null ? 0U : (uint)PlayerState.Number(subject, "item_id");

            // Preserve existing operation responses and notifications. Resending A006 after switching backpacks repeatedly rebuilds inventory and weapon previews.
            if (operation == 0 && DefaultLocation(item) is 10 or 11)
            {
                await session.SendAsync(ProfilePackets.PlayerInfo(player, player.CurrentRole), cancellationToken);
                // After changing character, replay only current-backpack attachments so a full inventory refresh does not switch the weapon view to another backpack.
                await SendAsync(session, 0xA00A, new TdrWriter().U16(0).U64(player.CurrentBag).U64(1).U8(12), cancellationToken);
            }
        }
    }

    private static void UnmountConflicts(PlayerState player, JsonObject subject, ulong owner, byte location)
    {
        // Native EquipWithProp does not unequip same-slot items at location 255; multiple functional items without fixed slots may coexist.
        if (location == byte.MaxValue) return;

        foreach (var other in player.Props.Where(p => !ReferenceEquals(p, subject) && PlayerState.Number(p, "owner_gid") == owner
            && PlayerState.Number(p, "location") == location))
        {
            other["owner_gid"] = 0UL; other["location"] = 12;
        }
    }
}
