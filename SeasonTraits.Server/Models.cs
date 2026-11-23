using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;

namespace SeasonTraits.Server;

public record TraitEffect
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }
    [JsonPropertyName("skillId")]
    public string? SkillId { get; init; }
    [JsonPropertyName("value")]
    public double? Value { get; init; }
    [JsonPropertyName("bonusType")]
    public string? BonusType { get; init; }
    [JsonPropertyName("itemId")]
    public string? ItemId { get; init; }
    [JsonPropertyName("amount")]
    public double? Amount { get; init; }
}

public record TraitDefinition
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("nameZh")]
    public required string NameZh { get; init; }
    [JsonPropertyName("nameEn")]
    public required string NameEn { get; init; }
    /// <summary>"positive" | "negative"</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("points")]
    public required int Points { get; init; }
    [JsonPropertyName("descriptionZh")]
    public required string DescriptionZh { get; init; }
    [JsonPropertyName("icon")]
    public string? Icon { get; init; }
    [JsonPropertyName("effects")]
    public List<TraitEffect> Effects { get; init; } = new();
    [JsonPropertyName("blocks")]
    public List<string> Blocks { get; init; } = new();

    public bool IsSelectable => Kind is "positive" or "negative";
}

public record TraitsData
{
    [JsonPropertyName("traits")]
    public List<TraitDefinition> Traits { get; init; } = new();
}

public record SeasonTraitsConfig
{
    [JsonPropertyName("streetTaxPeriodDays")]
    public double StreetTaxPeriodDays { get; init; } = 7;
    [JsonPropertyName("streetTaxAmount")]
    public double StreetTaxAmount { get; init; } = 50000;
}

public record ApplyRequestData : IRequestData
{
    [JsonPropertyName("selected")]
    public List<string>? Selected { get; init; }
}

public record ApplyResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }
    [JsonPropertyName("error")]
    public string? Error { get; init; }
    [JsonPropertyName("selected")]
    public List<string> Selected { get; init; } = new();
    [JsonPropertyName("balance")]
    public int Balance { get; init; }
    /// <summary>All trait-bound bonuses now on the profile (per-trait + global); the client mirrors
    /// these into its live session profile so effects apply without a restart.</summary>
    [JsonPropertyName("bonuses")]
    public List<Bonus> Bonuses { get; init; } = new();
    /// <summary>Final Info.Bans of the pmc (flea ban etc.), same mirroring purpose.</summary>
    /// <summary>Skill start levels granted by the selection (id = ESkillId name, progress = 100/level).
    /// The client must set these on its live SkillManager too — a server-side-only write is
    /// clobbered by the client's next profile save.</summary>
    [JsonPropertyName("skills")]
    public List<SkillProgressData> Skills { get; init; } = new();
    [JsonPropertyName("bans")]
    public List<Ban> Bans { get; init; } = new();
}

public record SkillProgressData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";
    [JsonPropertyName("progress")]
    public double Progress { get; init; }
}

/// <summary>
/// Persisted per profile in user/mods/.../store/&lt;profileId&gt;.json
/// </summary>
public record ProfileTraitState
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; init; } = "";
    [JsonPropertyName("selected")]
    public List<string> Selected { get; set; } = new();
    [JsonPropertyName("lockedAt")]
    public long LockedAt { get; set; }
    /// <summary>Charisma progress captured when personality_vacuum was taken (skill locked at that value).</summary>
    [JsonPropertyName("charismaProgress")]
    public double? CharismaProgress { get; set; }
    /// <summary>Unix seconds of the last street_tax payout.</summary>
    [JsonPropertyName("streetTaxPaidUpTo")]
    public double StreetTaxPaidUpTo { get; set; }
}

public record GetProfileResponse
{
    [JsonPropertyName("locked")]
    public bool Locked { get; init; }
    [JsonPropertyName("selected")]
    public List<string> Selected { get; init; } = new();
    /// <summary>False while the in-game profile creation flow has not filled the pmc skeleton.</summary>
    [JsonPropertyName("profileReady")]
    public bool ProfileReady { get; init; } = true;
}

public static class TraitKind
{
    public const string Positive = "positive";
    public const string Negative = "negative";
}
