using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.Game;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Spell.Event;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;

namespace NexusForever.WorldServer.Game.Spell
{
    public partial class Spell : IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public uint CastingId { get; }
        public bool IsCasting => _IsCasting();
        public bool IsFinished => status == SpellStatus.Finished || status == SpellStatus.Failed;
        public bool IsFailed => status == SpellStatus.Failed;
        public bool IsWaiting => status == SpellStatus.Waiting;
        public uint Spell4Id => parameters.SpellInfo.Entry.Id;
        public bool HasGroup(uint groupId) => parameters.SpellInfo.GroupList?.SpellGroupIds.Contains(groupId) ?? false;
        public bool HasThresholdToCast => (parameters.SpellInfo.Thresholds.Count > 0 && thresholdValue < thresholdMax) || thresholdSpells.Count > 0;
        public CastMethod CastMethod { get; }

        private readonly UnitEntity caster;
        private readonly SpellParameters parameters;
        private SpellStatus status;

        private double holdDuration;
        private uint totalThresholdTimer;
        private uint thresholdMax;
        private uint thresholdValue;
        private byte currentPhase = 255;
        private uint duration = 0;

        private readonly List<Spell> thresholdSpells = new List<Spell>();

        private readonly List<SpellTargetInfo> targets = new();
        private readonly List<Telegraph> telegraphs = new();
        private readonly List<Proxy> proxies = new();

        private readonly SpellEventManager events = new();
        private Dictionary<uint /*effectId*/, uint/*count*/> effectTriggerCount = new();

        private UpdateTimer persistCheck = new(0.1d);

        public Spell(UnitEntity caster, SpellParameters parameters)
        {
            this.caster = caster;
            this.parameters = parameters;
            CastingId = GlobalSpellManager.Instance.NextCastingId;
            status = SpellStatus.Initiating;
            CastMethod = (CastMethod)parameters.SpellInfo.BaseInfo.Entry.CastMethod;

            if (parameters.RootSpellInfo == null)
                parameters.RootSpellInfo = parameters.SpellInfo;

            if (parameters.SpellInfo.Thresholds.Count > 0)
                thresholdMax = (uint)parameters.SpellInfo.Thresholds.Count;
        }

        public void Update(double lastTick)
        {
            if (status == SpellStatus.Initiating)
                return;

            events.Update(lastTick);
            CheckPersistance(lastTick);

            // For Threshold Spells, a SpellStatus of Waiting is used to more accurately check state.
            if (status == SpellStatus.Executing && HasThresholdToCast)
                status = SpellStatus.Waiting;

            if (status == SpellStatus.Waiting && CastMethod == CastMethod.ChargeRelease)
            {
                holdDuration += lastTick;

                // For Charge+Hold Spells, they have a maximum time that can be held before the effect will fire. Execute effect at maximum time.
                // This will fire the Child spell, and then clean up this spell in the rest of this loop.
                if (holdDuration >= totalThresholdTimer)
                    HandleThresholdCast();
            }

            // Update all Child Spells that are triggered from Execute thresholds.
            thresholdSpells.ForEach(s => s.Update(lastTick));
            if (status == SpellStatus.Waiting && HasThresholdToCast)
            {
                // Clean up any finished Child Spells because this Spell cannot finish until it has.
                foreach (Spell thresholdSpell in thresholdSpells.ToList())
                    if (thresholdSpell.IsFinished)
                        thresholdSpells.Remove(thresholdSpell);
            }

            if ((status == SpellStatus.Executing && !events.HasPendingEvent && !parameters.ForceCancelOnly) ||
                (status == SpellStatus.Waiting && !HasThresholdToCast) ||
                status == SpellStatus.Finishing)
            {
                status = SpellStatus.Finished;
                // Inform Clients that this Spell has finished casting.
                SendSpellFinish();
                log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has finished.");

                // Clear any Threshold information sent to the caster.
                if (caster is Player player)
                    if (thresholdMax > 0)
                    {
                        player.Session.EnqueueMessageEncrypted(new ServerSpellThresholdClear
                        {
                            Spell4Id = Spell4Id
                        });

                        if (CastMethod != CastMethod.ChargeRelease)
                            SetCooldown();
                    }
            }
        }

        /// <summary>
        /// Begin cast, checking prerequisites before initiating.
        /// </summary>
        public void Cast()
        {
            /** Existing Spell **/
            if (status == SpellStatus.Waiting)
            {
                HandleThresholdCast();
                return;
            }

            /** New Spell **/
            if (status != SpellStatus.Initiating)
                throw new InvalidOperationException();

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started initating.");

            CastResult result = CheckCast();
            if (result != CastResult.Ok)
            {
                // Swallow Proxy CastResults
                if (parameters.IsProxy)
                    return;

                if (caster is Player)
                    (caster as Player).SpellManager.SetAsContinuousCast(null);

                SendSpellCastResult(result);
                status = SpellStatus.Failed;
                return;
            }

            // Global Cooldowns appear to have a "Type" that the GCD is stored against. Each spell will confirm that the specific GCD "Type" is not on CD.
            if (caster is Player player)
            {
                if (parameters.SpellInfo.GlobalCooldown != null && !parameters.IsProxy)
                    player.SpellManager.SetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum, parameters.SpellInfo.GlobalCooldown.CooldownTime / 1000d);
                else if (parameters.IsProxy)
                    player.SpellManager.SetSpellCooldown(parameters.SpellInfo, parameters.CooldownOverride / 1000d);
            }

            // It's assumed that non-player entities will be stood still to cast (most do). 
            // TODO: There are a handful of telegraphs that are attached to moving units (specifically rotating units) which this needs to be updated to account for.
            if (!(caster is Player))
                InitialiseTelegraphs();

            SendSpellStart();
            InitialiseCastMethod();

            status = SpellStatus.Casting;
            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started casting.");
        }

        private CastResult CheckCast()
        {
            CastResult preReqResult = CheckPrerequisites();
            if (preReqResult != CastResult.Ok)
                return preReqResult;

            CastResult ccResult = CheckCCConditions();
            if (ccResult != CastResult.Ok)
                return ccResult;

            if (caster is Player player)
            {
                if (IsCasting && parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                    return CastResult.SpellAlreadyCasting;

                // TODO: Some spells can be cast during other spell casts. Reflect that in this check
                if (caster.IsCasting() && parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                    return CastResult.SpellAlreadyCasting;

                if (player.SpellManager.GetSpellCooldown(parameters.SpellInfo.Entry.Id) > 0d &&
                    parameters.UserInitiatedSpellCast &&
                    !parameters.IsProxy)
                    return CastResult.SpellCooldown;

                foreach (SpellCoolDownEntry coolDownEntry in parameters.SpellInfo.Cooldowns)
                {
                    if (player.SpellManager.GetSpellCooldownByCooldownId(coolDownEntry.Id) > 0d &&
                        parameters.UserInitiatedSpellCast &&
                        !parameters.IsProxy)
                        return CastResult.SpellCooldown;
                }

                if (player.SpellManager.GetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum) > 0d && 
                    !parameters.IsProxy && 
                    parameters.UserInitiatedSpellCast)
                    return CastResult.SpellGlobalCooldown;

                if (parameters.CharacterSpell?.MaxAbilityCharges > 0 && parameters.CharacterSpell?.AbilityCharges == 0)
                    return CastResult.SpellNoCharges;

                CastResult resourceConditions = CheckResourceConditions();
                if (resourceConditions != CastResult.Ok)
                {
                    if (parameters.UserInitiatedSpellCast && !parameters.IsProxy)
                        player.SpellManager.SetAsContinuousCast(null);

                    return resourceConditions;
                }
            }

            return CastResult.Ok;
        }

        private CastResult CheckPrerequisites()
        {
            // TODO: Remove below line and evaluate PreReq's for Non-Player Entities
            if (!(caster is Player player))
                return CastResult.Ok;

            // Runners override the Caster Check, allowing the Caster to Cast the spell due to this Prerequisite being met
            if (parameters.SpellInfo.CasterCastPrerequisite != null && !CheckRunnerOverride(player))
            {
                if (!PrerequisiteManager.Instance.Meets(player, parameters.SpellInfo.CasterCastPrerequisite.Id))
                    return CastResult.PrereqCasterCast;
            }

            // not sure if this should be for explicit and/or implicit targets
            if (parameters.SpellInfo.TargetCastPrerequisites != null)
            {
            }

            // this probably isn't the correct place, name implies this should be constantly checked
            if (parameters.SpellInfo.CasterPersistencePrerequisites != null)
            {
            }

            if (parameters.SpellInfo.TargetPersistencePrerequisites != null)
            {
            }

            return CastResult.Ok;
        }

        /// <summary>
        /// Returns whether the Caster is in a state where they can ignore Resource or other constraints.
        /// </summary>
        private bool CheckRunnerOverride(Player player)
        {
            foreach (PrerequisiteEntry runnerPrereq in parameters.SpellInfo.PrerequisiteRunners)
                if (PrerequisiteManager.Instance.Meets(player, runnerPrereq.Id))
                    return true;

            return false;
        }

        private CastResult CheckCCConditions()
        {
            // TODO: this just looks like a mask for CCState enum
            if (parameters.SpellInfo.CasterCCConditions != null)
            {
            }

            // not sure if this should be for explicit and/or implicit targets
            if (parameters.SpellInfo.TargetCCConditions != null)
            {
            }

            return CastResult.Ok;
        }

        private CastResult CheckResourceConditions()
        {
            if (!(caster is Player player))
                return CastResult.Ok;

            bool runnerOveride = CheckRunnerOverride(player);
            if (runnerOveride)
                return CastResult.Ok;

            //for (int i = 0; i < parameters.SpellInfo.Entry.CasterInnateRequirements.Length; i++)
            //{
            //    uint innateRequirement = parameters.SpellInfo.Entry.CasterInnateRequirements[i];
            //    if (innateRequirement == 0)
            //        continue;

            //    switch (parameters.SpellInfo.Entry.CasterInnateRequirementEval[i])
            //    {
            //        case 2:
            //            if (caster.GetVitalValue((Vital)innateRequirement) < parameters.SpellInfo.Entry.CasterInnateRequirementValues[i])
            //                return GlobalSpellManager.Instance.GetFailedCastResultForVital((Vital)innateRequirement);
            //            break;
            //    }
            //}

            //for (int i = 0; i < parameters.SpellInfo.Entry.InnateCostTypes.Length; i++)
            //{
            //    uint innateCostType = parameters.SpellInfo.Entry.InnateCostTypes[i];
            //    if (innateCostType == 0)
            //        continue;

            //    if (caster.GetVitalValue((Vital)innateCostType) < parameters.SpellInfo.Entry.InnateCosts[i])
            //        return GlobalSpellManager.Instance.GetFailedCastResultForVital((Vital)innateCostType);
            //}

            return CastResult.Ok;
        }

        /// <summary>
        /// Initialises a <see cref="Telegraph"/> per <see cref="TelegraphDamageEntry"/> as associated with this <see cref="Spell"/>.
        /// </summary>
        private void InitialiseTelegraphs()
        {
            telegraphs.Clear();

            foreach (TelegraphDamageEntry telegraphDamageEntry in parameters.SpellInfo.Telegraphs)
                telegraphs.Add(new Telegraph(telegraphDamageEntry, caster, caster.Position, caster.Rotation));
        }

        /// <summary>
        /// Invoke the method that handles the <see cref="CastMethod"/> for this <see cref="Spell"/>.
        /// </summary>
        private void InitialiseCastMethod()
        {
            CastMethodDelegate handler = GlobalSpellManager.Instance.GetCastMethodHandler(CastMethod);
            if (handler == null)
            {
                log.Warn($"Unhandled cast method {CastMethod}. Using {CastMethod.Normal} instead.");
                GlobalSpellManager.Instance.GetCastMethodHandler(CastMethod.Normal).Invoke(this);
            }
            else
                handler.Invoke(this);
        }

        /// <summary>
        /// Get an initialised <see cref="Spell"/>, as part of a Threshold chain, to be cast as a Child (to this Spell instance) based on the current phase.
        /// </summary>
        private Spell InitialiseThresholdSpell()
        {
            if (parameters.SpellInfo.Thresholds.Count == 0)
                return null;

            (SpellInfo spellInfo, Spell4ThresholdsEntry thresholdsEntry) = parameters.SpellInfo.GetThresholdSpellInfo((int)thresholdValue);
            if (spellInfo == null || thresholdsEntry == null)
                throw new InvalidOperationException($"{spellInfo} or {thresholdsEntry} is null!");

            Spell thresholdSpell = new Spell(caster, new SpellParameters
            {
                SpellInfo = spellInfo,
                ParentSpellInfo = parameters.SpellInfo,
                RootSpellInfo = parameters.SpellInfo,
                UserInitiatedSpellCast = parameters.UserInitiatedSpellCast,
                ThresholdValue = thresholdsEntry.OrderIndex + 1,
                IsProxy = CastMethod == CastMethod.ChargeRelease
            });

            log.Trace($"Added Child Spell {thresholdSpell.Spell4Id} with casting ID {thresholdSpell.CastingId} to parent casting ID {CastingId}");

            return thresholdSpell;
        }

        /// <summary>
        /// This is called when Spell is of a type that has Multi-Tap capabilities.
        /// </summary>
        /// <remarks>This grabs and executes the next spell in a chain to cast or the appropriate charge+hold spell.</remarks>
        private void HandleThresholdCast()
        {
            if (status != SpellStatus.Waiting)
                throw new InvalidOperationException();

            if (parameters.SpellInfo.Thresholds.Count == 0)
                throw new InvalidOperationException();

            CastResult result = CheckCast();
            if (result != CastResult.Ok)
            {
                if (CastMethod == CastMethod.RapidTap && result != CastResult.PrereqCasterCast)
                {
                    if (caster is Player player)
                        player.SpellManager.SetAsContinuousCast(null);

                    SendSpellCastResult(result);
                    return;
                }
            }

            Spell thresholdSpell = InitialiseThresholdSpell();
            thresholdSpell.Cast();
            thresholdSpells.Add(thresholdSpell);

            switch (CastMethod)
            {
                case CastMethod.ChargeRelease:
                    SetCooldown();
                    thresholdValue = thresholdMax;
                    break;
                case CastMethod.RapidTap:
                    thresholdValue++;
                    break;
            }
        }

        /// <summary>
        /// Cancel cast with supplied <see cref="CastResult"/>.
        /// </summary>
        public void CancelCast(CastResult result)
        {
            if (!IsCasting && !HasThresholdToCast)
                return;

            if (HasThresholdToCast && thresholdSpells.Count > 0)
                if (thresholdSpells[0].IsCasting)
                {
                    thresholdSpells[0].CancelCast(result);
                    return;
                }

            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new Server07F9
                {
                    ServerUniqueId = CastingId,
                    CastResult = result,
                    CancelCast = true
                });

                if (result == CastResult.CasterMovement)
                    player.SpellManager.SetGlobalSpellCooldown(parameters.SpellInfo.Entry.GlobalCooldownEnum, 0d);

                player.SpellManager.SetAsContinuousCast(null);

                SendSpellCastResult(result);
            }

            events.CancelEvents();
            status = SpellStatus.Finishing;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} cast was cancelled.");
        }

        private void Execute()
        {
            status = SpellStatus.Executing;
            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started executing.");

            // Ensure that at the first point of an Execute pass, we charge the spell
            // This excludes Charge+Hold Spells, which are cost when the button lets go and is handled by Update().
            if ((currentPhase == 0 || currentPhase == 255) && !HasThresholdToCast && CastMethod != CastMethod.ChargeRelease)
            {
                // Deduct charges and any resources from the caster
                CostSpell();
                // Apply Cooldown for this spell. This is separate from Global CD.
                SetCooldown();
            }

            // Clear Effects so that we don't duplicate Effect information back to the client.
            targets.ForEach(t => t.Effects.Clear());

            // Order below must not change.
            SelectTargets();  // First Select Targets
            ExecuteEffects(); // All Effects are evaluated and executed (after SelectTargets())
            HandleProxies();  // Any Proxies that are added by Effets are evaluated and executed (after ExecuteEffects())
            SendSpellGo();    // Inform the Client once all evaluations are taken place (after Effects & Proxies are executed)

            // TODO: Confirm whether RapidTap spells cancel another out, and add logic as necessary

            if (parameters.SpellInfo.Entry.ThresholdTime > 0)
                SendThresholdStart();

            if (parameters.ThresholdValue > 0 && parameters.RootSpellInfo.Thresholds.Count > 1)
                SendThresholdUpdate();

        }

        private void HandleProxies()
        {
            foreach (Proxy proxy in proxies)
                proxy.Evaluate();

            foreach (Proxy proxy in proxies)
                proxy.Cast(caster, events);

            proxies.Clear();
        }

        private void SetCooldown()
        {
            if (!(caster is Player player))
                return;

            if (parameters.SpellInfo.Entry.SpellCoolDown != 0u)
                player.SpellManager.SetSpellCooldown(parameters.SpellInfo, parameters.SpellInfo.Entry.SpellCoolDown / 1000d);
        }

        private void CostSpell()
        {
            if (parameters.CharacterSpell?.MaxAbilityCharges > 0)
                parameters.CharacterSpell.UseCharge();

            // TODO: Handle costing Vital Resources
        }

        List<uint> uniqueTargets = new();
        private void SelectTargets()
        {
            // We clear targets every time this is called for this spell so we don't have duplicate targets.
            targets.Clear();

            // Add Caster Entity with the appropriate SpellEffectTargetFlags.
            targets.Add(new SpellTargetInfo(SpellEffectTargetFlags.Caster, caster));

            // Add Targeted Entity with the appropriate SpellEffectTargetFlags.
            if (parameters.PrimaryTargetId > 0)
            {
                UnitEntity primaryTargetEntity = caster.GetVisible<UnitEntity>(parameters.PrimaryTargetId);
                if (primaryTargetEntity != null)
                    targets.Add(new SpellTargetInfo((SpellEffectTargetFlags.Target), primaryTargetEntity));
            }

            // Targeting First Pass: Do Basic Checks to get targets for spell as needed, nearby.
            targets.AddRange(new AoeSelection(caster, parameters));

            // Re-initiailise Telegraphs at Execute time, so that position and rotation is calculated appropriately.
            // This is optimised to only happen on player-cast spells.
            // TODO: Add support for this for Server Controlled entities. It is presumed most will stand still when casting.
            if (caster is Player)
                InitialiseTelegraphs();

            if (telegraphs.Count > 0)
            {

                List<SpellTargetInfo> allowedTargets = new();
                foreach (Telegraph telegraph in telegraphs)
                {
                    List<uint> targetGuids = new();

                    // Ensure only telegraphs that apply to this Execute phase are evaluated.
                    if (CastMethod == CastMethod.Multiphase && currentPhase < 255)
                    {
                        int phaseMask = 1 << currentPhase;
                        if (telegraph.TelegraphDamage.PhaseFlags != 1 && (phaseMask & telegraph.TelegraphDamage.PhaseFlags) == 0)
                            continue;
                    }

                    log.Trace($"Getting targets for Telegraph ID {telegraph.TelegraphDamage.Id}");

                    foreach (var target in telegraph.GetTargets(this, targets))
                    {
                        // Ensure that this Telegraph hasn't already selected this entity
                        if (targetGuids.Contains(target.Entity.Guid))
                            continue;

                        // Ensure that this telegraph doesn't select an entity that has already been slected by another telegraph, as the targeting flags dictate.
                        if ((parameters.SpellInfo.BaseInfo.Entry.TargetingFlags & 32) != 0 &&
                            uniqueTargets.Contains(target.Entity.Guid))
                            continue;

                        allowedTargets.Add(target);
                        targetGuids.Add(target.Entity.Guid);
                        uniqueTargets.Add(target.Entity.Guid);
                    }

                    log.Trace($"Got {targets.Count} for Telegraph ID {telegraph.TelegraphDamage.Id}");
                }
                targets.RemoveAll(x => x.Flags == SpellEffectTargetFlags.Telegraph); // Only remove targets that are ONLY Telegraph Targeted
                targets.AddRange(allowedTargets);
            }

            if (parameters.SpellInfo.AoeTargetConstraints != null)
            {
                List<SpellTargetInfo> finalAoeTargets = new();
                foreach (var target in targets)
                {
                    // Ensure that we're not exceeding the amount of targets we can select
                    if (parameters.SpellInfo.AoeTargetConstraints.TargetCount > 0 &&
                        finalAoeTargets.Count > parameters.SpellInfo.AoeTargetConstraints.TargetCount)
                        break;

                    if ((target.Flags & SpellEffectTargetFlags.Telegraph) == 0)
                        continue;

                    finalAoeTargets.Add(target);
                }

                // Finalise targets for effect execution
                targets.RemoveAll(x => x.Flags == SpellEffectTargetFlags.Telegraph); // Only remove targets that are ONLY Telegraph Targeted
                targets.AddRange(finalAoeTargets);
            }
        }

        private void ExecuteEffects()
        {
            for (int index = 0; index < parameters.SpellInfo.Effects.Count(); index++)
            {
                Spell4EffectsEntry spell4EffectsEntry = parameters.SpellInfo.Effects[index];

                if (caster is Player player)
                {
                    // Ensure caster can apply this effect
                    if (spell4EffectsEntry.PrerequisiteIdCasterApply > 0 && !PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdCasterApply))
                        continue;
                }

                // Ensure that Effects fire in this spell phase.
                if (CastMethod == CastMethod.Multiphase && currentPhase < 255)
                {
                    int phaseMask = 1 << currentPhase;
                    if ((spell4EffectsEntry.PhaseFlags != 1 && spell4EffectsEntry.PhaseFlags != uint.MaxValue) && (phaseMask & spell4EffectsEntry.PhaseFlags) == 0)
                        continue;
                }

                log.Trace($"Executing SpellEffect ID {spell4EffectsEntry.Id} ({1 << currentPhase})");

                // select targets for effect
                List<SpellTargetInfo> effectTargets = targets
                    .Where(t => (t.Flags & (SpellEffectTargetFlags)spell4EffectsEntry.TargetFlags) != 0)
                    .ToList();

                SpellEffectDelegate handler = GlobalSpellManager.Instance.GetEffectHandler((SpellEffectType)spell4EffectsEntry.EffectType);
                if (handler == null)
                    log.Warn($"Unhandled spell effect {(SpellEffectType)spell4EffectsEntry.EffectType}");
                else
                {
                    uint effectId = GlobalSpellManager.Instance.NextEffectId;
                    foreach (SpellTargetInfo effectTarget in effectTargets)
                    {
                        if (!CheckEffectApplyPrerequisites(spell4EffectsEntry, effectTarget.Entity, effectTarget.Flags))
                            continue;

                        var info = new SpellTargetInfo.SpellTargetEffectInfo(effectId, spell4EffectsEntry);
                        effectTarget.Effects.Add(info);

                        // TODO: if there is an unhandled exception in the handler, there will be an infinite loop on Execute()
                        handler.Invoke(this, effectTarget.Entity, info);

                        // Track the number of times this effect has fired.
                        // Some spell effects have a limited trigger count per spell cast.
                        if (effectTriggerCount.TryGetValue(spell4EffectsEntry.Id, out uint count))
                            effectTriggerCount[spell4EffectsEntry.Id]++;
                        else
                            effectTriggerCount.TryAdd(spell4EffectsEntry.Id, 1);
                    }

                    // Add durations for each effect so that when the Effect timer runs out, the Spell can Finish.
                    if (spell4EffectsEntry.DurationTime > 0)
                        events.EnqueueEvent(new SpellEvent(spell4EffectsEntry.DurationTime / 1000d, () => { /* placeholder for duration */ }));

                    // This sets the maximum duration for this Spell based on it's longest lasting effect.
                    if (spell4EffectsEntry.DurationTime > 0 && spell4EffectsEntry.DurationTime > duration)
                        duration = spell4EffectsEntry.DurationTime;

                    // This makes this spell Infinite Duration. Spells only needed 1 Effect with this Flag to force it to Infinite Duration.
                    if (spell4EffectsEntry.DurationTime == 0u && ((SpellEffectFlags)spell4EffectsEntry.Flags & SpellEffectFlags.CancelOnly) != 0)
                        parameters.ForceCancelOnly = true;
                }
            }
        }

        private bool CheckEffectApplyPrerequisites(Spell4EffectsEntry spell4EffectsEntry, UnitEntity unit, SpellEffectTargetFlags targetFlags)
        {
            bool effectCanApply = true;

            // TODO: Possibly update Prereq Manager to handle other Units
            if (unit is not Player player)
                return true;

            if ((targetFlags & SpellEffectTargetFlags.Caster) != 0)
            {
                // TODO
                if (spell4EffectsEntry.PrerequisiteIdCasterApply > 0)
                {
                    effectCanApply = PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdCasterApply);
                }
            }

            if (effectCanApply && (targetFlags & SpellEffectTargetFlags.Caster) == 0)
            {
                if (spell4EffectsEntry.PrerequisiteIdTargetApply > 0)
                {
                    effectCanApply = PrerequisiteManager.Instance.Meets(player, spell4EffectsEntry.PrerequisiteIdTargetApply);
                }
            }

            return effectCanApply;
        }

        public bool IsMovingInterrupted()
        {
            // TODO: implement correctly
            return parameters.UserInitiatedSpellCast && parameters.SpellInfo.BaseInfo.SpellType.Id != 5 && parameters.SpellInfo.Entry.CastTime > 0;
        }

        /// <summary>
        /// Finish this <see cref="Spell"/> and end all effects associated with it.
        /// </summary>
        public void Finish()
        {
            if (status == SpellStatus.Finished)
                return;

            thresholdValue = thresholdMax;
            events.CancelEvents();
            status = SpellStatus.Finishing;
        }

        private bool _IsCasting()
        {
            if (parameters.IsProxy)
                return false;

            if (!(caster is Player) && status == SpellStatus.Initiating)
                return true;

            bool PassEntityChecks()
            {
                if (caster is Player)
                    return parameters.UserInitiatedSpellCast;

                return true;
            }

            switch (CastMethod)
            {
                case CastMethod.ChargeRelease:
                    return PassEntityChecks() && (status == SpellStatus.Casting || status == SpellStatus.Executing || status == SpellStatus.Waiting);
                case CastMethod.Channeled:
                case CastMethod.ChanneledField:
                case CastMethod.Multiphase:
                    return PassEntityChecks() && (status == SpellStatus.Casting || status == SpellStatus.Executing);
                case CastMethod.Normal:
                default:
                    return PassEntityChecks() && status == SpellStatus.Casting;
            }
        }

        private void SendSpellCastResult(CastResult castResult)
        {
            if (castResult == CastResult.Ok)
                return;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} failed to cast {castResult}.");

            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerSpellCastResult
                {
                    Spell4Id = parameters.SpellInfo.Entry.Id,
                    CastResult = castResult
                });
            }
        }

        private void SendSpellStart()
        {
            ServerSpellStart spellStart = new ServerSpellStart
            {
                CastingId = CastingId,
                CasterId = caster.Guid,
                PrimaryTargetId = parameters.PrimaryTargetId > 0 ? parameters.PrimaryTargetId : caster.Guid,
                Spell4Id = parameters.SpellInfo.Entry.Id,
                RootSpell4Id = parameters.RootSpellInfo?.Entry.Id ?? 0,
                ParentSpell4Id = parameters.ParentSpellInfo?.Entry.Id ?? 0,
                FieldPosition = new Position(caster.Position),
                Yaw = caster.Rotation.X,
                UserInitiatedSpellCast = parameters.UserInitiatedSpellCast,
                InitialPositionData = new List<InitialPosition>(),
                TelegraphPositionData = new List<TelegraphPosition>()
            };

            // TODO: Add Proxy Units
            List<UnitEntity> unitsCasting = new List<UnitEntity>();
            unitsCasting.Add(caster);

            foreach (UnitEntity unit in unitsCasting)
            {
                if (unit == null)
                    continue;

                if (unit is Player)
                    continue;

                spellStart.InitialPositionData.Add(new InitialPosition
                {
                    UnitId = unit.Guid,
                    Position = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw = unit.Rotation.X
                });
            }

            foreach (UnitEntity unit in unitsCasting)
            {
                if (unit == null)
                    continue;

                foreach (Telegraph telegraph in telegraphs)
                    spellStart.TelegraphPositionData.Add(new TelegraphPosition
                    {
                        TelegraphId = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags = 3,
                        Position = new Position(telegraph.Position),
                        Yaw = telegraph.Rotation.X
                    });
            }


            caster.EnqueueToVisible(spellStart, true);
        }

        private void SendSpellFinish()
        {
            if (status != SpellStatus.Finished)
                return;

            caster.EnqueueToVisible(new ServerSpellFinish
            {
                ServerUniqueId = CastingId,
            }, true);
        }

        private void SendSpellGo()
        {
            var serverSpellGo = new ServerSpellGo
            {
                ServerUniqueId = CastingId,
                PrimaryDestination = new Position(caster.Position),
                Phase = currentPhase
            };

            byte targetCount = 0;
            foreach (SpellTargetInfo targetInfo in targets
                .Where(t => t.Effects.Count > 0))
            {
                var networkTargetInfo = new TargetInfo
                {
                    UnitId = targetInfo.Entity.Guid,
                    Ndx = targetCount++,
                    TargetFlags = (byte)targetInfo.Flags,
                    InstanceCount = 1,
                    CombatResult = CombatResult.Hit
                };

                foreach (SpellTargetInfo.SpellTargetEffectInfo targetEffectInfo in targetInfo.Effects)
                {

                    if ((SpellEffectType)targetEffectInfo.Entry.EffectType == SpellEffectType.Proxy)
                        continue;

                    var networkTargetEffectInfo = new EffectInfo
                    {
                        Spell4EffectId = targetEffectInfo.Entry.Id,
                        EffectUniqueId = targetEffectInfo.EffectId,
                        DelayTime = targetEffectInfo.Entry.DelayTime,
                        TimeRemaining = duration > 0 ? (int)duration : -1
                    };

                    if (targetEffectInfo.Damage != null)
                    {
                        networkTargetEffectInfo.InfoType = 1;
                        networkTargetEffectInfo.DamageDescriptionData = new DamageDescription
                        {
                            RawDamage = targetEffectInfo.Damage.RawDamage,
                            RawScaledDamage = targetEffectInfo.Damage.RawScaledDamage,
                            AbsorbedAmount = targetEffectInfo.Damage.AbsorbedAmount,
                            ShieldAbsorbAmount = targetEffectInfo.Damage.ShieldAbsorbAmount,
                            AdjustedDamage = targetEffectInfo.Damage.AdjustedDamage,
                            OverkillAmount = targetEffectInfo.Damage.OverkillAmount,
                            KilledTarget = targetEffectInfo.Damage.KilledTarget,
                            CombatResult = targetEffectInfo.Damage.CombatResult,
                            DamageType = targetEffectInfo.Damage.DamageType
                        };
                    }

                    networkTargetInfo.EffectInfoData.Add(networkTargetEffectInfo);
                }

                serverSpellGo.TargetInfoData.Add(networkTargetInfo);
            }

            List<UnitEntity> unitsCasting = new List<UnitEntity>
                {
                    caster
                };

            foreach (UnitEntity unit in unitsCasting)
                serverSpellGo.InitialPositionData.Add(new Network.Message.Model.Shared.InitialPosition
                {
                    UnitId = unit.Guid,
                    Position = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw = unit.Rotation.X
                });

            foreach (UnitEntity unit in unitsCasting)
                foreach (Telegraph telegraph in telegraphs)
                    serverSpellGo.TelegraphPositionData.Add(new TelegraphPosition
                    {
                        TelegraphId = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags = 3,
                        Position = new Position(telegraph.Position),
                        Yaw = telegraph.Rotation.X
                    });

            caster.EnqueueToVisible(serverSpellGo, true);
        }

        private void SendThresholdStart()
        {
            if (caster is Player player)
                player.Session.EnqueueMessageEncrypted(new ServerSpellThresholdStart
                {
                    Spell4Id = parameters.SpellInfo.Entry.Id,
                    RootSpell4Id = parameters.RootSpellInfo?.Entry.Id ?? 0,
                    ParentSpell4Id = parameters.ParentSpellInfo?.Entry.Id ?? 0,
                    CastingId = CastingId
                });
        }

        private void SendThresholdUpdate()
        {
            if (caster is Player player)
                player.Session.EnqueueMessageEncrypted(new ServerSpellThresholdUpdate
                {
                    Spell4Id = parameters.ParentSpellInfo?.Entry.Id ?? Spell4Id,
                    Value = parameters.ThresholdValue > 0 ? (byte)parameters.ThresholdValue : (byte)thresholdValue
                });
        }

        private void SendRemoveBuff(uint unitId)
        {
            if (!parameters.SpellInfo.BaseInfo.HasIcon)
                throw new InvalidOperationException();

            caster.EnqueueToVisible(new ServerSpellBuffRemove
            {
                CastingId = CastingId,
                CasterId = unitId
            }, true);
        }

        private void SendBuffRemoved()
        {
            if (targets.Count == 0)
                return;

            ServerSpellBuffRemoveMulti spellTargets = new ServerSpellBuffRemoveMulti
            {
                CastingId = CastingId,
                SpellTargets = targets.Select(i => i.Entity.Guid).ToList()
            };

            caster.EnqueueToVisible(spellTargets, true);
        }

        private void CheckPersistance(double lastTick)
        {
            if (caster is not Player player)
                return;

            if (parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence == 0 && parameters.SpellInfo.Entry.PrerequisiteIdTargetPersistence == 0)
                return;

            persistCheck.Update(lastTick);
            if (persistCheck.HasElapsed)
            {
                if (parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence > 0 && !PrerequisiteManager.Instance.Meets(player, parameters.SpellInfo.Entry.PrerequisiteIdCasterPersistence))
                    Finish();
                
                // TODO: Check if target can still persist

                persistCheck.Reset();
            }
        }
    }
}