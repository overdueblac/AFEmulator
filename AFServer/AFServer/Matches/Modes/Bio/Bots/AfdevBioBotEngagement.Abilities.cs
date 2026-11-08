namespace AFServer.AssaultFire.Matches.Bots;

internal sealed partial class AfdevBioBotEngagement
{
    private DateTimeOffset nextSkillAt;
    private DateTimeOffset nextHeroAt;

    /// <summary>
    /// Submits skill/hero input through the same behavior task after retiring movement. Native rules own
    /// eligibility, health costs, cooldowns, and Pawn replacement.
    /// </summary>
    private async Task<AfdevBioBotCombatDecision?> UpdateAbilitiesAsync(AfdevBioBotCombatSnapshot current,
        CombatTarget? target, bool navigationActive, CancellationToken token)
    {
        if (Byte(participant.Controller, "Engine.PlayerController.bIgnoreMoveInput") != 0)
        {
            await CeaseFireAsync().ConfigureAwait(false);
            return Decision(current, target, true, "NativeMovementLocked");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (now >= nextHeroAt && reader.IsA(current.Pawn.Address, "TGBioGame.TGBioPlayerPawn") &&
            !Bit(participant.Pri, "TGBioGame.TGBioPlayerReplicationInfo.bBoss"))
        {
            uint gri = reader.U32(Field(world.WorldInfo, "Engine.WorldInfo.GRI"));

            if (IsPointer(gri) && reader.IsA(gri, "TGBioGame.TGBioGameReplicationInfo") &&
                Bit(gri, "TGBioGame.TGBioGameReplicationInfo.bCanChangeHumanHero"))
            {
                if (navigationActive)
                    return Decision(current, target, true, "RetireNavigationForHero");

                await CeaseFireAsync().ConfigureAwait(false);

                AfdevBioBotCombatResult result = await combat.ExecuteAsync(AfdevBioBotCombatAction.BecomeHero,
                    current, cancellationToken: token).ConfigureAwait(false);

                nextHeroAt = now.AddSeconds(2);
                save("behavior-hero-request", new UpdateAbilitiesAsyncJson1 { Pawn = current.Pawn, result = result,
                    Basis = "原生 GRI 已提示资格，ServerChangeToHumanHero 仍再次核对人数与回合。" });

                // Normal success replaces the Pawn and invalidates the old snapshot. Publish no navigation or attacks before a new complete sample.
                return Decision(current, target, true, "HeroRequested");
            }

            nextHeroAt = now.AddSeconds(1);
        }

        if (target == null || now < nextSkillAt || !reader.IsA(current.Pawn.Address, "TGBioGame.TGBioZombiePawn"))
            return null;

        ReadySkill? skill = ReadReadySkill(current, target);

        if (skill == null)
        {
            nextSkillAt = now.AddMilliseconds(500);
            return null;
        }
        if (navigationActive)
            return Decision(current, target, true, "RetireNavigationForSkill");

        await CeaseFireAsync().ConfigureAwait(false);
        nextSkillAt = now.AddSeconds(2);

        if (skill.RequiresAim)
        {
            AfdevBioBotCombatResult sight = await combat.ExecuteAsync(AfdevBioBotCombatAction.LineOfSight,
                current, target.Identity.Address, token).ConfigureAwait(false);

            if (!sight.NativeExecuted || sight.Interrupted || sight.BoolResult != true)
                return null;

            AfdevBioBotCombatResult aim = await combat.ExecuteAsync(AfdevBioBotCombatAction.Aim,
                current, target.Identity.Address, token).ConfigureAwait(false);

            if (!aim.NativeExecuted || aim.Interrupted)
                return Decision(current, target, true, "SkillAimInterrupted");
        }

        AfdevBioBotCombatResult started = await combat.ExecuteAsync(AfdevBioBotCombatAction.StartSkill,
            current, cancellationToken: token).ConfigureAwait(false);

        save("behavior-skill-request", new UpdateAbilitiesAsyncJson2 { Pawn = current.Pawn, Identity = skill.Identity, ClassPath = skill.ClassPath, Target = target.Identity, started = started,
            Basis = "按当前 ZombieType 请求主动技能；管理器和技能本体再次检查冷却、施放状态与消耗。" });
        return Decision(current, target, true, "SkillRequested");
    }

    /// <summary>
    /// Low-frequency request prefilter only. Does not establish skill success or modify native cooldowns.
    /// </summary>
    private ReadySkill? ReadReadySkill(AfdevBioBotCombatSnapshot current, CombatTarget target)
    {
        uint manager = reader.U32(Field(participant.Controller, "TGBioGame.TGBioPlayerController.BioSkillMgr"));

        if (!IsPointer(manager) || Deleted(manager) || !reader.IsA(manager, "TGBioGame.TGBioSkillManager") ||
            reader.U32(Field(manager, "TGBioGame.TGBioSkillManager.OwnerPC")) != participant.Controller ||
            Float(manager, "TGBioGame.TGBioSkillManager.InitCoolDownTime") > 0)
            return null;

        byte zombieType = Byte(current.Pawn.Address, "TGBioGame.TGBioZombiePawn.ZombieType");
        uint item = reader.U32(Field(manager, "TGBioGame.TGBioSkillManager.SkillChain"));
        var seen = new HashSet<uint>();

        while (IsPointer(item) && seen.Add(item) && seen.Count <= 8)
        {
            if (Deleted(item) || !reader.IsA(item, "TGBioGame.TGBioSkillBase") ||
                reader.U32(Field(item, "TGBioGame.TGBioSkillBase.OwnerPC")) != participant.Controller ||
                reader.U32(Field(item, "TGBioGame.TGBioSkillBase.OwnerSkillManager")) != manager)
                return null;

            if (Byte(item, "TGBioGame.TGBioSkillBase.ZombieType") == zombieType &&
                Bit(item, "TGBioGame.TGBioSkillBase.bInitiative"))
            {
                float time = Float(world.WorldInfo, "Engine.WorldInfo.TimeSeconds");
                float lastFire = Float(item, "TGBioGame.TGBioSkillBase.LastFireTime");
                float cooldown = Float(item, "TGBioGame.TGBioSkillBase.CooldownTime") +
                    Float(item, "TGBioGame.TGBioSkillBase.ModifCooldownTime");
                float duration = Float(item, "TGBioGame.TGBioSkillBase.FireTime");
                uint frame = reader.U32(item + 0x18);
                uint state = frame == 0 ? 0 : reader.U32(frame + 0x28);
                string statePath = state == 0 ? "" : reader.Path(state);

                if (!float.IsFinite(time) || !float.IsFinite(lastFire) || !float.IsFinite(cooldown) || !float.IsFinite(duration) ||
                    !Bit(item, "TGBioGame.TGBioSkillBase.bForceQuitCoolDown") && cooldown > 0 && lastFire > 0 && time - lastFire < cooldown + duration ||
                    statePath.EndsWith(".SkillBegin", StringComparison.Ordinal) ||
                    statePath.EndsWith(".SkillSinging", StringComparison.Ordinal) ||
                    statePath.EndsWith(".SkillFiring", StringComparison.Ordinal))
                    return null;

                QueryObject identity = reader.Identity(item);
                string classPath = reader.Path(identity.Class);
                double distance = Math.Sqrt(DistanceSquared(current.Location, target.Location));
                bool requiresAim = classPath == "TGBioGameContent.TGBioSkill_FlyCutter_Content";
                bool useful = classPath switch
                {
                    "TGBioGameContent.TGBioSkill_FastMove_Content" => distance > 220,
                    "TGBioGameContent.TGBioSkill_FlyCutter_Content" => distance is >= 200 and <= 2200,
                    "TGBioGameContent.TGBioSkill_Trample_Content" => distance <= 450,
                    "TGBioGameContent.TGBioSkill_Invisible_Content" => distance is >= 220 and <= 2200,
                    "TGBioGameContent.TGBioSkill_Shield_Content" => distance <= 1400,
                    _ => false
                };

                return useful ? new ReadySkill(identity, classPath, requiresAim) : null;
            }

            item = reader.U32(Field(item, "TGBioGame.TGBioSkillBase.Next"));
        }

        return null;
    }

    private byte Byte(uint instance, string path) => process.ReadMemory(Field(instance, path, "ByteProperty", 1), 1)[0];
    private float Float(uint instance, string path) => BitConverter.Int32BitsToSingle(
        unchecked((int)reader.U32(Field(instance, path, "FloatProperty"))));

    private sealed record ReadySkill(QueryObject Identity, string ClassPath, bool RequiresAim);
}
