using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SeasonTraits.Client;

/// <summary>
/// Mirrors server-applied trait changes onto the client's LIVE session profile so effects
/// apply immediately without a game restart. Reflection path (types are obfuscated in
/// Assembly-CSharp):
///   FindObjectOfType&lt;AbstractApplication&gt;() -> GetClientBackEndSession() ->
///   GetProfileBySide(ESideType.Pmc) -> Profile.BonusController.AddBonus(descriptor.ToBonus())
///   and Profile.Skills (SkillManager).&lt;SkillId&gt;.SetCurrent(progress, silent).
/// </summary>
public static class LiveProfileMirror
{
    private static readonly System.Collections.Generic.HashSet<string> MirroredIds = new();

    public static void MirrorApplyResult(string responseJson)
    {
        try
        {
            var result = JObject.Parse(responseJson);
            var profile = GetLiveProfile();
            if (profile == null)
            {
                SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: live session profile not found, effects apply after restart");
                return;
            }
            MirrorBonuses(profile, result["bonuses"] as JArray);
            MirrorSkills(profile, result["skills"] as JArray);
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: live mirror failed ({e.Message}); effects apply after restart");
        }
    }

    private static void MirrorBonuses(object profile, JArray bonuses)
    {
        if (bonuses == null || bonuses.Count == 0)
        {
            return;
        }
        var controller = GetMember(profile, "BonusController");
        if (controller == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: BonusController not found on live profile");
            return;
        }

        var descriptorType = GetBonusDescriptorType(profile.GetType());
        var elementType = descriptorType?.GetField("Bonuses")?.FieldType.GetElementType();
        var toBonus = elementType?.GetMethod("ToBonus");
        var addBonus = controller.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "AddBonus" && m.GetParameters().Length == 2);
        if (elementType == null || toBonus == null || addBonus == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: bonus descriptor/ToBonus/AddBonus not resolved, bonuses apply after restart");
            return;
        }

        var mirrored = 0;
        foreach (var token in bonuses)
        {
            var id = token.Value<string>("id");
            if (id == null || !MirroredIds.Add(id))
            {
                continue; // already mirrored this session
            }
            var descriptor = JsonConvert.DeserializeObject(token.ToString(Formatting.None), elementType);
            if (descriptor == null)
            {
                continue;
            }
            var bonus = toBonus.Invoke(descriptor, null);
            if (bonus == null)
            {
                continue;
            }
            addBonus.Invoke(controller, new[] { bonus, true });
            mirrored++;
        }
        SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: mirrored {mirrored} trait bonuses onto the live session profile (no restart needed)");
    }

    /// <summary>Skill start levels must be set on the client's live SkillManager too — a
    /// server-side-only write is clobbered by the client's next profile save.</summary>
    private static void MirrorSkills(object profile, JArray skills)
    {
        if (skills == null || skills.Count == 0)
        {
            return;
        }
        var manager = GetMember(profile, "Skills");
        if (manager == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: live SkillManager not found");
            return;
        }
        var strengthField = manager.GetType().GetField("Strength");
        if (strengthField == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: skill record fields not found on SkillManager");
            return;
        }
        var setCurrent = (strengthField.FieldType.GetMethod("SetCurrent")
            ?? strengthField.FieldType.BaseType?.GetMethod("SetCurrent"));
        if (setCurrent == null)
        {
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: SetCurrent not found on skill records");
            return;
        }
        var set = 0;
        void SetRecord(object record, float value)
        {
            // resolve per-type: SkillManager mixes skill records with unrelated public fields
            var method = record.GetType().GetMethod("SetCurrent")
                ?? record.GetType().BaseType?.GetMethod("SetCurrent");
            if (method == null)
            {
                return;
            }
            try
            {
                method.Invoke(record, new object[] { value, true });
                set++;
            }
            catch (Exception e)
            {
                SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: skill SetCurrent failed on {record.GetType().Name}: {e.Message}");
            }
        }
        var missed = 0;
        foreach (var token in skills)
        {
            var id = token.Value<string>("id");
            var progress = token.Value<double?>("progress") ?? 0;
            // The server sends final post-apply values only (audit M-1): never a "*" entry,
            // so mirroring is a plain order-independent set of each listed skill.
            var single = manager.GetType().GetField(id, BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager);
            if (single == null)
            {
                missed++;
                continue;
            }
            SetRecord(single, (float)progress);
        }
        if (missed > 0)
        {
            SeasonTraitsPlugin.Log.LogWarning(
                $"SeasonTraits: {missed} skills not found on live SkillManager (game field mismatch); mirrored {set} skill start levels");
        }
        else
        {
            SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: mirrored {set} skill start levels onto the live SkillManager");
        }
    }

    private static object GetLiveProfile()
    {
        var app = UnityEngine.Object.FindObjectOfType<EFT.AbstractApplication>();
        var session = app?.GetType().GetMethod("GetClientBackEndSession")?.Invoke(app, null);
        if (session == null)
        {
            return null;
        }
        var getProfile = session.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetProfileBySide" && m.GetParameters().Length == 1);
        if (getProfile == null)
        {
            return null;
        }
        var side = Enum.Parse(getProfile.GetParameters()[0].ParameterType, "Pmc");
        return getProfile.Invoke(session, new[] { side });
    }

    private static Type GetBonusDescriptorType(Type profileType)
    {
        var ctor = profileType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.GetField("Bonuses") != null);
        return ctor?.GetParameters()[0].ParameterType;
    }

    private static object GetMember(object owner, string name)
    {
        var type = owner.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null)
        {
            return prop.GetValue(owner);
        }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(owner);
    }
}
