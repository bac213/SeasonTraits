using System.Collections.Generic;

namespace SeasonTraits.Client;

/// <summary>
/// Cached set of the current profile's selected trait ids, populated from the /seasonTraits/profile
/// response (and refreshed on apply). In-raid Harmony patches consult this to decide whether to
/// apply an effect, so the effects only run for the local player's chosen traits.
/// </summary>
public static class TraitEffectsState
{
    private static readonly HashSet<string> SelectedTraits = new();

    public static void SetSelected(IEnumerable<string> ids)
    {
        SelectedTraits.Clear();
        if (ids == null)
        {
            return;
        }
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
            {
                SelectedTraits.Add(id);
            }
        }
    }

    public static bool Has(string traitId)
        => traitId != null && SelectedTraits.Contains(traitId);
}
