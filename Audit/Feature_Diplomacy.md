# Diplomacy and Alliance Systems — Static Analysis Audit

**Scope:** Data structures (Model), logic (Controller), and persistence for faction relationships.  
**Goal:** Support porting and LLM-overridable offer evaluation.

---

## 1. Data Structure: Relationship Model

### 1.1 Storage Shape

Relationships are **not** a matrix. They are a **per-empire list of relationship objects**:

- **Owner:** `Empire` (each empire owns its outgoing relations).
- **Lookup:** `Relationship[] RelationsMap` indexed by **other empire’s Id minus one** (`index = withEmpire.Id - 1`).
- **Iteration:** `Relationship[] ActiveRelations` (same objects as in the map, used for loops).
- **Pair model:** For empires A and B there are **two** `Relationship` instances: A→B (owned by A) and B→A (owned by B). Treaties are set **bilaterally** via `SignBilateralTreaty` in `Empire_Relationship.cs`.

So the model is: **two Relationship objects per pair**, not a single matrix cell.

### 1.2 Class Diagram (Relationship Model)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Empire (partial – relationship storage)                                     │
├─────────────────────────────────────────────────────────────────────────────┤
│ - RelationsMap: Relationship[]     // index = them.Id - 1                   │
│ - ActiveRelations: Relationship[]  // same refs, for iteration            │
│ - KnownEmpires: SmallBitSet        // known faction IDs                     │
├─────────────────────────────────────────────────────────────────────────────┤
│ + GetRelationsOrNull(Empire): Relationship                                   │
│ + GetRelations(Empire, out Relationship): bool                              │
│ + SignTreatyWith(Empire, TreatyType)                                         │
│ + BreakTreatyWith(Empire, TreatyType)                                        │
│ + SignAllianceWith(Empire) / BreakAllianceWith(Empire)                       │
│ + CreateBilateralRelations(Empire us, Empire them)                           │
└─────────────────────────────────────────────────────────────────────────────┘
                                        │
                                        │ owns 1..*
                                        ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Relationship (Gameplay/Relationship.cs) [StarDataType]                       │
├─────────────────────────────────────────────────────────────────────────────┤
│ Identity & state                                                            │
│   Them: Empire                    // other faction (persisted by ref)       │
│   Known: bool                                                               │
│   Posture: Posture               // Friendly | Neutral | Hostile | Allied   │
├─────────────────────────────────────────────────────────────────────────────┤
│ Trust, fear, war state                                                       │
│   Trust: float                                                               │
│   Threat: float                                                              │
│   TrustUsed, FearUsed: float                                                 │
│   TrustEntries: Array<TrustEntry>   FearEntries: Array<FearEntry>           │
│   AtWar: bool   ActiveWar: War      WarHistory: Array<War>                   │
│   CanAttack: bool   IsHostile: bool                                           │
├─────────────────────────────────────────────────────────────────────────────┤
│ Treaty flags (bilateral; set via Empire.SignTreatyWith/BreakTreatyWith)      │
│   Treaty_OpenBorders, Treaty_NAPact, Treaty_Trade, Treaty_Alliance,           │
│   Treaty_Peace                                                                │
│   Treaty_Trade_TurnsExisted   PeaceTurnsRemaining                            │
│   TurnsAllied, TurnsInNap, TurnsInOpenBorders                                │
├─────────────────────────────────────────────────────────────────────────────┤
│ Anger components                                                             │
│   Anger_FromShipsInOurBorders, Anger_TerritorialConflict,                     │
│   Anger_MilitaryConflict, Anger_DiplomaticConflict, TotalAnger               │
├─────────────────────────────────────────────────────────────────────────────┤
│ Rejection / history (for AI/player offers)                                   │
│   HaveRejected_Alliance, haveRejectedNAPact, HaveRejected_TRADE,            │
│   HaveRejected_OpenBorders, HaveRejectedDemandTech                           │
├─────────────────────────────────────────────────────────────────────────────┤
│ + SetTreaty(Empire us, TreatyType, bool value)                               │
│ + SetAlliance(bool ally, Empire us, Empire them)                             │
│ + PrepareForWar(WarType, Empire) / CancelPrepareForWar()                     │
│ + UpdateRelationship(Empire us, Empire them)                                 │
│ + AdvanceRelationshipTurn(Empire us, Empire them)                            │
└─────────────────────────────────────────────────────────────────────────────┘
         │
         │ ActiveWar when AtWar
         ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ War (AI/StrategyAI/WarGoals/War.cs) [StarDataType]                            │
├─────────────────────────────────────────────────────────────────────────────┤
│   Us, Them: Empire   WarType: WarType   StartDate, EndStarDate                │
│   OurStartingStrength, TheirStartingStrength, ...                            │
│   Score: WarScore    OurRelationToThem: Relationship                         │
├─────────────────────────────────────────────────────────────────────────────┤
│ + GetWarScoreState(): WarState   GetBorderConflictState(Array<Planet>): WarState │
└─────────────────────────────────────────────────────────────────────────────┘
```

Supporting types:

- **TrustEntry / FearEntry:** Turn-based trust/fear cost entries (`TrustEntryType`, `TurnTimer`, `TurnsInExistence`, cost).
- **Offer:** Diplomatic offer (treaties, techs, colonies, artifacts, attitude); used as input to evaluation and acceptance.

---

## 2. Diplomatic States (Enums)

### 2.1 TreatyType (`Gameplay/Relationship.cs`)

| Value          | Meaning            | Notes                                      |
|----------------|--------------------|--------------------------------------------|
| Alliance       | Full alliance      | Implies OpenBorders + NAPact when signing  |
| NonAggression  | NAP                |                                            |
| OpenBorders    | Open borders       |                                            |
| Peace          | Peace treaty       | Time-limited (PeaceTurnsRemaining)         |
| Trade          | Trade agreement    | Treaty_Trade_TurnsExisted tracked          |

### 2.2 Posture (`Gameplay/Posture.cs`)

| Value    | Meaning  |
|----------|----------|
| Friendly |          |
| Neutral  |          |
| Hostile  |          |
| Allied   |          |

### 2.3 WarType (`Ship_Game` root – used by diplomacy/war)

| Value           | Meaning            |
|-----------------|--------------------|
| BorderConflict  | Purge enemies from shared systems |
| ImperialistWar  | Attack all enemy systems         |
| GenocidalWar   |                    |
| DefensiveWar   | Attack border systems            |
| SkirmishWar    | Pirates / events   |
| EmpireDefense  |                    |

### 2.4 WarState (`AI/StrategyAI/WarState.cs`)

Used for war/peace evaluation (e.g. peace offer acceptance):  
`ColdWar`, `LosingBadly`, `LosingSlightly`, `EvenlyMatched`, `WinningSlightly`, `Dominating`, `NotApplicable`.

### 2.5 Offer.Attitude (`AI/EmpireAI/Offer.cs`)

| Value     | Meaning    |
|----------|------------|
| Pleading |            |
| Respectful |          |
| Threaten |            |

---

## 3. Logic & Triggers

### 3.1 Modifying Relations / Treaties

- **Treaty changes:** `Empire.SignTreatyWith(them, type)` / `BreakTreatyWith(them, type)` → `SignBilateralTreaty(them, type, true|false)` → both sides’ `Relationship.SetTreaty(us, type, value)`. No global event; callers are UI, AI goals, and events.
- **Alliance bundle:** `SignAllianceWith(them)` signs Alliance, OpenBorders, NonAggression. Breaking alliance uses `BreakAllianceWith` / `BreakAllianceAndOpenBordersWith`.
- **DeclareWar:** `EmpireAI.DeclareWarOn(them, warType)` (and `DeclareWarFromEvent`, `DeclareWarOnViaCall`):
  - Sets `usToThem.AtWar = true`, `ChangeToHostile()`, `ActiveWar = War.CreateInstance(...)`, `Trust = 0f`.
  - Calls `BreakAllTreatiesWith(them, includingPeace: true)`.
  - Calls `them.AI.GetWarDeclaredOnUs(us, warType)`.
  - Notifications: `ShowWarDeclaredNotification(us, them)` (and optionally `AddDeclareWarViaAllyCall` for ally calls). So “declare war” triggers notification and bilateral state change, not a single global diplomacy event bus.
- **Trust/Fear:** Updated in `Relationship_trust.cs` (e.g. `UpdateTrust`) and via `AddTrustEntry` / `AddAnger*` and direct `Trust`/`Threat` changes in response to offers and actions.

### 3.2 AI Alliance Offer Flow

- **Who proposes:** `Relationship.OfferAlliance(us)` is called from relationship update logic when conditions (trust, treaties, strength, anger, etc.) are met.
- **Who evaluates:** When the **other** empire (e.g. AI) receives an alliance offer, `EmpireAI.AnalyzeOffer(theirOffer, ourOffer, them, attitude)` is used. If `theirOffer.Alliance` is true, it routes to `ProcessAlliance` → `WeCanAllyWithThem` and trust/treaty checks. So the “evaluation” for accepting/refusing an alliance lives in **EmpireAI.DiplomacyOffers** (`ProcessAlliance` and `WeCanAllyWithThem`).

---

## 4. Offer Evaluation Logic (for LLM Overriding)

Entry point for diplomatic offers (including alliance) is **`EmpireAI.AnalyzeOffer`** in `Ship_Game/AI/EmpireAI/EmpireAI.DiplomacyOffers.cs`. Alliance is handled first; then peace; then value-based treaty/trade/tech/colony evaluation with `ProcessQuality(valueToUs, valueToThem, out offerDifferential)` and attitude (Pleading / Respectful / Threaten).

### 4.1 Alliance-Offer Evaluation (crucial for LLM)

Alliance is evaluated only when **they** offered alliance (`theirOffer.Alliance`). Code path: `AnalyzeOffer` → `ProcessAlliance(theirOffer, ourOffer, usToThem, them)` → `WeCanAllyWithThem(them, usToThem, out answer)`.

**Snippet: Alliance routing and ProcessAlliance**

```csharp
// EmpireAI.DiplomacyOffers.cs – AnalyzeOffer
if (theirOffer.Alliance)
    return ProcessAlliance(theirOffer, ourOffer, usToThem, them);
```

```csharp
// EmpireAI.DiplomacyOffers.cs – ProcessAlliance
string ProcessAlliance(Offer theirOffer, Offer ourOffer, Relationship usToThem, Empire them)
{
    string answer;
    if (!theirOffer.IsBlank() || !ourOffer.IsBlank())
    {
        answer = "OFFER_ALLIANCE_TOO_COMPLICATED";
    }
    else if (them.isPlayer 
        && (usToThem.TurnsInOpenBorders < 100 * OwnerEmpire.Universe.P.Pace
            || usToThem.TurnsAbove95 < OwnerEmpire.PersonalityModifiers.TurnsAbove95AllianceTreshold * them.Universe.P.Pace))
    {
        answer = "TREATY_TOO_SOON_REJECT";
    }
    else if (usToThem.AvailableTrust < OwnerEmpire.data.DiplomaticPersonality.Alliance + usToThem.TotalAnger 
        || usToThem.TurnsKnown <= 100 * them.Universe.P.Pace)
    {
        answer = "AI_ALLIANCE_REJECT";
    }
    else if (WeCanAllyWithThem(them, usToThem, out answer))
    {
        usToThem.SetAlliance(true, OwnerEmpire, them);
    }

    return answer;
}

bool WeCanAllyWithThem(Empire them, Relationship usToThem, out string answer)
{
    bool allowAlliance     = true;
    answer                 = "AI_ALLIANCE_ACCEPT";
    const string rejection = "AI_ALLIANCE_REJECT_ALLIED_WITH_ENEMY";
    if (OwnerEmpire.TheyAreAlliedWithOurEnemies(them, out Array<Empire> empiresAlliedWithThem))
    {
        usToThem.AddAngerDiplomaticConflict(OwnerEmpire.PersonalityModifiers.AddAngerAlliedWithEnemy);
        if (!OwnerEmpire.IsPacifist || !OwnerEmpire.IsCunning) // Only pacifist and cunning will ally
        {
            allowAlliance   = false;
            answer          = rejection;
            usToThem.Trust -= 25;
        }
        else
        {
            usToThem.Trust -= 5;
        }
    }

    CheckAIEmpiresResponse(them, empiresAlliedWithThem, OwnerEmpire, allowAlliance);
    return allowAlliance;
}
```

So the “offer evaluation” for alliance is:

- **Reject** if offer is not “blank” (too many other terms) → `OFFER_ALLIANCE_TOO_COMPLICATED`.
- **Reject** if them is player and (open borders too short or trust time above 95 too short) → `TREATY_TOO_SOON_REJECT`.
- **Reject** if `AvailableTrust < Alliance personality threshold + TotalAnger` or `TurnsKnown` too low → `AI_ALLIANCE_REJECT`.
- **Else** run `WeCanAllyWithThem`: if they are allied with our enemies, only Pacifist/Cunning can still ally (and trust is reduced); otherwise reject with `AI_ALLIANCE_REJECT_ALLIED_WITH_ENEMY`.
- **Accept** by calling `usToThem.SetAlliance(true, OwnerEmpire, them)` and returning `AI_ALLIANCE_ACCEPT`.

To override with an LLM, you would replace or wrap this branch (alliance leg of `AnalyzeOffer` / `ProcessAlliance` / `WeCanAllyWithThem`) so the LLM can return accept/reject and the same (or new) answer strings while the rest of the pipeline (e.g. `SetAlliance`, notifications) stays unchanged.

### 4.2 General Offer Quality (value-based evaluation)

Used for non-alliance, non-peace offers (trade, NAP, open borders, tech, colonies, etc.):

```csharp
// EmpireAI.DiplomacyOffers.cs – ProcessQuality
OfferQuality ProcessQuality(float valueToUs, float valueToThem, out float offerDiff)
{
    offerDiff = valueToUs / valueToThem.LowerBound(0.01f);

    if (offerDiff.AlmostEqual(1) && valueToUs > 0)
        return OfferQuality.Fair;

    if (offerDiff > 1.45f) return OfferQuality.Great;
    if (offerDiff > 1.1f)  return OfferQuality.Good;
    if (offerDiff > 0.9f)  return OfferQuality.Fair;
    if (offerDiff > 0.65f) return OfferQuality.Poor;

    return OfferQuality.Insulting;
}

enum OfferQuality
{
    Insulting,
    Poor,
    Fair,
    Good,
    Great
}
```

`valueToUs` / `valueToThem` are computed from tech, artifacts, colonies, and treaty types in `AnalyzeOffer`. Attitude (Pleading / Respectful / Threaten) then decides acceptance and relation changes using this quality and trust/fear thresholds.

---

## 5. Persistence

- **Relationship:** All fields shown in the class diagram that are used for diplomacy (Trust, Threat, treaty flags, anger, war, etc.) are marked `[StarData]` and live on `Relationship`. `Them` is a persisted `Empire` reference (object graph; serializer resolves by reference/ID as per the project’s StarData semantics).
- **Empire:** `RelationsMap` and `ActiveRelations` are `[StarData]` on `Empire`. Lookup at runtime is by **Faction ID**: `GetRelationsOrNull(withEmpire)` uses `withEmpire.Id - 1` as the index into `RelationsMap`. So active treaties and relations are effectively keyed by the other empire’s ID after load.
- **War:** `War` is `[StarDataType]` with `Us`, `Them`, `WarType`, and related fields; stored on `Relationship.ActiveWar` and in `WarHistory`.
- **Treaties:** No separate “Treaty” entity; treaty state is the boolean and counter fields on `Relationship` (e.g. `Treaty_Alliance`, `Treaty_Trade_TurnsExisted`, `PeaceTurnsRemaining`). So “active treaties” are persisted as part of each `Relationship` and referenced by the owning empire and the other empire’s reference (and thus ID in the serialized graph).

---

## 6. Summary Table

| Aspect            | Implementation                                                                 |
|-------------------|-------------------------------------------------------------------------------|
| **Structure**     | Per-empire list of `Relationship` objects; two relations per pair (A→B, B→A). |
| **Lookup**        | `RelationsMap[them.Id - 1]`; iteration via `ActiveRelations`.                 |
| **Trust / Fear**   | `Relationship.Trust`, `Threat`; `TrustEntries` / `FearEntries` for timed costs. |
| **Trade state**   | `Treaty_Trade`, `Treaty_Trade_TurnsExisted`.                                  |
| **War state**     | `AtWar`, `ActiveWar: War`, `CanAttack`, `IsHostile`.                          |
| **Treaty changes**| `Empire.SignTreatyWith` / `BreakTreatyWith` → `Relationship.SetTreaty`.       |
| **DeclareWar**    | `EmpireAI.DeclareWarOn` → set AtWar, create War, break treaties, notify.      |
| **Alliance eval** | `EmpireAI.AnalyzeOffer` → `ProcessAlliance` → `WeCanAllyWithThem`.           |
| **Persistence**   | StarData on `Relationship`, `Empire` (RelationsMap, ActiveRelations); treaties and war stored on Relationship; empire reference by ID in serialized graph. |

---

*Audit completed from static analysis of C# sources; no code was executed.*
