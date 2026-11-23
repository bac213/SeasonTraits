using System.Collections.Concurrent;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace SeasonTraits.Server;

using SPTarkov.Server.Core.Servers;
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class SeasonTraitsRouter(JsonUtil jsonUtil, SeasonTraitsRouterCallback callback)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<EmptyRequestData>(
            "/seasonTraits/list",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleList(sessionId)
        ),
        new RouteAction<EmptyRequestData>(
            "/seasonTraits/profile",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleGetProfile(sessionId)
        ),
        new RouteAction<ApplyRequestData>(
            "/seasonTraits/apply",
            async (url, info, sessionId, output, cancellationToken) =>
                await callback.HandleApply(sessionId, info)
        ),
    ])
{
}

[Injectable]
public class SeasonTraitsRouterCallback(
    ISptLogger<SeasonTraitsRouterCallback> logger,
    HttpResponseUtil httpResponseUtil,
    TraitDataService traitData,
    TraitApplier traitApplier,
    SaveServer saveServer)
{
    /// <summary>Per-profile apply gate: ValidateSelection→ApplySelection→SaveState must be
    /// atomic — two concurrent applies for one profile would both pass validation and mail
    /// grant items twice (audit ML-4). Entries persist per profile (few, single-user server).</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _applyGates = new();

    public ValueTask<string> HandleList(MongoId sessionId)
    {
        return new ValueTask<string>(httpResponseUtil.NoBody(traitData.GetTraits()));
    }

    public ValueTask<string> HandleGetProfile(MongoId sessionId)
    {
        var state = traitData.GetState(sessionId);
        var response = new GetProfileResponse
        {
            Locked = state != null,
            Selected = state?.Selected ?? new List<string>(),
            ProfileReady = ProfileIsComplete(sessionId),
        };
        return new ValueTask<string>(httpResponseUtil.NoBody(response));
    }

    /// <summary>
    /// A login-time SptProfile skeleton exists before the in-game creation flow fills it;
    /// applying traits into the skeleton would persist a broken partial profile.
    /// </summary>
    private bool ProfileIsComplete(MongoId sessionId)
    {
        var pmcData = saveServer.GetProfile(sessionId)?.CharacterData?.PmcData;
        return pmcData?.Info != null
            && pmcData.Skills?.Common?.Any() == true
            && pmcData.Inventory?.Items != null;
    }

    public async ValueTask<string> HandleApply(MongoId sessionId, ApplyRequestData request)
    {
        var gate = _applyGates.GetOrAdd(sessionId.ToString(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
        var selected = request.Selected ?? new List<string>();

        var error = traitData.ValidateSelection(sessionId, selected);
        if (error != null)
        {
            return Rejected(error);
        }

        var profile = saveServer.GetProfile(sessionId);
        var pmcData = profile?.CharacterData?.PmcData;
        if (pmcData == null)
        {
            return Rejected("档案不存在或尚未创建 PMC");
        }
        if (!ProfileIsComplete(sessionId))
        {
            return Rejected("档案创建流程尚未完成，请完成选边与昵称创建后再试");
        }


        var traits = traitData.GetTraits().Traits;
        var state = new ProfileTraitState
        {
            ProfileId = sessionId,
            Selected = selected,
            LockedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        traitApplier.ApplySelection(sessionId, pmcData, state);
        traitData.SaveState(state);

        // persist profile immediately so effects land on disk even before the periodic save
        await saveServer.SaveProfileAsync(sessionId);

        var balance = TraitDataService.ComputeBalance(selected.Select(id => traits.First(t => t.Id == id)));
        logger.Success($"SeasonTraits: profile {sessionId} locked traits [{string.Join(", ", selected)}]");

        // Final-state skill payload (audit M-1): values read back from the applied profile so
        // the client mirror is order-independent and clamp-consistent; "*" never appears —
        // when a wildcard trait is picked, every Common skill is listed at its final progress.
        var common = pmcData.Skills?.Common ?? new List<CommonSkill>();
        List<SkillProgressData> skills;
        if (selected.Any(id => (traits.FirstOrDefault(t => t.Id == id)?.Effects ?? Enumerable.Empty<TraitEffect>())
                .Any(e => e.Type == "skillStartLevel" && e.SkillId == "*")))
        {
            skills = common
                .Select(s => new SkillProgressData { Id = s.Id.ToString(), Progress = s.Progress })
                .ToList();
        }
        else
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in selected)
            {
                foreach (var eff in traits.FirstOrDefault(t => t.Id == id)?.Effects ?? Enumerable.Empty<TraitEffect>())
                {
                    if (eff.Type == "skillStartLevel" && eff.SkillId != null && eff.SkillId != "*")
                    {
                        ids.Add(eff.SkillId);
                    }
                }
            }
            skills = common
                .Where(s => ids.Contains(s.Id.ToString()))
                .Select(s => new SkillProgressData { Id = s.Id.ToString(), Progress = s.Progress })
                .ToList();
        }

        return httpResponseUtil.NoBody(new ApplyResult
        {
            Success = true,
            Selected = selected,
            Balance = balance,
            Bonuses = pmcData.Bonuses?.Where(TraitDataService.HasTraitMarker).ToList() ?? new(),
            Skills = skills,
            Bans = pmcData.Info?.Bans?.ToList() ?? new(),
        });
        }
        finally
        {
            gate.Release();
        }
    }

    private string Rejected(string error)
    {
        return httpResponseUtil.NoBody(new ApplyResult
        {
            Success = false,
            Error = error,
        });
    }
}
