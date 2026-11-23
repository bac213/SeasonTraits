using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeasonTraits.Client;

/// <summary>
/// Adds a third "特质" sub-tab to the character screen's Skills / Mastering tab bar and shows a
/// read-only summary of the traits selected for the current profile.
///
/// Everything here is deliberately late and lazy: the tab is cloned only after the screen has been
/// shown for a frame (never during prefab Awake, where touching the hierarchy can race with the
/// menu load), and the summary panel is built on the first click. Any failure costs the tab only.
/// </summary>
internal static class TraitsTab
{
    private const string TabLabel = "特质";
    private const float TabGap = 2f;

    private sealed class Entry
    {
        public Tab Tab;
        public RectTransform Rect;
        public TraitsTabController Controller;
    }

    private static readonly ConditionalWeakTable<SkillsAndMasteringScreen, Entry> Installed =
        new ConditionalWeakTable<SkillsAndMasteringScreen, Entry>();

    /// <summary>Called from the Show postfix; installs once, two frames later.</summary>
    internal static void OnShown(SkillsAndMasteringScreen screen)
    {
        if (screen == null || Installed.TryGetValue(screen, out _))
        {
            return;
        }

        try
        {
            screen.StartCoroutine(InstallLater(screen));
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: could not schedule traits tab install: {e.Message}");
        }
    }

    private static IEnumerator InstallLater(SkillsAndMasteringScreen screen)
    {
        yield return null;
        yield return null;

        try
        {
            Install(screen);
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogError($"SeasonTraits: traits tab install failed: {e}");
        }
    }

    private static void Install(SkillsAndMasteringScreen screen)
    {
        if (screen == null || Installed.TryGetValue(screen, out _))
        {
            return;
        }

        if (screen._skillsTab == null || screen._masteringTab == null || screen._skillsScreen == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: skills screen layout changed, traits tab not installed");
            return;
        }

        var group = AccessTools.FieldRefAccess<SkillsAndMasteringScreen, TabGroup>("_skillMasterTabGroup")(screen);
        if (group == null || group._tabs == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: tab group not ready, traits tab not installed");
            return;
        }

        var source = screen._masteringTab;
        var parent = source.transform.parent;
        var clone = UnityEngine.Object.Instantiate(source.gameObject, parent);
        clone.name = "SeasonTraitsTab";

        var tab = clone.GetComponent<Tab>();
        if (tab == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: cloned tab has no Tab component, aborting");
            UnityEngine.Object.Destroy(clone);
            return;
        }

        ApplyLabel(clone);

        var cloneRect = (RectTransform)clone.transform;
        PlaceTab(cloneRect, (RectTransform)source.transform,
            screen._skillsTab != null ? (RectTransform)screen._skillsTab.transform : null, parent);

        clone.SetActive(true);

        HideMasteringSearchField(screen);

        var controller = new TraitsTabController(screen);
        tab.Init(controller);
        tab.OnSelectionChanged += group.SelectionChangedHandler;

        var tabs = group._tabs;
        var extended = new Tab[tabs.Length + 1];
        Array.Copy(tabs, extended, tabs.Length);
        extended[tabs.Length] = tab;
        group._tabs = extended;

        clone.transform.SetAsLastSibling();

        Installed.Add(screen, new Entry { Tab = tab, Rect = cloneRect, Controller = controller });

        SeasonTraitsPlugin.Log.LogInfo(
            $"SeasonTraits: traits tab installed (parent='{parent.name}', tabs={extended.Length}, layoutGroup={parent.GetComponent<LayoutGroup>() != null})");
    }

    /// <summary>
    /// The Mastering screen's "search weapon" input sits exactly where the third tab has to go.
    /// Hide it (disable only, the component stays valid for MasteringList) so the tab is readable.
    /// </summary>
    private static void HideMasteringSearchField(SkillsAndMasteringScreen screen)
    {
        try
        {
            var mastering = screen._masteringScreen;
            var field = mastering != null ? mastering._searchWeaponInputField : null;
            if (field == null)
            {
                return;
            }

            field.gameObject.SetActive(false);
            SeasonTraitsPlugin.Log.LogInfo("SeasonTraits: hid the mastering search field");
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: could not hide mastering search field: {e.Message}");
        }
    }

    /// <summary>Replaces the cloned tab's localized caption with our own label.</summary>
    private static void ApplyLabel(GameObject clone)
    {
        foreach (var localized in clone.GetComponentsInChildren<LocalizedText>(true))
        {
            // Stops the localization manager from writing "专精" back over our caption.
            localized.enabled = false;
        }

        foreach (var tmp in clone.GetComponentsInChildren<TMP_Text>(true))
        {
            tmp.text = TabLabel;
        }

        foreach (var legacy in clone.GetComponentsInChildren<Text>(true))
        {
            legacy.text = TabLabel;
        }
    }

    /// <summary>
    /// Sits the clone to the right of the Mastering tab. The tab bar may either be a plain
    /// RectTransform layout (tabs positioned by anchors) or a layout group; both are handled.
    /// </summary>
    private static void PlaceTab(RectTransform rect, RectTransform reference, RectTransform previous, Transform parent)
    {
        rect.anchorMin = reference.anchorMin;
        rect.anchorMax = reference.anchorMax;
        rect.pivot = reference.pivot;
        rect.sizeDelta = reference.sizeDelta;
        rect.localScale = reference.localScale;

        if (parent.GetComponent<LayoutGroup>() != null)
        {
            SeasonTraitsPlugin.Log.LogInfo("SeasonTraits: tab bar uses a LayoutGroup, tab position left to the layout");
            return;
        }

        var direction = Vector2.right;
        if (previous != null)
        {
            var delta = reference.anchoredPosition - previous.anchoredPosition;
            direction = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y)
                ? new Vector2(delta.x >= 0f ? 1f : -1f, 0f)
                : new Vector2(0f, delta.y >= 0f ? 1f : -1f);
        }

        var extent = direction.x != 0f ? reference.rect.width : reference.rect.height;
        if (extent <= 0f)
        {
            extent = direction.x != 0f ? reference.sizeDelta.x : reference.sizeDelta.y;
        }

        rect.anchoredPosition = reference.anchoredPosition + direction * (extent + TabGap);

        SeasonTraitsPlugin.Log.LogInfo(
            $"SeasonTraits: tab bar parent='{parent.name}' components=[{string.Join(", ", parent.GetComponents<Component>().Select(c => c.GetType().Name))}] " +
            $"skills=({previous?.anchoredPosition.ToString() ?? "n/a"}, {previous?.sizeDelta.ToString() ?? "n/a"}) " +
            $"mastering=({reference.anchoredPosition}, {reference.sizeDelta}) dir={direction} new=({rect.anchoredPosition})");
    }

    private sealed class TraitsTabController : ITabController
    {
        private readonly SkillsAndMasteringScreen _screen;
        private TraitsSummaryPanel _panel;

        public TraitsTabController(SkillsAndMasteringScreen screen)
        {
            _screen = screen;
        }

        public void Show()
        {
            try
            {
                if (_panel == null && _screen != null && _screen._skillsScreen != null)
                {
                    var template = _screen._skillsScreen.transform as RectTransform;
                    _panel = TraitsSummaryPanel.Create(_screen._skillsScreen.transform.parent, template);
                    SeasonTraitsPlugin.Log.LogInfo("SeasonTraits: traits summary panel created");
                }

                _panel?.ShowPanel();
            }
            catch (Exception e)
            {
                SeasonTraitsPlugin.Log.LogError($"SeasonTraits: failed to show traits panel: {e}");
            }
        }

        public Task<bool> TryHide()
        {
            _panel?.HidePanel();
            return Task.FromResult(true);
        }
    }
}

/// <summary>Harmony entry point: schedule the tab install after the screen has been shown.</summary>
public class SkillsTabShowPatch : BasePatch
{
    protected override MethodBase GetTargetMethod()
        => AccessTools.Method(typeof(SkillsAndMasteringScreen), "Show");

    [HarmonyPostfix]
    public static void Postfix(SkillsAndMasteringScreen __instance)
    {
        TraitsTab.OnShown(__instance);
    }
}
