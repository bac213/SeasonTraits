using System.Reflection;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.InRaid;

namespace SeasonTraits.Server.Patches;

/// <summary>
/// Scales trader buy prices per profile by the traderPrice effects of the selected traits
/// (values read from db/traits.json — single source of truth, e.g. personality_vacuum +20%,
/// third_leg -5%). The catalog carries no trader scope, so third_leg stays Therapist-only
/// as a code rule. GetAssort returns a fresh clone per call, so mutating the result is safe.
/// </summary>
public class TraderPricePatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;

    private static readonly MongoId TherapistId = "54cb57776803fa99248b456e";

    protected override MethodBase GetTargetMethod()
    {
        return typeof(TraderAssortHelper).GetMethod(nameof(TraderAssortHelper.GetAssort))
            ?? throw new InvalidOperationException("SeasonTraits: TraderAssortHelper.GetAssort not found");
    }

    [PatchPostfix]
    public static void Postfix(TraderAssort __result, MongoId sessionId, MongoId traderId)
    {
        var state = TraitData.GetState(sessionId);
        if (state == null || __result?.BarterScheme == null)
        {
            return;
        }
        double multiplier = 1.0;
        foreach (var traitId in state.Selected)
        {
            var trait = TraitData.GetTrait(traitId);
            if (trait == null)
            {
                continue;
            }
            foreach (var effect in trait.Effects)
            {
                if (effect.Type != "traderPrice" || effect.Value == null)
                {
                    continue;
                }
                // Therapist scope is not expressible in the catalog; third_leg applies there only
                if (traitId == "third_leg" && traderId != TherapistId)
                {
                    continue;
                }
                multiplier *= (100.0 + effect.Value.Value) / 100.0;
            }
        }
        if (multiplier == 1.0)
        {
            return;
        }
        foreach (var schemeList in __result.BarterScheme.Values)
        {
            foreach (var scheme in schemeList)
            {
                foreach (var barter in scheme)
                {
                    if (barter.Count is > 0)
                    {
                        barter.Count = barter.Count * multiplier;
                    }
                }
            }
        }
    }
}

/// <summary>
/// Server-side skill gains for the tarkov_shooter trait: +100% XP on the Sniper (bolt-action) skill.
/// In-raid gains are client-computed and not affected (documented limitation).
/// </summary>
public class ShooterSkillXpPatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileHelper).GetMethods()
            .First(m => m.Name == nameof(ProfileHelper.AddSkillPointsToPlayer) && m.GetParameters().Length == 5)
            ?? throw new InvalidOperationException("SeasonTraits: ProfileHelper.AddSkillPointsToPlayer not found");
    }

    [PatchPrefix]
    public static void Prefix(PmcData pmcProfile, SkillTypes skill, ref double pointsToAddToSkill)
    {
        if (skill != SkillTypes.Sniper || pmcProfile?.SessionId == null)
        {
            return;
        }
        var state = TraitData.GetState(pmcProfile.SessionId);
        if (state != null && state.Selected.Contains("tarkov_shooter"))
        {
            pointsToAddToSkill *= 2.0;
        }
    }
}

/// <summary>
/// Clamps skill gains applied through the server-side funnel (quests, hideout, books, ...).
/// In-raid gains are synced wholesale from the client and handled by PostRaidSkillCapPatch.
/// </summary>
public class ServerSkillCapPatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;
    internal static TraitApplier TraitApplier = null!;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProfileHelper).GetMethods()
            .First(m => m.Name == nameof(ProfileHelper.AddSkillPointsToPlayer) && m.GetParameters().Length == 5)
            ?? throw new InvalidOperationException("SeasonTraits: ProfileHelper.AddSkillPointsToPlayer not found");
    }

    [PatchPostfix]
    public static void Postfix(PmcData pmcProfile)
    {
        if (pmcProfile?.SessionId == null)
        {
            return;
        }
        var state = TraitData.GetState(pmcProfile.SessionId);
        if (state == null)
        {
            return;
        }
        TraitApplier.ClampSkillCaps(pmcProfile, state);
    }
}

/// <summary>
/// Post-raid skills come wholesale from the client (LocationLifecycleService.HandlePostRaidPmc
/// does `pmcData.Skills = profile.Skills`), so caps must be re-applied after that assignment.
/// </summary>
public class PostRaidSkillCapPatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;
    internal static TraitApplier TraitApplier = null!;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod("HandlePostRaidPmc",
                   BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SeasonTraits: LocationLifecycleService.HandlePostRaidPmc not found");
    }

    [PatchPostfix]
    public static void Postfix(MongoId sessionId, SptProfile fullServerProfile)
    {
        var state = TraitData.GetState(sessionId);
        if (state == null)
        {
            return;
        }
        var pmcData = fullServerProfile?.CharacterData?.PmcData;
        if (pmcData != null)
        {
            TraitApplier.ClampSkillCaps(pmcData, state);
        }
    }
}

/// <summary>
/// armor_shortage (per-profile): removes armor and armored-equipment items from a trader's
/// assort for profiles that picked the trait. GetAssort returns a fresh clone per call, so
/// mutating the result is safe (same basis as TraderPricePatch). Fence is exempt, matching
/// the old global behavior.
/// </summary>
public class ArmorShortagePatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;
    internal static ItemHelper ItemHelper = null!;

    private static readonly MongoId ArmorBaseclass = "5448e54d4bdc2dcc718b4568"; // BaseClasses.ARMOR
    private static readonly MongoId ArmoredEquipmentBaseclass = "57bef4c42459772e8d35a53b"; // BaseClasses.ARMORED_EQUIPMENT
    private static readonly MongoId FenceId = "579dc571d53a0658a154fbec";

    protected override MethodBase GetTargetMethod()
    {
        return typeof(TraderAssortHelper).GetMethod(nameof(TraderAssortHelper.GetAssort))
            ?? throw new InvalidOperationException("SeasonTraits: TraderAssortHelper.GetAssort not found");
    }

    [PatchPostfix]
    public static void Postfix(TraderAssort __result, MongoId sessionId, MongoId traderId)
    {
        if (__result?.Items == null || traderId == FenceId)
        {
            return;
        }
        var state = TraitData.GetState(sessionId);
        if (state == null || !state.Selected.Contains("armor_shortage"))
        {
            return;
        }
        var armorItemIds = __result.Items
            .Where(i => ItemHelper.IsOfBaseclass(i.Template, ArmorBaseclass)
                        || ItemHelper.IsOfBaseclass(i.Template, ArmoredEquipmentBaseclass))
            .Select(i => i.Id)
            .ToHashSet();
        if (armorItemIds.Count == 0)
        {
            return;
        }
        __result.Items.RemoveAll(i => armorItemIds.Contains(i.Id));
        foreach (var armorId in armorItemIds)
        {
            __result.BarterScheme?.Remove(armorId);
            __result.LoyalLevelItems?.Remove(armorId);
        }
    }
}

/// <summary>
/// handyman (per-profile): halves hideout craft time for profiles that picked the trait by
/// post-processing the adjusted craft time. In-raid production countdowns restart on profile
/// change, so the halved value is what the client sees when it starts a craft.
/// </summary>
public class HandymanCraftTimePatch : AbstractPatch
{
    internal static TraitDataService TraitData = null!;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(HideoutHelper).GetMethod(nameof(HideoutHelper.GetAdjustedCraftTimeWithSkills))
            ?? throw new InvalidOperationException("SeasonTraits: HideoutHelper.GetAdjustedCraftTimeWithSkills not found");
    }

    [PatchPostfix]
    public static void Postfix(PmcData pmcData, ref double? __result)
    {
        if (__result == null || pmcData?.SessionId == null)
        {
            return;
        }
        var state = TraitData.GetState(pmcData.SessionId);
        if (state != null && state.Selected.Contains("handyman"))
        {
            __result = __result * 0.5;
        }
    }
}

/// <summary>
/// Wires DI services into the static patch fields and enables all SeasonTraits harmony
/// patches during Preload. (The 4.1.2 packages expose no ServiceLocator, so static
/// references assigned from an injectable entry point are used instead.)
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload)]
public class PatchRegistration(
    ISptLogger<PatchRegistration> logger,
    TraitDataService traitData,
    TraitApplier traitApplier,
    ItemHelper itemHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        TraderPricePatch.TraitData = traitData;
        ShooterSkillXpPatch.TraitData = traitData;
        ServerSkillCapPatch.TraitData = traitData;
        ServerSkillCapPatch.TraitApplier = traitApplier;
        PostRaidSkillCapPatch.TraitData = traitData;
        PostRaidSkillCapPatch.TraitApplier = traitApplier;
        ArmorShortagePatch.TraitData = traitData;
        ArmorShortagePatch.ItemHelper = itemHelper;
        HandymanCraftTimePatch.TraitData = traitData;

        new TraderPricePatch().Enable();
        new ShooterSkillXpPatch().Enable();
        new ServerSkillCapPatch().Enable();
        new PostRaidSkillCapPatch().Enable();
        new ArmorShortagePatch().Enable();
        new HandymanCraftTimePatch().Enable();
        logger.Success("SeasonTraits: harmony patches enabled");
        return Task.CompletedTask;
    }
}
