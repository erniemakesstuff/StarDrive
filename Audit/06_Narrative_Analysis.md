# Narrative, Text & Events — Static Analysis Report

**Static audit of the StarDrive 4X codebase for LLM-generated content replacement.**  
Focus: String storage, event definitions, and variable injection patterns.

---

## 1. String Storage

### 1.1 Location of Text Assets

| Asset Type | Format | Location | Count/Notes |
|------------|--------|----------|-------------|
| **GameText** | YAML | `game/Content/GameText.yaml` | ~8,200+ entries (auto-generated enum in `GameText.cs`) |
| **Mod Override** | YAML | `Mods/*/Content/GameText.yaml` (optional) | Merged at runtime |
| **Language Variants** | XML (legacy) | `game/Content/Localization/{English,Russian,Spanish,Ukrainian,German}/GameText_*.xml` | Referenced by `LocalizationTool` for export |
| **ShipRoles** | XML | `game/Content/ShipRoles/*.xml` | `<Localization>ID</Localization>` — references GameText ID |
| **PlanetEdicts** | XML | `game/Content/PlanetEdicts/*.xml` | `<Localization>ID</Localization>` |
| **HelpTopics** | XML | `game/Content/HelpTopics/{Lang}/*.xml` | Help content per language |

**Core runtime:** `Ship_Game/Data/Localizer.cs` — loads from `GameText.yaml` via `Localizer.LoadFromYaml()`. No CSV; YAML is the primary format.

### 1.2 Lookup Mechanism

- **`Localizer.Token(int id)`** — Lookup by 1-based index (maps to `GameText` enum).
- **`Localizer.Token(GameText gameText)`** — Lookup via enum.
- **`Localizer.Token(string nameId)`** — Lookup by NameId (e.g. `"NewGame"`).
- **`LocalizedText`** — Struct with `Id`, `String`, `Method` (Id, NameId, RawText, Parse). Lazy-evaluates on `.Text`.

### 1.3 Hardcoding Check — Requires Extraction

| Location | Pattern | Example |
|----------|---------|---------|
| **`NotificationManager.cs:851`** | Raw string concatenation | `"Rebellion on " + beingInvaded.Name + "!"` |
| **`NotificationManager.cs`** | Many notifications use `$"{var} {Localizer.Token(GameText.X)}"` | `invader.data.Traits.Singular + Localizer.Token(GameText.ForcesSpottedIn)` |
| **`RandomEventManager.cs`** | Direct `Localizer.Token(GameText.X)` with concatenation | `$"{planet.Name} {Localizer.Token(message)} {postText}"` |
| **`Volcano.cs`** | Concatenation of tokens | `$"{message}\n{Localizer.Token(GameText.TheEnvironmentSufferedBothPermanent)}"` |
| **`StatTracker.cs:110`** | Raw template | `planet.Owner.data.Traits.Name + " colonized " + planet.Name` |
| **`TreeNode.cs:279`** | `string.Format` with token | `string.Format(new LocalizedText(GameText.ResearchMultiLevelTech).Text, Entry.Level + 1, text)` |

**Verdict:** Most narrative text uses `Localizer.Token(GameText.X)` — already externalized. Hardcoded patterns are limited to:
- Notification message assembly (concatenation in C#)
- A few raw templates like "Rebellion on X!" and "X colonized Y"

---

## 2. Event Definitions

### 2.1 Event Types

| Type | Definition | Storage | Format |
|------|------------|---------|--------|
| **`RandomEvent`** | C# class | **Mixed** — struct in code, spawn logic in C# | `RandomEventManager.cs` — event names/strings in code, `NotificationString` from `[StarData]` |
| **`Encounter`** | C# class | **External** | XML in `Content/Encounter Dialogs/` — `Message`, `Response` with `LocalizedText` or raw `Text` |
| **`ExplorationEvent`** | C# class | **External** | XML in `Content/Exploration Events/` — `Outcome` with `TitleText`, `DescriptionText`, `ConfirmText` |
| **`FederationQuest`** | C# class | **Code + External** | `QuestType` enum in C#; dialog text from DiplomacyDialogs XML via `GetDialogueByName("Federation_Quest_DestroyEnemy")` |
| **`DiplomacyDialog`** | C# class | **External** | XML in `Content/DiplomacyDialogs/{Race}.xml` — `DialogLine` with `Friendly`, `Neutral`, `Hostile`, `DL_Agg`, etc. |
| **`Log.GameEvent`** | Enum | **Static C#** | Analytics only (NewGame, YouWin, etc.) — not narrative |

### 2.2 Structure of Key Event Classes

#### ExplorationEvent

```csharp
// Ship_Game/ExplorationEvent.cs
public sealed class ExplorationEvent
{
    public string Name;
    public string FileName;
    public int StoryStep;
    public Remnants.RemnantStory Story;
    public Array<Outcome> PotentialOutcomes;
    // ...
}
```

#### Outcome (Exploration Event Result)

```csharp
// Ship_Game/StoryAndEvents/Outcome.cs
public sealed class Outcome
{
    public string TitleText;      // Localizer.Token(TitleText) for display
    public string DescriptionText; // Localizer.Token(DescriptionText)
    public string ConfirmText;    // Button text
    public string Image;          // Asset path
    // + grants: MoneyGranted, TroopsToSpawn, UnlockTech, etc.
}
```

- **Storage:** XML. `TitleText`, `DescriptionText`, `ConfirmText` can be raw strings or GameText nameIds.
- **LLM-ready:** Yes — external XML. Replace static strings with LLM prompt + context.

#### Encounter (Diplomacy Popup)

```csharp
// Ship_Game/StoryAndEvents/Encounter.cs
public sealed class Encounter
{
    public int Step;
    public bool FactionInitiated, PlayerInitiated, FirstContact;
    public string Name, Faction, DescriptionText;
    public Array<Message> MessageList;
}

// Message
public sealed class Message
{
    public string Text;           // DEPRECATED
    public string LocalizedText;  // NameId for Localizer.Token
    public Array<Response> ResponseOptions;
}

// Response
public sealed class Response
{
    public string Text, LocalizedText;
    public int MoneyToThem, DefaultIndex, FailIndex, SuccessIndex;
    // ...
}
```

- **Storage:** XML in `Content/Encounter Dialogs/`.
- **LLM-ready:** Yes — external. Replace `LocalizedText` / `Text` with LLM-generated content.

#### DiplomacyDialog (Race-Specific Dialogs)

- **Storage:** XML in `Content/DiplomacyDialogs/Human.xml`, `Cordrazine.xml`, etc.
- **Structure:** `DialogLine` with `DialogType`, `Friendly`, `Neutral`, `Hostile`, `Default`, `DL_Agg`, `DL_Hon`, `DL_Pac`, etc.
- **LLM-ready:** Yes — external. Full dialog trees per race.

#### RandomEvent

```csharp
// Ship_Game/StoryAndEvents/RandomEventManager.cs
[StarDataType]
public sealed class RandomEvent
{
    [StarData] public string Name;
    [StarData] public string NotificationString;
    [StarData] public int TurnTimer;
    [StarData] public bool InhibitWarp;
}
```

- **Spawn logic:** Hardcoded in `TryEventSpawn()` (HyperSpaceFlux, ShiftInOrbit, FoundMinerals, etc.). Event *content* may come from data; *selection* is in code.

---

## 3. Variable Injection (Context Data for LLM)

### 3.1 Parse Patterns

#### LocalizedText.Parse

- **Syntax:** `{TokenName}` or `{123}` — replaced with `Localizer.Token()` result.
- **Example:** `"{RaceNameSingular}: "` → "Human: "
- **Use:** UI labels, tooltips, dynamic composition.

#### Diplomacy / Encounter Keywords

**DiplomacyScreen.ConvertDiplomacyKeyword / EncounterInstance.ParseEncounterKeyword:**

| Keyword | Context Data | Source |
|---------|--------------|--------|
| `SING` | Player race singular | `Player.data.Traits.Singular` / `Us.data.Traits.Singular` |
| `PLURAL` | Player race plural | `Player.data.Traits.Plural` |
| `TARSYS` | System under discussion | `SystemToDiscuss?.Name` / `SysToDiscuss?.Name` |
| `TAREMP` | Target empire name | `TargetEmpire.data.Traits.Name` / `EmpireToDiscuss?.data.Traits.Name` |
| `ADJ1`, `ADJ2` | Race adjectives | `Player.data.Traits.Adj1`, `Adj2` |
| `MONEY` | Custom money demand | `CustomMoneyDemand` |
| `TECH_DEMAND` | Tech being demanded | `OurOffer.TechnologiesOffered[0]` → tech name |

**Context bundle for LLM:** `{ UsSingular, UsPlural, ThemSingular, ThemPlural, TAREMP, TARSYS, EmpireToDiscussName, SysToDiscussName, TechDemanded, Attitude, TreatyState, WarState }`

### 3.2 string.Format / Interpolation Usage

| Location | Pattern | Context Data |
|----------|---------|--------------|
| `TreeNode.cs:279` | `string.Format(token, level, text)` | `Entry.Level`, research text |
| `NotificationManager` | `$"{empire.Name} {Localizer.Token(...)}"` | `empire`, `planet`, `invader`, `conqueror`, `victim`, `attacker`, `absorber`, `beingInvaded` |
| `Volcano.cs` | `$"{planet.Name} {Localizer.Token(...)}"` | `planet`, `numLavaPoolsCreated`, `increaseBy` |
| `SolarSystemBody.cs` | `$"{Name} {Localizer.Token(...)}"` | `Name`, `MineralRichness` |
| `Planet.cs` | `$"{Name} {Localizer.Token(...)}"` | `Name`, `MaxPopulationBillion` |

### 3.3 Notification Context Data Summary

For each notification type, the code has access to:

| Notification Type | Context Vars |
|-------------------|--------------|
| Invasion | `beingInvaded` (SolarSystem), `invader` (Empire), `strRatio` |
| Colonized | `wasColonized` (Planet), `emp` (Empire) |
| Capital Transfer | `from`, `to` (Planet) |
| Treaty Break | `empire`, `type` (TreatyType) |
| Planet Captured | `p` (Planet), `conqueror`, `loser` (Empire) |
| Troops Landing | `invader`, `where` (Planet) |
| Anomaly Revealed | `p` (Planet) |
| Empire Merged | `absorber`, `target` (Empire) |
| War Declared | `attacker`, `victim` (Empire) |
| Rebellion | `beingInvaded` (Planet) |

These should be passed to the LLM as structured context (e.g. JSON) when generating notification text.

---

## 4. Output Deliverables Summary

### 4.1 Location of Text Assets

| Storage | Location | Notes |
|---------|----------|-------|
| **Code** | `Ship_Game/Data/GameText.cs` | Auto-generated enum; do not edit directly |
| **External (Primary)** | `game/Content/GameText.yaml` | 8K+ entries; Id, NameId, ENG, RUS, SPA, UKR, GER |
| **External (Dialogs)** | `game/Content/DiplomacyDialogs/*.xml` | Per-race dialog trees |
| **External (Encounters)** | `game/Content/Encounter Dialogs/*.xml` | Step-based encounter flows |
| **External (Exploration)** | `game/Content/Exploration Events/*.xml` | Event name, outcomes, TitleText, DescriptionText |
| **External (Help)** | `game/Content/HelpTopics/{Lang}/*.xml` | Help content |

### 4.2 Event Class Structures

- **ExplorationEvent** — Name, StoryStep, PotentialOutcomes (Outcome[]). Outcomes have TitleText, DescriptionText, ConfirmText, Image, grants.
- **Encounter** — Step, MessageList (Message[]). Message has LocalizedText, ResponseOptions (Response[]). Response has LocalizedText, SuccessIndex, FailIndex.
- **FederationQuest** — type (QuestType), EnemyName. Dialog text via GetDialogueByName.
- **RandomEvent** — Name, NotificationString. Spawn rules in C#.
- **DiplomacyDialog** — Dialogs (DialogLine[]). DialogLine has DialogType, Friendly, Neutral, Hostile, Default, DL_* (personality variants).

### 4.3 Hardcoded Narrative Systems — Requires Rewrite

| System | Location | Issue |
|--------|----------|-------|
| **Notification message assembly** | `NotificationManager.cs` | ~40+ `Add*Notification` methods build messages via string concatenation. Templates are implicit in code. |
| **RandomEvent notifications** | `RandomEventManager.cs` | `NotifyPlayerIfAffected` uses `$"{planet.Name} {Localizer.Token(message)} {postText}"`. Template is in code. |
| **Rebellion notification** | `NotificationManager.cs:851` | `"Rebellion on " + beingInvaded.Name + "!"` — fully hardcoded. |
| **StatTracker event log** | `StatTracker.cs:110` | `planet.Owner.data.Traits.Name + " colonized " + planet.Name` — hardcoded template. |
| **Diplomacy keyword injection** | `DiplomacyScreen.cs`, `EncounterInstance.cs` | Keywords (SING, PLURAL, TARSYS, TAREMP) are fixed. LLM would need equivalent context schema. |

### 4.4 LLM Integration Recommendations

1. **GameText.yaml** — Keep as fallback. Add optional `LLMPrompt` or `LLMContextKeys` for entries that support dynamic generation.
2. **Exploration Events** — Add `LLMContextSchema` to Outcome. Pass `{ Planet, Empire, EventName, OutcomeIndex }`; generate TitleText, DescriptionText, ConfirmText.
3. **Diplomacy Dialogs** — Replace static XML lines with LLM calls. Context: `{ Us, Them, DialogType, Attitude, SysToDiscuss, EmpireToDiscuss }`. Map existing keywords to context fields.
4. **Notifications** — Introduce `NotificationTemplate` with placeholders. Resolve at runtime via LLM or templating; pass context (Empire, Planet, TreatyType, etc.).
5. **Encounter Dialogs** — Same as Diplomacy; Message.Text / LocalizedText → LLM output with EncounterInstance context.

---

*Audit performed via static code analysis. Engine: StarDrive (XNA/MonoGame).*
