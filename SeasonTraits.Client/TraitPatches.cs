using System;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.HealthSystem;
using EFT.Hideout;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using EFT.UI.Matchmaker;
using HarmonyLib;
using UnityEngine;

namespace SeasonTraits.Client;

/// <summary>
/// All in-raid Harmony patches for the numeric and health traits. Every patch guards on
/// <see cref="TraitEffectsState.Has"/> so effects only run for the local profile's selected traits.
/// </summary>
public static class TraitPatches
{
    private static bool _patched;

    public static void Patch()
    {
        if (_patched)
        {
            return;
        }
        _patched = true;
        new SkillsTabShowPatch().Enable();
        new SprinterPatch().Enable();
        new ThirdLegPatch().Enable();
        new FallDamagePatch().Enable();
        new FracturePatch().Enable();
        new BleedPatch().Enable();
        new WellThatHurtPatch().Enable();
        new SafecrackerPatch().Enable();
        new SafecrackerKeycardPatch().Enable();
        new ExhaustionPatch().Enable();
        new MarathonRunnerPatch().Enable();
        new BushbornePatch().Enable();
        new BushborneSlowdownPatch().Enable();
        new DrJekyllPatch().Enable();
        new AllergicPatch().Enable();
        new JuiceTimePatch().Enable();
        new SailorsNostalgiaPatch().Enable();
        new BrokenSecureContainerPatch().Enable();
        new NoInsurancePatch().Enable();
        new NoFirHideoutPatch().Enable();
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 1 — numeric movement
// ---------------------------------------------------------------------------------------------

public class SprinterPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertyGetter(typeof(MovementContext), "SprintingSpeed");

    [HarmonyPostfix]
    public static void Postfix(MovementContext __instance, ref float __result)
    {
        if (TraitEffectsState.Has("sprinter")
            && __instance != null
            && AccessTools.Field(typeof(MovementContext), "_player")?.GetValue(__instance) is Player player
            && player.IsYourPlayer)
        {
            __result *= 1.05f;
        }
    }
}

public class ThirdLegPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertyGetter(typeof(MovementContext), "MaxSpeed");

    [HarmonyPostfix]
    public static void Postfix(MovementContext __instance, ref float __result)
    {
        if (TraitEffectsState.Has("third_leg")
            && __instance != null
            && AccessTools.Field(typeof(MovementContext), "_player")?.GetValue(__instance) is Player player
            && player.IsYourPlayer)
        {
            __result *= 0.99f;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 1 — fall damage & fracture / bleed probabilities
// ---------------------------------------------------------------------------------------------

/// <summary>
/// sturdy_bones: -20% fall damage; osteoporosis: +20%. Physical.OnWeightUpdated assigns
/// FallDamageMultiplier from scratch each time, so a postfix scaling is idempotent.
/// </summary>
public class FallDamagePatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(Physical), "OnWeightUpdated");

    [HarmonyPostfix]
    public static void Postfix(Physical __instance)
    {
        if (TraitEffectsState.Has("sturdy_bones"))
        {
            __instance.FallDamageMultiplier *= 0.8f;
        }
        else if (TraitEffectsState.Has("osteoporosis"))
        {
            __instance.FallDamageMultiplier *= 1.2f;
        }
    }
}

/// <summary>
/// sturdy_bones: -25% fracture chance; osteoporosis: +25%. The probability objects live in the
/// global health config (GlobalConfiguration.Health.Effects) read at ApplyDamage time, so scale
/// them once at raid start. Applied once per session; a static guard prevents compounding. In a
/// shared raid this would affect bots too (SPT is effectively solo — accepted).
/// </summary>
public class FracturePatch : BasePatch
{
    private static bool _applied;

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(GameWorld), "OnGameStarted");

    [HarmonyPostfix]
    public static void Postfix()
    {
        if (_applied || !Singleton<GlobalConfiguration>.Instantiated)
        {
            return;
        }
        float factor = 0f;
        if (TraitEffectsState.Has("sturdy_bones"))
        {
            factor = 0.75f;
        }
        else if (TraitEffectsState.Has("osteoporosis"))
        {
            factor = 1.25f;
        }
        if (factor != 0f)
        {
            var effects = Singleton<GlobalConfiguration>.Instance.Health.Effects;
            ScaleProb(effects.Fracture.FallingProbability, factor);
            ScaleProb(effects.Fracture.BulletHitProbability, factor);
            _applied = true;
        }
    }

    private static void ScaleProb(EffectsSettings.ProbabilitySetting prob, float factor)
    {
        prob.K *= factor;
        prob.B *= factor;
    }
}

/// <summary>
/// thrombophilia: -25% bleed chance; hemophilia: +25%. Same global-config scaling as fracture.
/// </summary>
public class BleedPatch : BasePatch
{
    private static bool _applied;

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(GameWorld), "OnGameStarted");

    [HarmonyPostfix]
    public static void Postfix()
    {
        if (_applied || !Singleton<GlobalConfiguration>.Instantiated)
        {
            return;
        }
        float factor = 0f;
        if (TraitEffectsState.Has("thrombophilia"))
        {
            factor = 0.75f;
        }
        else if (TraitEffectsState.Has("hemophilia"))
        {
            factor = 1.25f;
        }
        if (factor != 0f)
        {
            var effects = Singleton<GlobalConfiguration>.Instance.Health.Effects;
            ScaleProb(effects.HeavyBleeding.Probability, factor);
            ScaleProb(effects.LightBleeding.Probability, factor);
            _applied = true;
        }
    }

    private static void ScaleProb(EffectsSettings.ProbabilitySetting prob, float factor)
    {
        prob.K *= factor;
        prob.B *= factor;
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 1 — medkit resource consumption
// ---------------------------------------------------------------------------------------------

/// <summary>
/// well_that_hurt: +25% medkit resource consumption. Scaling the per-tick deltaTime makes each
/// RegularUpdate tick process 25% more time, so the medkit burns HpResource 25% faster over its
/// fixed work time (i.e. consumes 25% more total resource for the same real duration).
/// </summary>
public class WellThatHurtPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var medEffect = AccessTools.FirstInner(typeof(ActiveHealthController), t => t.Name == "MedEffect");
        return AccessTools.Method(medEffect, "RegularUpdate");
    }

    [HarmonyPrefix]
    public static void Prefix(ref float deltaTime)
    {
        if (TraitEffectsState.Has("well_that_hurt"))
        {
            deltaTime *= 1.25f;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 1 — key durability
// ---------------------------------------------------------------------------------------------

/// <summary>
/// safecracker: 25% chance a mechanical key use costs no durability. UnlockOperation does
/// key.NumberOfUsages++; pre-decrementing by 1 (only in the 25% case) makes the body's ++ restore
/// the original count and avoids crossing the discard threshold.
/// </summary>
public class SafecrackerPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(WorldInteractiveObject), "UnlockOperation");

    [HarmonyPrefix]
    public static void Prefix(KeyComponent key)
    {
        if (TraitEffectsState.Has("safecracker") && UnityEngine.Random.value < 0.25f)
        {
            key.NumberOfUsages -= 1;
        }
    }
}

/// <summary>KeycardDoor overrides UnlockOperation with the same usage increment.</summary>
public class SafecrackerKeycardPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(KeycardDoor), "UnlockOperation");

    [HarmonyPrefix]
    public static void Prefix(KeyComponent key)
    {
        if (TraitEffectsState.Has("safecracker") && UnityEngine.Random.value < 0.25f)
        {
            key.NumberOfUsages -= 1;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 1 — stamina
// ---------------------------------------------------------------------------------------------

/// <summary>
/// exhaustion: -20% stamina restoration and -10 stamina capacity. The fields are read in
/// PhysicalBase.Init when the Stamina pools are built, so scale them in a prefix before the base init.
/// Only the local player's physical is affected.
/// </summary>
public class ExhaustionPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(PhysicalBase), "Init");

    [HarmonyPrefix]
    public static void Prefix(PhysicalBase __instance, IPlayer iPlayer)
    {
        if (!TraitEffectsState.Has("exhaustion") || iPlayer == null || !iPlayer.IsYourPlayer)
        {
            return;
        }
        __instance.StaminaCapacity = Mathf.Max(1f, __instance.StaminaCapacity - 10f);
        __instance.HandsCapacity = Mathf.Max(1f, __instance.HandsCapacity - 10f);
        __instance.StaminaRestoreRate *= 0.8f;
        __instance.HandsRestoreRate *= 0.8f;
    }
}

/// <summary>
/// marathon_runner: -20% arm/leg stamina consumption. The consumption deltas are lazy funcs; wrap
/// the Sprint and Walk deltas so they yield 0.8x of the original value. Done once when the local
/// player's physical is initialised.
/// </summary>
public class MarathonRunnerPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(Physical), "Init");

    [HarmonyPostfix]
    public static void Postfix(Physical __instance, IPlayer player)
    {
        if (!TraitEffectsState.Has("marathon_runner") || player == null || !player.IsYourPlayer)
        {
            return;
        }
        Scale(__instance, Physical.EConsumptionType.Sprint);
        Scale(__instance, Physical.EConsumptionType.Walk);
    }

    private static void Scale(Physical physical, Physical.EConsumptionType type)
    {
        var delta = physical.Consumptions[type].Delta;
        physical.Consumptions[type].Delta = new Compute<float>(() => delta.Value * 0.8f);
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 2 — vegetation noise & slowdown
// ---------------------------------------------------------------------------------------------

/// <summary>
/// bushborne: -75% vegetation noise and -75% vegetation movement slowdown.
/// Noise: multiply CovertMovementVolumeBySpeed by 0.25 only while on a soft surface.
/// Slowdown: when the swamp speed limit is applied, shrink the slowdown (1 - limit) by 75%.
/// </summary>
public class BushbornePatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.PropertyGetter(typeof(MovementContext), "CovertMovementVolumeBySpeed");

    [HarmonyPostfix]
    public static void Postfix(MovementContext __instance, ref float __result)
    {
        if (TraitEffectsState.Has("bushborne") && __instance.SoftSurface)
        {
            __result *= 0.25f;
        }
    }
}

public class BushborneSlowdownPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(MovementContext), "AddStateSpeedLimit",
            new[] { typeof(float), typeof(Player.ESpeedLimit) });

    [HarmonyPrefix]
    public static void Prefix(ref float speedLimit, Player.ESpeedLimit cause)
    {
        if (TraitEffectsState.Has("bushborne") && cause == Player.ESpeedLimit.Swamp)
        {
            speedLimit = 1f - (1f - speedLimit) * 0.25f;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 2 — fresh wounds
// ---------------------------------------------------------------------------------------------

/// <summary>
/// dr_jekyll: fresh wounds cannot be removed during the raid. Patch the shared Effect.NextState()
/// so a Wound never transitions out of the Started state while the trait is active (it would then
/// be removed once its work time elapsed).
/// </summary>
public class DrJekyllPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var effect = AccessTools.FirstInner(typeof(ActiveHealthController), t => t.Name == "Effect");
        return AccessTools.Method(effect, "NextState");
    }

    [HarmonyPrefix]
    public static bool Prefix(ActiveHealthController.Effect __instance)
    {
        return !(TraitEffectsState.Has("dr_jekyll")
                 && __instance is ActiveHealthController.Wound
                 && __instance.State == EEffectState.Started);
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 2 — allergic
// ---------------------------------------------------------------------------------------------

/// <summary>
/// allergic: consuming one of the profile's pre-rolled allergen items triggers intoxication.
/// The three allergens are rolled client-side by AllergenState; a prefix on DoMedEffect
/// short-circuits to DoIntoxication when the consumed item matches.
/// </summary>
public class AllergicPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(ActiveHealthController), "DoMedEffect");

    [HarmonyPrefix]
    public static void Prefix(ActiveHealthController __instance, Item item)
    {
        if (!TraitEffectsState.Has("allergic") || item == null)
        {
            return;
        }
        if (AllergenState.IsAllergen(item.TemplateId.ToString()))
        {
            __instance.DoIntoxication();
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 2 — drink/food buffs
// ---------------------------------------------------------------------------------------------

/// <summary>
/// juice_time: drinking a juice item grants a painkiller effect. There is no PainKiller stimulator
/// buff type in EStimulatorBuffType, so this cannot be done via StimulatorBuffs injection — a
/// client patch on DoMedEffect applies the effect directly instead.
/// </summary>
public class JuiceTimePatch : BasePatch
{
    private static readonly System.Collections.Generic.HashSet<string> JuiceTemplates = new()
    {
        "544fb62a4bdc2dfb738b4568", // juice_army
        "57513f07245977207e26a311", // drink_juice_apple
        "57513f9324597720a7128161", // drink_juice_grand
        "57513fcc24597720a31c09a6", // drink_juice_vita
    };

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(ActiveHealthController), "DoMedEffect");

    [HarmonyPrefix]
    public static void Prefix(ActiveHealthController __instance, Item item)
    {
        if (TraitEffectsState.Has("juice_time") && item != null
            && JuiceTemplates.Contains(item.TemplateId.ToString()))
        {
            __instance.DoPainKiller();
        }
    }
}

/// <summary>
/// sailors_nostalgia: eating canned fish grants a short health-regeneration buff. Applied as a
/// client patch on DoMedEffect using the scav regeneration effect (HealthRate approximation).
/// </summary>
public class SailorsNostalgiaPatch : BasePatch
{
    private static readonly System.Collections.Generic.HashSet<string> FishTemplates = new()
    {
        "5673de654bdc2d180f8b456d", // item_food_konserva_fish (Saury Can)
        "5bc9c29cd4351e003562b8a3", // item_food_sprats
    };

    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(ActiveHealthController), "DoMedEffect");

    [HarmonyPrefix]
    public static void Prefix(ActiveHealthController __instance, Item item)
    {
        if (TraitEffectsState.Has("sailors_nostalgia") && item != null
            && FishTemplates.Contains(item.TemplateId.ToString()))
        {
            __instance.DoScavRegeneration(2f);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Phase 2 — broken secure container
// ---------------------------------------------------------------------------------------------

/// <summary>
/// broken_secure_container: the secure container may only hold cash, keys, dogtags, special gear,
/// certain containers and battle-pass files. Client-side only (the server does not re-validate).
/// Patch Slot.CheckConditions to reject disallowed items when the slot's root item is a secure
/// container.
/// </summary>
public class BrokenSecureContainerPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(Slot), "CheckConditions");

    [HarmonyPrefix]
    public static bool Prefix(Slot __instance, Item item, ref Option<bool> __result)
    {
        if (!TraitEffectsState.Has("broken_secure_container") || item == null || __instance.ParentItem == null)
        {
            return true;
        }
        var root = __instance.ParentItem.GetRootItem();
        if (root == null || !ItemViewFactory.IsSecureContainer(root))
        {
            return true;
        }
        if (IsAllowed(item))
        {
            return true;
        }
        __result = new Slot.ItemFiltersWontAllowError(item, __instance);
        return false;
    }

    private static bool IsAllowed(Item item)
    {
        // cash
        if (item.TemplateId == "5449016a4bdc2d6f028b456f" /* roubles */
            || item.TemplateId == "5696686a4bdc2da3298b456a" /* dollars */
            || item.TemplateId == "569668774bdc2da3298b456b" /* euros */)
        {
            return true;
        }
        // keys
        if (item.TryGetItemComponent<KeyComponent>(out _))
        {
            return true;
        }
        // dogtags / quest / special items
        if (item.QuestItem)
        {
            return true;
        }
        return false;
    }
}

// ---------------------------------------------------------------------------------------------
// base patch type
// ---------------------------------------------------------------------------------------------

/// <summary>
/// no_insurance (selectable negative trait): skip the pre-raid insurance screen entirely so the
/// player cannot insure gear. Patching Show(controller) avoids the "Sequence contains no matching
/// element" crash the game throws when no insurer trader is available.
/// </summary>
public class NoInsurancePatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(MatchmakerInsuranceScreen), "Show",
            new[] { typeof(MatchmakerInsuranceScreen.InsuranceScreenController) });

    [HarmonyPostfix]
    public static void Postfix(MatchmakerInsuranceScreen.InsuranceScreenController controller)
    {
        if (TraitEffectsState.Has("no_insurance"))
        {
            // Advance after the screen is fully shown so the matchmaker flow transitions cleanly
            // instead of freezing when the screen is hidden without advancing.
            controller.ShowNextScreen();
        }
    }
}

/// <summary>
/// no_fir_hideout (selectable positive trait): hideout craft item slots accept non-FiR items.
/// ItemRequirement.IsSuitableItem rejects non-FiR stock when the requirement demands FiR;
/// clearing the flag on the local profile's recipe requirements lets the original check pass.
/// </summary>
public class NoFirHideoutPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(ItemRequirement), "IsSuitableItem",
            new[] { typeof(IExchangeRequirement), typeof(Item) });

    [HarmonyPrefix]
    public static void Prefix(IExchangeRequirement requirement)
    {
        if (TraitEffectsState.Has("no_fir_hideout") && requirement is ItemRequirement ir)
        {
            AccessTools.PropertySetter(typeof(ItemRequirement), "IsSpawnedInSession")?.Invoke(ir, new object[] { false });
        }
    }
}

// ---------------------------------------------------------------------------------------------
// base patch type
// ---------------------------------------------------------------------------------------------

public abstract class BasePatch
{
    private static readonly Harmony Instance = new("com.spt.seasontraits.client");

    public void Enable()
    {
        var target = GetTargetMethod();
        if (target == null)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: target method not found for {GetType().Name}");
            return;
        }
        var prefix = GetType().GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var postfix = GetType().GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Instance.Patch(target,
            prefix: prefix != null ? new HarmonyMethod(prefix) : null,
            postfix: postfix != null ? new HarmonyMethod(postfix) : null);
        SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: patched {target.DeclaringType?.Name}.{target.Name}");
    }

    protected abstract MethodBase GetTargetMethod();
}
