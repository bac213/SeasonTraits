using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.Commerce;

using SPTarkov.Server.Core.Models.Eft.Common;

namespace SeasonTraits.Server;

/// <summary>
/// Applies selected trait effects to a profile's PmcData.
/// </summary>
[Injectable]
public class TraitApplier(
    ISptLogger<TraitApplier> logger,
    TraitDataService traitData,
    InventoryHelper inventoryHelper,
    MailSendService mailSendService)
{
    /// <summary>Skill ids excluded from the "average"/"incompetent" skill caps AND from the
    /// wildcard "*" skill-start level. Crafting only: the catalog says 制造除外, so a handyman
    /// Crafting-51 start must survive an average pick instead of being reset to 25.</summary>
    private static readonly HashSet<string> CapExemptSkills = new() { "Crafting" };

    public void ApplySelection(MongoId sessionId, PmcData pmcData, ProfileTraitState state)
    {
        MigrateLegacySkillBoosts(pmcData);

        // Deterministic stacking (audit M-1): fixed-point skill starts first, the wildcard
        // "*" start last, everything else in its original order — overlapping picks
        // (average + handyman/youth) then resolve identically regardless of submission order.
        foreach (var traitId in state.Selected)
        {
            var trait = traitData.GetTrait(traitId);
            if (trait == null)
            {
                continue; // missing-trait warning is logged once in pass 3
            }
            foreach (var effect in trait.Effects)
            {
                if (effect.Type == "skillStartLevel" && effect.SkillId != "*")
                {
                    ApplyEffect(sessionId, pmcData, trait, effect);
                }
            }
        }
        foreach (var traitId in state.Selected)
        {
            var trait = traitData.GetTrait(traitId);
            if (trait == null)
            {
                continue;
            }
            foreach (var effect in trait.Effects)
            {
                if (effect.Type == "skillStartLevel" && effect.SkillId == "*")
                {
                    ApplyEffect(sessionId, pmcData, trait, effect);
                }
            }
        }
        foreach (var traitId in state.Selected)
        {
            var trait = traitData.GetTrait(traitId);
            if (trait == null)
            {
                logger.Warning($"SeasonTraits: trait {traitId} not found in db, skipped for {sessionId}");
                continue;
            }
            foreach (var effect in trait.Effects)
            {
                if (effect.Type != "skillStartLevel")
                {
                    ApplyEffect(sessionId, pmcData, trait, effect);
                }
            }
        }

        // capture charisma progress for the lock when personality_vacuum present
        if (state.Selected.Contains("personality_vacuum"))
        {
            var charisma = FindSkill(pmcData, "Charisma");
            if (charisma != null)
            {
                state.CharismaProgress = charisma.Progress;
            }
        }

        // caps also clamp any start levels granted in the same selection (e.g. average +25)
        ClampSkillCaps(pmcData, state);
    }

    private void ApplyEffect(MongoId sessionId, PmcData pmcData, TraitDefinition trait, TraitEffect effect)
    {
        switch (effect.Type)
        {
            case "skillStartLevel":
            {
                if (effect.SkillId == null || effect.Value == null)
                {
                    break;
                }
                var progress = Math.Min(effect.Value.Value * 100.0, CommonSkill.MaxSkillProgress);
                if (effect.SkillId == "*")
                {
                    // e.g. Average: every skill starts at the given level; Crafting exempt
                    // (catalog "制造除外") so a handyman Crafting 51 start is never lowered.
                    foreach (var entry in pmcData.Skills?.Common ?? new List<CommonSkill>())
                    {
                        if (CapExemptSkills.Contains(entry.Id.ToString()))
                        {
                            continue;
                        }
                        entry.Progress = progress;
                    }
                    break;
                }
                var skill = FindSkill(pmcData, effect.SkillId);
                if (skill == null)
                {
                    logger.Warning($"SeasonTraits: skill {effect.SkillId} not found in profile, skipped");
                    break;
                }
                // EFT skill progress: 100 points per level, MaxSkillProgress = 5100 (51 levels)
                skill.Progress = progress;
                break;
            }
            case "bonus":
            {
                if (effect.BonusType == null || !Enum.TryParse<BonusType>(effect.BonusType, out var bonusType))
                {
                    logger.Warning($"SeasonTraits: unknown bonus type {effect.BonusType}, skipped");
                    break;
                }
                // SkillLevelingBoost is consumed by the client per skill id (`skillName`), a field the
                // SPT Bonus model cannot express — emitting it makes Profile ctor throw
                // "Nullable object must have a value" on load. The group variant is the correct model
                // for "all skills +X%": one bonus per ESkillClass group.
                if (bonusType == BonusType.SkillLevelingBoost)
                {
                    bonusType = BonusType.SkillGroupLevelingBoost;
                    foreach (var group in Enum.GetValues<BonusSkillType>())
                    {
                        AddBonus(pmcData, trait.Id, bonusType, effect.Value ?? 0, group);
                    }
                }
                else
                {
                    AddBonus(pmcData, trait.Id, bonusType, effect.Value ?? 0, null);
                }
                break;
            }
            case "grantItem":
            {
                if (effect.ItemId == null)
                {
                    break;
                }
                // Deliver via mail so the item lands in the client's mail box without needing a
                // game restart (a direct stash write is only visible after profile reload).
                SendItemViaMail(sessionId, effect.ItemId, (int)(effect.Amount ?? 1));
                break;
            }
            case "fleaBlock":
            {
                // official semantics (TraderHelper:204): Ban.DateTime is a UNIX-SECONDS timestamp.
                // The game computes ProfileBan.TimeLeft via TimeSpan.FromSeconds of the remainder —
                // long.MaxValue (DateTime ticks) overflows and crashes profile load.
                // ~19 years ahead; the game treats it as effectively permanent.
                var banUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 6995L * 86400;
                var bans = pmcData.Info?.Bans?.ToList() ?? new List<Ban>();
                var existing = bans.FirstOrDefault(b => b.BanType == BanType.RagFair);
                if (existing != null)
                {
                    existing.DateTime = banUntil; // upsert, never stack
                }
                else
                {
                    bans.Add(new Ban
                    {
                        BanType = BanType.RagFair,
                        DateTime = banUntil,
                    });
                }
                if (pmcData.Info != null)
                {
                    pmcData.Info.Bans = bans;
                }
                break;
            }
            case "skillXpMultiplier":
            case "skillCap":
            case "traderPrice":
            case "income":
            case "flavor":
                // handled elsewhere: client-side bonus / harmony patches / IOnUpdate / nothing
                break;
            default:
                logger.Warning($"SeasonTraits: unknown effect type {effect.Type}, skipped");
                break;
        }
    }

    /// <summary>
    /// Repairs profiles written by older builds: those emitted <c>SkillLevelingBoost</c> bonuses with
    /// only <c>skillType</c>, which crashes the client in <c>SkillLevelingBoostBonus..ctor</c>
    /// ("Nullable object must have a value") while the profile loads. Trait-owned entries are rewritten
    /// to <c>SkillGroupLevelingBoost</c> (same +X% effect, correct descriptor); anything not owned by a
    /// trait is left untouched.
    /// </summary>
    private void MigrateLegacySkillBoosts(PmcData pmcData)
    {
        if (pmcData.Bonuses == null || pmcData.Bonuses.Count == 0)
        {
            return;
        }

        var migrated = 0;
        var kept = new List<Bonus>(pmcData.Bonuses.Count);
        foreach (var bonus in pmcData.Bonuses)
        {
            if (bonus == null)
            {
                continue;
            }

            if (bonus.Type != BonusType.SkillLevelingBoost)
            {
                kept.Add(bonus);
                continue;
            }

            if (bonus.ExtensionData == null || !bonus.ExtensionData.ContainsKey(TraitDataService.TraitMarkerKey))
            {
                kept.Add(bonus);
                continue;
            }

            migrated++;
            if (bonus.SkillType == null)
            {
                // Nothing sane to migrate to: drop it and let the apply pass re-add group variants.
                continue;
            }

            bonus.Type = BonusType.SkillGroupLevelingBoost;
            kept.Add(bonus);
        }

        if (migrated > 0)
        {
            pmcData.Bonuses = kept;
            logger.Info($"SeasonTraits: migrated {migrated} legacy SkillLevelingBoost bonus(es) to SkillGroupLevelingBoost");
        }
    }

    private void AddBonus(PmcData pmcData, string traitId, BonusType type, double value, BonusSkillType? group)
    {
        pmcData.Bonuses ??= new List<Bonus>();
        // idempotency guard: never stack the same trait twice
        if (pmcData.Bonuses.Any(b => TraitDataService.IsBonusForTrait(b, traitId) && b.Type == type
            && (group == null || b.SkillType == group)))
        {
            return;
        }
        pmcData.Bonuses.Add(new Bonus
        {
            Id = TraitDataService.MakeId(),
            Type = type,
            Value = value,
            IsPassive = true,
            IsProduction = false,
            IsVisible = true,
            SkillType = group,
            ExtensionData = new Dictionary<string?, object?> { [TraitDataService.TraitMarkerKey] = traitId },
        });
    }

    private void GrantItem(MongoId sessionId, PmcData pmcData, string templateId, int amount)
    {
        var item = new Item
        {
            Id = TraitDataService.MakeId(),
            Template = templateId,
            Upd = amount > 1 ? new Upd { StackObjectsCount = amount } : null,
        };
        var request = new AddItemDirectRequest
        {
            ItemWithModsToAdd = new List<Item> { item },
        };
        // AddItemToStash writes into output.ProfileChanges[sessionId].Items.NewItems — must be pre-populated
        var output = new ItemEventRouterResponse
        {
            ProfileChanges = new Dictionary<MongoId, ProfileChange>
            {
                [sessionId] = new ProfileChange { Items = new ItemChanges { NewItems = new List<Item>() } },
            },
        };
        inventoryHelper.AddItemToStash(sessionId, request, pmcData, output);
        if (output.Warnings is { Count: > 0 })
        {
            logger.Error($"SeasonTraits: failed to grant item {templateId} to {sessionId}: {output.Warnings[0].ErrorMessage}");
        }
        else
        {
            logger.Success($"SeasonTraits: granted {templateId} x{amount} to {sessionId}");
        }
    }

    /// <summary>
    /// Send a trait reward item as a system mail so the client receives it in-game (mail box)
    /// without a restart — unlike a direct stash write which is only visible after profile reload.
    /// </summary>
    private void SendItemViaMail(MongoId sessionId, string templateId, int amount)
    {
        var item = new Item
        {
            Id = TraitDataService.MakeId(),
            Template = templateId,
            Upd = amount > 1 ? new Upd { StackObjectsCount = amount } : null,
        };
        mailSendService.SendSystemMessageToPlayer(
            sessionId,
            "赛季特质奖励：已通过邮件发放（领取后进入仓库）",
            new List<Item> { item });
        logger.Success($"SeasonTraits: mailed {templateId} x{amount} to {sessionId}");
    }

    public void AddRoubles(MongoId sessionId, PmcData pmcData, double amount)
    {
        // prefer growing an existing small rouble stack to avoid wasting stash slots
        const string roubleTpl = "5449016a4bdc2d6f028b456f";
        const double maxStack = 500000;
        var stack = pmcData.Inventory?.Items?.FirstOrDefault(i =>
            i.Template == roubleTpl
            && (i.Upd?.StackObjectsCount ?? 1) + amount <= maxStack);
        if (stack != null)
        {
            stack.Upd ??= new Upd();
            stack.Upd.StackObjectsCount = (stack.Upd.StackObjectsCount ?? 1) + amount;
            return;
        }
        GrantItem(sessionId, pmcData, roubleTpl, (int)amount);
    }

    public CommonSkill? FindSkill(PmcData pmcData, string skillId)
        => pmcData.Skills?.Common?.FirstOrDefault(s => s.Id.ToString().Equals(skillId, StringComparison.OrdinalIgnoreCase));

    // ---- skill caps ----

    /// <summary>
    /// Clamp skill progress per the skillCap effects of the profile's selected traits
    /// (values read from db/traits.json — single source of truth): average caps everything
    /// at 25, incompetent at 30, except Crafting (CapExemptSkills). personality_vacuum's
    /// Charisma lock has no catalog progress semantics and stays a code rule.
    /// </summary>
    public void ClampSkillCaps(PmcData pmcData, ProfileTraitState state)
    {
        var common = pmcData.Skills?.Common;
        if (common == null)
        {
            return;
        }
        foreach (var skill in common)
        {
            var id = skill.Id.ToString();
            if (!CapExemptSkills.Contains(id))
            {
                foreach (var traitId in state.Selected)
                {
                    var trait = traitData.GetTrait(traitId);
                    if (trait == null)
                    {
                        continue;
                    }
                    foreach (var effect in trait.Effects)
                    {
                        if (effect.Type != "skillCap" || effect.Value == null)
                        {
                            continue;
                        }
                        if (effect.SkillId != null && effect.SkillId != "*"
                            && !id.Equals(effect.SkillId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        skill.Progress = Math.Min(skill.Progress, effect.Value.Value * 100.0);
                    }
                }
            }
            if (state.Selected.Contains("personality_vacuum")
                && id.Equals("Charisma", StringComparison.OrdinalIgnoreCase)
                && state.CharismaProgress != null)
            {
                skill.Progress = state.CharismaProgress.Value;
            }
        }
    }
}
