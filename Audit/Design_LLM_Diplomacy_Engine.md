# LLM-Driven Diplomacy & History Engine — Design Specification

**Context:** Rewriting the engine. Legacy uses BinarySerializer + Key-Value localization. Target: ECS architecture with LLM-driven history and diplomacy.

**References:** `Feature_Diplomacy.md`, `05_Persistence_Analysis.md`, `06_Narrative_Analysis.md`

---

## 1. DiplomaticHistory.json — JSON Schema

The legacy game does **not** explicitly persist diplomatic events as history. Trust, Threat, and WarHistory exist, but events like "War Declared," "Treaty Broken," "Peace Signed" are not logged as first-class entries. This schema adds an explicit event log for LLM context and narrative generation.

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "$id": "https://stardrive.example/schemas/DiplomaticHistory.json",
  "title": "DiplomaticHistory",
  "description": "JSON-based diplomatic event history, stored as sidecar to save file (Persistence_Analysis Option B)",
  "type": "object",
  "required": ["version", "saveId", "starDate", "events"],
  "properties": {
    "version": { "type": "integer", "const": 1 },
    "saveId": { "type": "string", "description": "Links to main save file (e.g. save name or path)" },
    "starDate": { "type": "number", "description": "Current game star date (for temporal queries)" },
    "events": {
      "type": "array",
      "items": { "$ref": "#/definitions/DiplomaticEvent" }
    }
  },
  "definitions": {
    "DiplomaticEvent": {
      "type": "object",
      "required": ["id", "type", "starDate", "empireIds"],
      "properties": {
        "id": { "type": "string", "format": "uuid" },
        "type": {
          "type": "string",
          "enum": [
            "WarDeclared",
            "PeaceSigned",
            "TreatySigned",
            "TreatyBroken",
            "AllianceFormed",
            "AllianceBroken",
            "ColonyCaptured",
            "InvasionStarted",
            "Colonized",
            "FirstContact",
            "DemandRejected",
            "OfferAccepted",
            "OfferRejected"
          ]
        },
        "starDate": { "type": "number" },
        "empireIds": {
          "type": "array",
          "items": { "type": "integer" },
          "minItems": 1,
          "description": "Order matters: actor, target, optional third (e.g. ally)"
        },
        "planetId": { "type": "integer", "description": "Optional, for ColonyCaptured, Colonized" },
        "systemId": { "type": "integer", "description": "Optional, for InvasionStarted" },
        "treatyType": {
          "type": "string",
          "enum": ["Alliance", "NonAggression", "OpenBorders", "Trade", "Peace"]
        },
        "warType": {
          "type": "string",
          "enum": ["BorderConflict", "ImperialistWar", "GenocidalWar", "DefensiveWar", "SkirmishWar", "EmpireDefense"]
        },
        "offerType": { "type": "string", "description": "Alliance, Peace, NAPact, Trade, etc." },
        "attitude": { "type": "string", "enum": ["Pleading", "Respectful", "Threaten"] },
        "llmNarrative": {
          "type": "string",
          "description": "Optional LLM-generated one-line summary for UI/history screen"
        }
      }
    }
  }
}
```

### Example `DiplomaticHistory.json` instance

```json
{
  "version": 1,
  "saveId": "AutoSave_001",
  "starDate": 42.5,
  "events": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "type": "WarDeclared",
      "starDate": 38.2,
      "empireIds": [2, 1],
      "warType": "ImperialistWar",
      "llmNarrative": "The Kulrathi Federation declared war on the United Federation, citing territorial encroachment in the Tarsys system."
    },
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "type": "TreatyBroken",
      "starDate": 40.1,
      "empireIds": [2, 1],
      "treatyType": "NonAggression",
      "llmNarrative": "The Kulrathi abrogated the non-aggression pact without warning."
    }
  ]
}
```

---

## 2. LLMDiplomacyContext — C# Struct

Payload for the `DiplomacyEvaluationSystem` to send to the LLM. All fields are **serializable** (primitive or DTO) for JSON/HTTP.

```csharp
namespace StarDrive.ECS.Diplomacy
{
    /// <summary>
    /// Context data copied from ECS World and Relationship state for LLM evaluation.
    /// Immutable snapshot; no references to live game objects.
    /// </summary>
    public readonly struct LLMDiplomacyContext
    {
        // ---- Identity ----
        public int UsEmpireId { get; }
        public int ThemEmpireId { get; }
        public string UsEmpireName { get; }
        public string ThemEmpireName { get; }
        public string UsPersonality { get; }   // Aggressive, Honorable, Pacifist, etc.
        public string ThemPersonality { get; }

        // ---- Relationship State (from Relationship) ----
        public float Trust { get; }
        public float Threat { get; }
        public float TotalAnger { get; }
        public float Anger_Territorial { get; }
        public float Anger_Military { get; }
        public float Anger_Diplomatic { get; }
        public float Anger_ShipsInBorders { get; }

        public bool Treaty_Alliance { get; }
        public bool Treaty_NAPact { get; }
        public bool Treaty_Trade { get; }
        public bool Treaty_OpenBorders { get; }
        public bool Treaty_Peace { get; }
        public int PeaceTurnsRemaining { get; }
        public int TurnsKnown { get; }
        public int TurnsAllied { get; }
        public int TurnsInOpenBorders { get; }
        public int TurnsAbove95 { get; }

        public bool AtWar { get; }
        public string WarType { get; }         // null if not at war
        public string WarState { get; }        // ColdWar, LosingBadly, Dominating, etc.

        public bool HaveRejected_Alliance { get; }
        public bool HaveRejected_NAPact { get; }
        public bool HaveRejected_TRADE { get; }
        public bool HaveRejected_OpenBorders { get; }

        public float AvailableTrust { get; }   // Trust - TrustUsed
        public int NumTechsWeGave { get; }

        // ---- Offer (from DiplomacyRequest) ----
        public bool TheirOffer_Alliance { get; }
        public bool TheirOffer_Peace { get; }
        public bool TheirOffer_NAPact { get; }
        public bool TheirOffer_Trade { get; }
        public bool TheirOffer_OpenBorders { get; }
        public string[] TheirOffer_Technologies { get; }
        public string[] TheirOffer_Colonies { get; }
        public string[] OurOffer_Technologies { get; }
        public string[] OurOffer_Colonies { get; }
        public string Attitude { get; }        // Pleading | Respectful | Threaten

        // ---- Strength / Threat ----
        public float OurMilitaryStrength { get; }
        public float TheirMilitaryStrength { get; }
        public bool TheyAreAlliedWithOurEnemies { get; }
        public int NumEmpiresAlliedWithThem { get; }

        // ---- History (from DiplomaticHistory.json) ----
        public LLMDiplomaticEvent[] RecentEvents { get; }  // Last N events involving Us or Them
    }

    public readonly struct LLMDiplomaticEvent
    {
        public string Type { get; }      // WarDeclared, TreatyBroken, etc.
        public float StarDate { get; }
        public int ActorEmpireId { get; }
        public int TargetEmpireId { get; }
        public string LlmNarrative { get; }
    }
}
```

---

## 3. Injection Points: Legacy → New ECS

### 3.1 Legacy Entry Point

| Legacy | New ECS Equivalent |
|--------|-------------------|
| `EmpireAI.AnalyzeOffer(theirOffer, ourOffer, them, attitude)` | `DiplomacyEvaluationSystem` processes `DiplomacyRequest` component |

### 3.2 DiplomacyRequest Component (Payload)

```csharp
/// <summary>
/// ECS component: Request for diplomacy evaluation (offer acceptance/rejection).
/// Added when an AI empire must evaluate an incoming offer.
/// </summary>
public struct DiplomacyRequest : IComponentData
{
    public Entity UsEmpire;
    public Entity ThemEmpire;
    public Entity RelationshipEntity;   // Or use Us/Them to look up
    public OfferDTO TheirOffer;
    public OfferDTO OurOffer;
    public OfferAttitude Attitude;
    public Entity OutputEntity;         // Write Accept/Reject + AnswerString here
}

public struct OfferDTO
{
    public bool Alliance;
    public bool PeaceTreaty;
    public bool NAPact;
    public bool TradeTreaty;
    public bool OpenBorders;
    public FixedList64Bytes<int> TechnologiesOffered;   // Tech UIDs
    public FixedList64Bytes<int> ColoniesOffered;       // Planet IDs
    public FixedList64Bytes<int> ArtifactsOffered;
}
```

### 3.3 DiplomacyEvaluationSystem Flow

```
1. Query: Entities with DiplomacyRequest
2. For each request:
   a. Read Relationship, Empire components for Us, Them
   b. Read DiplomaticHistory for RecentEvents
   c. Build LLMDiplomacyContext (struct)
   d. [SYNC or ASYNC] Send to LLM service
   e. Parse response → Accept | Reject, AnswerString
   f. Write to OutputEntity (DiplomacyResult)
   g. Remove DiplomacyRequest
3. Other systems consume DiplomacyResult to execute (SignTreaty, etc.)
```

### 3.4 Data to Copy from ECS → LLMDiplomacyContext

| Source | Fields |
|--------|--------|
| `Relationship` | Trust, Threat, TotalAnger, Anger_*, Treaty_*, TurnsKnown, TurnsAllied, AtWar, WarType, WarState, HaveRejected_*, AvailableTrust, NumTechsWeGave |
| `Empire` | Name, DiplomaticPersonality.Name, CurrentMilitaryStrength |
| `DiplomaticHistory` | RecentEvents (filter by Us/Them, last 10–20) |
| `Offer` (Their/Our) | Alliance, Peace, NAPact, Trade, OpenBorders, TechnologiesOffered, ColoniesOffered |
| `Attitude` | Pleading, Respectful, Threaten |

---

## 4. Template System for Notification Text

### 4.1 Problem (from Narrative Analysis)

- `NotificationManager` uses `$"{var} {Localizer.Token(GameText.X)}"` and raw concatenation.
- Example: `"Rebellion on " + beingInvaded.Name + "!"`
- LLM needs a way to inject variations without hardcoding.

### 4.2 Proposed: SmartFormat (or Handlebars.NET)

**SmartFormat** supports named placeholders and conditional formatting. Example:

```
Template: "{{InvaderName}} forces spotted in {{SystemName}} system. Threat level: {{ThreatLevel}}."
Context:  { InvaderName: "Kulrathi", SystemName: "Tarsys", ThreatLevel: "High" }
Output:   "Kulrathi forces spotted in Tarsys system. Threat level: High."
```

**Handlebars.NET** alternative:

```
Template: "{{InvaderName}} forces spotted in {{SystemName}} system. Threat: {{ThreatLevel}}."
Context:  { "InvaderName": "Kulrathi", "SystemName": "Tarsys", "ThreatLevel": "High" }
```

### 4.3 Notification Template Registry

```csharp
// YAML or JSON config
NotificationTemplates:
  BeingInvaded:
    Fallback: "{InvaderName} forces spotted in {SystemName} system. Threat: {ThreatLevel}."
    ContextKeys: [InvaderName, SystemName, ThreatLevel]
    LLMPrompt: "Generate a short, urgent military alert. Empire: {InvaderName}. System: {SystemName}. Threat: {ThreatLevel}."
  WarDeclared:
    Fallback: "{AttackerName} and {VictimName} are now at war."
    ContextKeys: [AttackerName, VictimName]
  Rebellion:
    Fallback: "Rebellion on {PlanetName}!"
    ContextKeys: [PlanetName]
  Colonized:
    Fallback: "{PlanetName} was colonized. Click for colony screen."
    ContextKeys: [PlanetName]
```

### 4.4 Resolution Flow

1. Notification triggered with context dict: `{ "InvaderName": invader.data.Traits.Name, "SystemName": system.Name, "ThreatLevel": "High" }`
2. Look up template by notification type.
3. If LLM enabled: send context + template.LLMPrompt to LLM → use result.
4. Else: `SmartFormat.Format(template.Fallback, context)` or `Handlebars.Compile(template.Fallback)(context)`.
5. Display result.

### 4.5 Package Recommendation

| Library | Pros | Cons |
|---------|------|------|
| **SmartFormat.NET** | Localized pluralization, conditional, list formatting | Slightly heavier |
| **Handlebars.NET** | Familiar Mustache syntax, lightweight | No pluralization built-in |
| **Nustache** | Simple | Less maintained |

**Recommendation:** **SmartFormat** for notifications — supports `{ThreatLevel:choose(High|Medium|Low):...}` style conditionals for localization-friendly fallbacks.

---

## 5. File Layout (New Engine)

```
Assets/
  Diplomacy/
    DiplomaticHistory.json          # Sidecar, loaded with save
    NotificationTemplates.yaml      # Template + ContextKeys + LLMPrompt
Schemas/
  DiplomaticHistory.schema.json     # JSON Schema for validation
ECS/
  Diplomacy/
    LLMDiplomacyContext.cs          # Struct
    DiplomacyRequest.cs             # IComponentData
    DiplomacyResult.cs              # IComponentData
    DiplomacyEvaluationSystem.cs    # System
    HistoryEventWriterSystem.cs     # Appends to DiplomaticHistory on War/Treaty events
```

---

*Design spec for LLM-driven diplomacy engine. Compatible with Persistence_Analysis Option B (JSON sidecar) and Feature_Diplomacy relationship model.*
