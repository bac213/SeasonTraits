# Repository Guidelines

## Project Overview

SeasonTraits is a two-part SPT (Single Player Tarkov) mod adding a per-profile "season traits" system:

- **Server module** (`SeasonTraits.Server`, net10.0): serves a trait catalog, persists per-profile trait selection, applies server-side effects (skill levels, bonuses, mailed items, flea-market ban, street-tax payout) into a PMC profile, and applies per-profile Harmony-patch effects (armor assort removal, hideout craft-time halving).
- **Client plugin** (`SeasonTraits.Client`, net472 BepInEx): shows a one-shot uGUI trait-selection panel at the main menu, posts the selection to the server, and applies in-raid numeric/combat effects via Harmony patches. It also mirrors server-applied bonuses/skills onto the live session profile so effects land without a restart. A read-only 「特质」 sub-tab is injected into the character screen's Skills/Mastering tab bar to display the selected traits.

There is **no solution file** — the two projects build independently.

## Architecture & Data Flow

```
SeasonTraits.Client (net472 BepInEx, game-side)
   │  GET /seasonTraits/list, /seasonTraits/profile
   │  POST /seasonTraits/apply            (via SPT.Common Http RequestHandler)
   ▼
SeasonTraits.Server (net10.0 SPT module)
   ├─ SeasonTraitsRouter (StaticRouter)  → list / profile / apply
   ├─ TraitDataService  → loads db/traits.json + config.json; persists state
   │                      to user/mods/SeasonTraits/store/<profileId>.json
   ├─ TraitApplier      → writes effects into PmcData (skills/bonuses/mail/ban)
   ├─ Patches           → per-profile Harmony patches (armor assort removal,
   │                      craft-time halving, price/skill-cap adjustments)
   └─ StreetTaxUpdater  → IOnUpdate: periodic rouble payout for street_tax
```

- Server-side effects: `skillStartLevel`, `bonus`, `grantItem` (mailed), `fleaBlock`, `income`, `skillCap`, `traderPrice`, `flavor`.
- Client-side in-raid effects: Harmony patches in `TraitPatches.cs` (movement, fall/fracture/bleed, medkits, keys, stamina, vegetation, allergens, secure container, insurance-screen skip, no-FiR hideout crafts).
- Client `LiveProfileMirror` reflection-writes applied bonuses/skills onto the live profile.

## Key Directories

| Path | Purpose |
|---|---|
| `SeasonTraits.Client/` | BepInEx plugin (net472). Entry `SeasonTraitsPlugin`, all Harmony patches in `TraitPatches.cs`, UI panel in `TraitsPanelUI.cs`. |
| `SeasonTraits.Server/` | SPT server module (net10.0). Router, data service, applier, street-tax, server-side Harmony patches. |
| `SeasonTraits.Server/db/traits.json` | Trait catalog (`id/nameZh/nameEn/kind/points/descriptionZh/icon/effects[]/blocks[]`); `kind` is `positive` or `negative`. |
| `SeasonTraits.Server/config.json` | Street-tax payout settings (`streetTaxPeriodDays`, `streetTaxAmount`). |
| `dist/SeasonTraits/` | Packaged distribution (mirrored into `dist/SeasonTraits.zip`, top-level `SeasonTraits/` folder). |

## Development Commands

Build each project separately (no shared `.sln`):

```bash
dotnet build -c Release SeasonTraits.Server/SeasonTraits.Server.csproj
dotnet build -c Release SeasonTraits.Client/SeasonTraits.Client.csproj
```

Both should complete with **0 errors**. Pre-existing, tolerated warnings: `CS8602` (nullable deref), `CS8714` (nullable generic). Client builds clean (0 warnings).

Deploy the built DLLs to the running install:

```bash
# Client → game BepInEx plugins
cp SeasonTraits.Client/bin/Release/SeasonTraits.Client.dll E:\SPT\BepInEx\plugins\
# Server → SPT server mods
cp SeasonTraits.Server/bin/Release/SeasonTraits.Server/* E:\SPT\SPT_Runtime\user\mods\SeasonTraits\
```

Server must be stopped before overwriting `SeasonTraits.Server.dll` (file lock while running). Restart server after deploy; restart game client to reload the plugin.

## Code Conventions & Common Patterns

**Server (SPT module):**
- DI via `[Injectable]` attribute; lifecycle via `IOnLoad`/`IOnUpdate`; ordering via `OnLoadOrder`/`OnUpdateOrder` priorities (e.g. `TypePriority = OnLoadOrder.PostLoad + 1`, `OnUpdateOrder.InsuranceCallbacks + 500`).
- HTTP via `StaticRouter` subclasses; request/response DTOs are C# `record`s (`ApplyRequestData`, `ApplyResult`, `SkillProgressData`, `ProfileTraitState`, `GetProfileResponse`).
- JSON via `System.Text.Json` (`[JsonPropertyName]`).
- Per-profile state persisted to `store/<profileId>.json`; trait ids tagged on bonuses via `ExtensionData[TraitDataService.TraitMarkerKey]` for idempotent stacking.
- Effects applied in `TraitApplier.ApplyEffect` via a `switch` on `effect.Type`.

**Client (BepInEx/Harmony):**
- Every patch derives from `BasePatch` (abstract): override `GetTargetMethod()` (returns `MethodBase`, usually `AccessTools.PropertyGetter`/`AccessTools.Method`); `BasePatch.Enable()` reflects a static `Prefix`/`Postfix` and applies a shared `Harmony("com.spt.seasontraits.client")`.
- Each patch guards on `TraitEffectsState.Has("<traitId>")` so it only runs for the local profile's selected traits.
- Local-player-only patches (movement) also check `AccessTools.Field(typeof(MovementContext), "_player")?.GetValue(__instance) is Player p && p.IsYourPlayer` to avoid affecting NPCs.
- JSON via `Newtonsoft.Json.Linq` (`JObject`/`JArray`).
- UI built entirely in code (`TraitsPanelUI`), `ScreenSpaceOverlay` canvas, `VerticalLayoutGroup` + `ContentSizeFitter`, custom `WheelScroll` with `AfterMove` re-cull workaround.

## Important Files

| File | Role |
|---|---|
| `SeasonTraits.Server/ModMetadata.cs` | `record ModMetadata : IModMetadata` (SPT mod manifest; ModGuid `com.spt.seasontraits`). |
| `SeasonTraits.Server/SeasonTraitsRouter.cs` | `StaticRouter` + `SeasonTraitsRouterCallback` for `/seasonTraits/list`, `/profile`, `/apply`. |
| `SeasonTraits.Server/TraitDataService.cs` | Loads traits/config, validates selection (points economy, mutual exclusion, once-only lock), persists per-profile state. |
| `SeasonTraits.Server/TraitApplier.cs` | `ApplySelection`/`ApplyEffect`/`AddBonus`/`GrantItem`/`SendItemViaMail`/`AddRoubles`/`ClampSkillCaps`. |
| `SeasonTraits.Server/Patches.cs` | Server-side Harmony patches (trader price, shooter XP, skill caps, armor assort removal, craft-time halving) + `PatchRegistration` (wires static fields, enables patches). |
| `SeasonTraits.Server/Models.cs` | All DTO records + `TraitKind` constants (`positive`/`negative`). |
| `SeasonTraits.Client/SeasonTraitsPlugin.cs` | BepInEx entry: `Awake()` runs `TraitPatches.Patch()`, `Update()` polls menu and shows the panel. |
| `SeasonTraits.Client/TraitPatches.cs` | All in-raid Harmony patches + `BasePatch` base + `Patch()` registration. |
| `SeasonTraits.Client/TraitsPanelUI.cs` | Code-built uGUI trait selection panel. |
| `SeasonTraits.Client/TraitsTab.cs` | Harmony postfix on `SkillsAndMasteringScreen.Awake`: clones the Mastering tab, registers the third 「特质」 sub-tab, owns `TraitsTabController`. |
| `SeasonTraits.Client/TraitsSummaryPanel.cs` | Read-only code-built panel rendered inside the character screen (icons, kind, points, description, net points). |
| `SeasonTraits.Client/TraitsApi.cs` | Cached `RequestHandler` GET helpers for `/seasonTraits/list` and `/seasonTraits/profile`. |
| `SeasonTraits.Client/TraitEffectsState.cs` | Static cache of selected trait ids (`SetSelected`/`Has`). |
| `SeasonTraits.Client/AllergenState.cs` | Client-side allergen roll for the `allergic` trait. |
| `SeasonTraits.Client/LiveProfileMirror.cs` | Reflection-mirrors server bonuses/skills onto the live profile. |

## Runtime/Tooling Preferences

- **Server:** .NET 10 (`net10.0`), `Microsoft.NET.Sdk.Web`, packages `SPTarkov.Common`/`SPTarkov.DI`/`SPTarkov.Server.Core`/`SPTarkov.Reflection` `4.1.2`. SPT host listens on `https://127.0.0.1:6969`.
- **Client:** .NET Framework `net472`, `Microsoft.NETFramework.ReferenceAssemblies` `1.0.3`, references via `HintPath` to `E:\SPT\BepInEx\core` and `E:\SPT\EscapeFromTarkov_Data\Managed` (BepInEx, 0Harmony, SPT.Common, UnityEngine modules, Assembly-CSharp, Comfort, Sirenix.Serialization, Newtonsoft.Json, ItemComponent.Types) — all `Private=false`.
- **Package manager:** NuGet (`dotnet restore` implicit on build). No Node/Bun. Build with `dotnet build -c Release`.
- **Logs:** server → `E:\SPT\SPT_Runtime\user\logs\spt\sptYYYYMMDD.log` (ready marker `服务端已开启，游戏愉快`); client → `E:\SPT\BepInEx\LogOutput.log` (patch markers `SeasonTraits: patched <Type>.<method>`).

## Testing & QA

- **No test framework, no CI, no test files** currently in the repo.
- Verification is manual:
  1. `dotnet build -c Release` both projects → 0 errors.
  2. Start the SPT server; confirm `SeasonTraits: config loaded (periodDays=…, amount=…)` + `SeasonTraits: harmony patches enabled` + `服务端已开启` in the server log.
  3. Start the game; confirm `SeasonTraits: patched <Type>.<method>` lines in `E:\SPT\BepInEx\LogOutput.log`.
  4. In-raid functional checks per trait (movement, fall damage, stamina, insurance-screen skip, etc.).
  5. Open 角色信息 → 技能: the third 「特质」 sub-tab must sit right of 专精 and list the selected traits (`SeasonTraits: traits tab installed`, `SeasonTraits: traits tab rendered N trait(s)`); switching back to 技能/专精 must hide the panel, and re-entering the screen must not duplicate the tab.
- Trait selection is **one-shot per profile** (once locked, `ValidateSelection` returns "该档案已锁定特质，不可更改"). To re-test, wipe/reset the profile or the mod's `store/<profileId>.json`.
