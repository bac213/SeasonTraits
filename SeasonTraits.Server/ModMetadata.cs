using SPTarkov.Server.Core.Models.Spt.Mod;

namespace SeasonTraits.Server;

/// <summary>
/// Required by the SPT mod loader, replaces the old package.json.
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.spt.seasontraits";
    public string Name { get; init; } = "SeasonTraits";
    public string Author { get; init; } = "SPT user";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "CC0-1.0";
    public bool HasPrepatcher { get; init; } = false;
}
