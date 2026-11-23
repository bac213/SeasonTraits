using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace SeasonTraits.Server;

/// <summary>
/// Owns traits.json + config.json loading and per-profile selection state persistence.
/// </summary>
[Injectable]
public class TraitDataService(ISptLogger<TraitDataService> logger, ModHelper modHelper)
{
    private readonly ConcurrentDictionary<string, ProfileTraitState> _stateCache = new();
    private TraitsData? _traits;
    private SeasonTraitsConfig? _config;
    private string? _modPath;

    public string GetModPath()
    {
        _modPath ??= modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        return _modPath;
    }

    public TraitsData GetTraits()
    {
        if (_traits != null)
        {
            return _traits;
        }
        _traits = modHelper.GetJsonDataFromFile<TraitsData>(GetModPath(), "db/traits.json")
            ?? throw new InvalidOperationException("SeasonTraits: failed to load db/traits.json");
        return _traits;
    }

    public SeasonTraitsConfig GetConfig()
    {
        if (_config != null)
        {
            return _config;
        }
        var loaded = modHelper.GetJsonDataFromFile<SeasonTraitsConfig>(GetModPath(), "config.json")
            ?? new SeasonTraitsConfig();
        // One-time load log doubles as the runtime proof that config keys bind (audit ML-1:
        // keys must match the [JsonPropertyName] camelCase spelling).
        logger.Info($"SeasonTraits: config loaded (periodDays={loaded.StreetTaxPeriodDays}, amount={loaded.StreetTaxAmount})");
        _config = loaded;
        return _config;
    }

    public TraitDefinition? GetTrait(string id)
        => GetTraits().Traits.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Bonus id must be a 24-hex MongoId; the owning trait id is carried in the bonus'
    /// extension data key instead.
    /// </summary>
    public const string TraitMarkerKey = "seasonTraitId";

    public static bool HasTraitMarker(Bonus? bonus)
        => bonus?.ExtensionData != null && bonus.ExtensionData.ContainsKey(TraitMarkerKey);

    public static bool IsBonusForTrait(Bonus? bonus, string traitId)
        => bonus?.ExtensionData != null
           && bonus.ExtensionData.TryGetValue(TraitMarkerKey, out var v)
           && (Equals(v, traitId) || v?.ToString() == traitId);
    // System.Text.Json deserializes ExtensionData values into JsonElement on profile load, so a
    // round-tripped profile must be compared via ToString() — Equals(JsonElement, string) is
    // always false, which let the global bonus stack on every login (duplicate ExperienceRate).

    /// <summary>
    /// Generate a 24-char hex id compatible with EFT item ids.
    /// </summary>
    public static string MakeId()
    {
        var guid = Guid.NewGuid().ToString("N");
        return guid[..24];
    }

    // ---- per-profile state ----

    public ProfileTraitState? GetState(string profileId)
    {
        if (_stateCache.TryGetValue(profileId, out var cached))
        {
            return cached;
        }
        var file = System.IO.Path.Combine(GetModPath(), "store", $"{profileId}.json");
        if (!File.Exists(file))
        {
            return null;
        }
        try
        {
            var state = JsonSerializer.Deserialize<ProfileTraitState>(File.ReadAllText(file));
            if (state != null)
            {
                _stateCache[profileId] = state;
            }
            return state;
        }
        catch (Exception e)
        {
            logger.Error($"SeasonTraits: failed to read state for {profileId}: {e.Message}");
            return null;
        }
    }

    public void SaveState(ProfileTraitState state)
    {
        var dir = System.IO.Path.Combine(GetModPath(), "store");
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, $"{state.ProfileId}.json");
        // temp + rename so a crash mid-write never leaves a truncated store file (audit L-3)
        var tmp = file + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, file, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // leftover tmp is harmless; the next write overwrites it
            }
            throw;
        }
        _stateCache[state.ProfileId] = state;
    }

    /// <summary>
    /// Validate a selection against the points economy, mutual-exclusion and once-only rules.
    /// Returns null when valid, otherwise an error message.
    /// </summary>
    public string? ValidateSelection(string profileId, List<string> selectedIds)
    {
        var traits = GetTraits().Traits;
        if (selectedIds.Count == 0)
        {
            return "未选择任何特质";
        }
        if (selectedIds.Distinct().Count() != selectedIds.Count)
        {
            return "存在重复选择的特质";
        }
        // Lock check first (audit L-5): a locked profile gets the lock message even when the
        // submitted ids are unknown/invalid.
        if (GetState(profileId) != null)
        {
            return "该档案已锁定特质，不可更改";
        }
        foreach (var id in selectedIds)
        {
            var trait = traits.FirstOrDefault(t => t.Id == id);
            if (trait == null)
            {
                return $"未知特质: {id}";
            }
            if (!trait.IsSelectable)
            {
                return $"特质 {id} 不可手动选择";
            }
        }

        // mutual exclusion
        foreach (var id in selectedIds)
        {
            var trait = traits.First(t => t.Id == id);
            var conflict = selectedIds.FirstOrDefault(blocked => trait.Blocks.Contains(blocked));
            if (conflict != null)
            {
                return $"特质冲突: {trait.NameZh} 与 {traits.First(t => t.Id == conflict).NameZh} 互斥";
            }
        }

        // points economy: negative traits grant points, positive traits spend them, balance must be >= 0
        var balance = selectedIds.Sum(id =>
        {
            var t = traits.First(x => x.Id == id);
            return t.Kind == TraitKind.Negative ? t.Points : -t.Points;
        });
        if (balance < 0)
        {
            return $"点数不足: 余额 {balance}，需要 ≥ 0";
        }
        return null;
    }

    public static int ComputeBalance(IEnumerable<TraitDefinition> selected)
        => selected.Sum(t => t.Kind == TraitKind.Negative ? t.Points : -t.Points);
}
