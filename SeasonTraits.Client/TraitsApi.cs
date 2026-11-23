using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace SeasonTraits.Client;

/// <summary>
/// Thin read-only client for the SeasonTraits server endpoints used by the character-screen tab.
/// Every call is defensive: the tab must never take the game down when the server is slow or absent.
/// </summary>
internal static class TraitsApi
{
    private static JArray _catalog;

    internal sealed class Selection
    {
        public readonly List<string> Ids = new List<string>();
        public bool Locked;
        public bool ProfileReady = true;
    }

    /// <summary>GET /seasonTraits/list — trait catalog, cached for the session.</summary>
    internal static JArray GetCatalog(bool force = false)
    {
        if (_catalog != null && !force)
        {
            return _catalog;
        }

        try
        {
            var json = RequestHandler.GetJson("/seasonTraits/list");
            var traits = JObject.Parse(json)["traits"] as JArray;
            if (traits != null && traits.Count > 0)
            {
                _catalog = traits;
                SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: tab catalog loaded ({traits.Count} traits)");
            }
            else
            {
                SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: tab catalog response had no traits");
            }
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: tab catalog request failed: {e.Message}");
        }

        return _catalog;
    }

    /// <summary>GET /seasonTraits/profile — selected trait ids for the current session profile. Null on failure.</summary>
    internal static Selection GetSelection()
    {
        try
        {
            var json = RequestHandler.GetJson("/seasonTraits/profile");
            var obj = JObject.Parse(json);
            var selection = new Selection
            {
                Locked = obj.Value<bool?>("locked") ?? false,
                ProfileReady = obj.Value<bool?>("profileReady") ?? true,
            };

            if (obj["selected"] is JArray selected)
            {
                foreach (var token in selected)
                {
                    var id = token?.Value<string>();
                    if (!string.IsNullOrEmpty(id))
                    {
                        selection.Ids.Add(id);
                    }
                }
            }

            return selection;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: tab profile request failed: {e.Message}");
            return null;
        }
    }

    internal static JObject FindTrait(JArray catalog, string id)
    {
        if (catalog == null || string.IsNullOrEmpty(id))
        {
            return null;
        }

        return catalog.OfType<JObject>()
            .FirstOrDefault(t => string.Equals(t.Value<string>("id"), id, StringComparison.Ordinal));
    }
}
