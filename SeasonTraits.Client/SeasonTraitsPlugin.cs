using System.Linq;
using BepInEx;
using BepInEx.Logging;
using EFT.UI;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace SeasonTraits.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class SeasonTraitsPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.spt.seasontraits.client";
    public const string PluginName = "SeasonTraits.Client";
    public const string PluginVersion = "0.1.2";

    internal static ManualLogSource Log;

    private bool _checkedThisSession;
    private float _lastCheck = -10f;
    private TraitsPanelUI _panel;

    private void Awake()
    {
        Log = Logger;
        Logger.LogInfo($"SeasonTraits.Client {PluginVersion} loaded");
        try
        {
            TraitPatches.Patch();
        }
        catch (System.Exception e)
        {
            Logger.LogError($"SeasonTraits: failed to enable harmony patches: {e}");
        }
    }
    private float _menuSeenAt = -1f;

    private void Update()
    {
        if (_checkedThisSession)
        {
            _panel?.EnsureVisible();
            return;
        }
        // main menu is up: MenuUI singleton only exists in the menu scene
        if (MenuUI.Instance != null && MenuUI.Instance.isActiveAndEnabled)
        {
            // the menu keeps loading canvases after MenuUI appears; wait for it to settle
            // so the panel is created after the menu's own full-screen canvases
            if (_menuSeenAt < 0f)
            {
                _menuSeenAt = Time.realtimeSinceStartup;
            }
            if (Time.realtimeSinceStartup - _menuSeenAt < 3f)
            {
                return;
            }
            if (Time.realtimeSinceStartup - _lastCheck < 2f)
            {
                return;
            }
            _lastCheck = Time.realtimeSinceStartup;
            _checkedThisSession = true;
            try
            {
                var profileJson = RequestHandler.GetJson("/seasonTraits/profile");
                var profile = JObject.Parse(profileJson);
                var selected = (profile["selected"] as JArray)?.Select(t => t.Value<string>()).ToList()
                    ?? new System.Collections.Generic.List<string>();
                TraitEffectsState.SetSelected(selected);
                AllergenState.EnsureAllergens(selected.Contains("allergic"));
                var locked = profile.Value<bool?>("locked") ?? true;
                var ready = profile.Value<bool?>("profileReady") ?? true;
                if (!ready)
                {
                    // login-time profile skeleton: wait until the in-game creation flow finishes
                    _checkedThisSession = false;
                    Logger.LogInfo("SeasonTraits: profile not ready yet, waiting for in-game creation to finish");
                    return;
                }
                Logger.LogInfo($"SeasonTraits: session={RequestHandler.SessionId}");
                if (locked)
                {
                    Logger.LogInfo("SeasonTraits: profile already has traits, panel not shown");
                    return;
                }

                var listJson = RequestHandler.GetJson("/seasonTraits/list");
                var traits = JObject.Parse(listJson)["traits"] as JArray;
                if (traits == null || traits.Count == 0)
                {
                    Logger.LogWarning("SeasonTraits: server returned an empty trait list");
                    return;
                }

                var es = UnityEngine.EventSystems.EventSystem.current;
                Logger.LogInfo($"SeasonTraits: EventSystem={es?.GetType().FullName} module={es?.currentInputModule?.GetType().FullName}");
                _panel = TraitsPanelUI.Create(traits, OnApply, OnClosed);
                Logger.LogInfo("SeasonTraits: trait selection panel shown");
            }
            catch (System.Exception e)
            {
                Logger.LogError($"SeasonTraits: failed to init panel: {e}");
                if (_panel == null)
                {
                    // transient failure (e.g. server not up yet): keep polling so the panel can
                    // still appear this session instead of being stuck forever (audit M-4)
                    _checkedThisSession = false;
                }
            }
        }
    }

    private static string OnApply(JArray selected)
    {
        var body = new JObject { ["selected"] = selected }.ToString(Newtonsoft.Json.Formatting.None);
        return RequestHandler.PostJson("/seasonTraits/apply", body);
    }

    private void OnClosed()
    {
        // never show again this session; the server rejects re-apply anyway
        _panel = null;
    }
}
