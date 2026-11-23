using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;

using SPTarkov.Server.Core.Servers;

namespace SeasonTraits.Server;

/// <summary>
/// Weekly street_tax payout for profiles holding the trait. Runs on every server tick,
/// pays out only when the configured period has elapsed since the last payout.
/// </summary>
[Injectable(TypePriority = OnUpdateOrder.InsuranceCallbacks + 500)]
public class StreetTaxUpdater(
    ISptLogger<StreetTaxUpdater> logger,
    TraitDataService traitData,
    TraitApplier traitApplier,
    SaveServer saveServer) : IOnUpdate
{
    public async Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        var config = traitData.GetConfig();
        var periodSeconds = config.StreetTaxPeriodDays * 86400;
        if (periodSeconds <= 0 || config.StreetTaxAmount <= 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (profileId, profile) in saveServer.GetProfiles())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            var pmcData = profile?.CharacterData?.PmcData;
            if (pmcData == null)
            {
                continue;
            }
            var state = traitData.GetState(profileId);
            if (state == null || !state.Selected.Contains("street_tax"))
            {
                continue;
            }
            if (state.StreetTaxPaidUpTo <= 0)
            {
                // first run after taking the trait: start the clock, no immediate payout
                state.StreetTaxPaidUpTo = now;
                traitData.SaveState(state);
                continue;
            }
            if (now - state.StreetTaxPaidUpTo < periodSeconds)
            {
                continue;
            }
            try
            {
                traitApplier.AddRoubles(profileId, pmcData, config.StreetTaxAmount);
                state.StreetTaxPaidUpTo = now;
                traitData.SaveState(state);
                // persist the profile right away so a server crash cannot lose the payout
                // (audit supplement: in-memory-only roubles used to wait for the periodic save)
                await saveServer.SaveProfileAsync(profileId);
                logger.Success($"SeasonTraits: street_tax paid {config.StreetTaxAmount}₽ to {profileId}");
            }
            catch (Exception e)
            {
                logger.Error($"SeasonTraits: street_tax payout failed for {profileId}: {e.Message}");
            }
        }
        return true;
    }
}
