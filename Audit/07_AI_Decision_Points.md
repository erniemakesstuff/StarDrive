# AI Decision Points — Strategic Layer (Diplomacy & Faction Management)

**Static audit for replacing deterministic AI with async LLM-driven decisions.**  
Focus: Decision hotspots, memory model, and control structure.

---

## 1. Decision Hotspots (Strategic Layer)

### 1.1 Diplomacy — Offer Evaluation & Response

| Location | Method | Logic type | Notes |
|----------|--------|------------|--------|
| **`Ship_Game/AI/EmpireAI/EmpireAI.DiplomacyOffers.cs`** | **`AnalyzeOffer`** | Deterministic | **Primary diplomacy entry.** Computes `valueToUs`, `valueToThem`, `totalTrustRequiredFromUs`; then `ProcessQuality(valueToUs, valueToThem)` → `OfferQuality` (Insulting/Poor/Fair/Good/Great). Branching: `switch(attitude)` (Pleading / Respectful / Threaten) and `switch(offerQuality)`. Returns response string and may call `AcceptOffer` / `ImproveRelations` / `DamageRelationship`. **Ideal candidate for `await LLM_Bridge.AskDecision()`** — single entry, clear input (offers + empire context), output = response key + accept/reject. |
| Same file | **`ProcessAlliance`** | Threshold-based | Checks `theirOffer.IsBlank()`, `TurnsInOpenBorders`, `TurnsAbove95`, `AvailableTrust`, `WeCanAllyWithThem()`. Returns rejection string or sets alliance. |
| Same file | **`ProcessPeace`** | Deterministic | Calls **`AnalyzePeaceOffer`**; uses `WarState`, `WarType`, `OfferQuality` in nested switches to return accept/reject. |
| Same file | **`AnalyzePeaceOffer`** | Deterministic | Builds `valueToUs` / `valueToThem` from techs, colonies, artifacts; `ProcessQuality`; then `switch(ActiveWar.WarType)` (BorderConflict, DefensiveWar, ImperialistWar) and `switch(state)` (EvenlyMatched, Dominating, ColdWar, LosingBadly, etc.) to set **`PeaceAnswer`** (string + bool Peace). |
| Same file | **`ProcessQuality`** | Pure math | `offerDiff = valueToUs / valueToThem`; returns Great/Good/Fair/Poor/Insulting from thresholds (e.g. >1.45 → Great, >0.9 → Fair). |

**Call sites for `AnalyzeOffer`:**

- **`Ship_Game/GameScreens/DiplomacyScreen/DiplomacyScreen.cs`** — `DoNegotiationResponse(Them.AI.AnalyzeOffer(OurOffer, TheirOffer, Us, Attitude))` (player vs AI).
- **`Ship_Game/Gameplay/Relationship.cs`** — Multiple: AI responding to player offers (open borders, trade, peace, demand tech, etc.) with `them.AI.AnalyzeOffer(...)`.

**Logic check:** Decisions are **deterministic math + thresholds** (trust, value ratios, anger, personality modifiers). No randomness in the decision itself; only in prior state (trust gain, etc.). Easy to describe as “if relation/trust/value in range X then outcome Y” and replace with an LLM call that returns the same outcome contract.

---

### 1.2 War Declaration & Preparation

| Location | Method | Logic type | Notes |
|----------|--------|------------|--------|
| **`Ship_Game/AI/EmpireAI/EmpireAI.RunDiplomaticPlanner.cs`** | **`RunDiplomaticPlanner`** | Personality switch | `switch(OwnerEmpire.Personality)` → `DoConservativeRelations()` / `DoAggressiveRelations()` / `DoRuthlessRelations()` / `DoXenophobicRelations()`. Each iterates `AllRelations` and calls relationship behavior (e.g. `rel.DoConservative(OwnerEmpire, rel.Them, out theyArePotentialTargets)`), then **`PrepareToAttackClosest`** / **`PrepareToAttackWeakest`** / **`PrepareToAttackXenophobic`**. |
| Same file | **`PrepareToAttackClosest`** | Deterministic | `!IsAtWarWithMajorEmpire && potentialTargets.Count > 0 && TotalEnemiesStrength() * 1.5f < OffensiveStrength` → pick **closest** by distance; if `closest.CurrentMilitaryStrength * 1.5f < OffensiveStrength` → **`usToThem.PrepareForWar(WarType.ImperialistWar, OwnerEmpire)`**. |
| Same file | **`PrepareToAttackWeakest`** | Deterministic | Same preconditions; pick **weakest** by `CurrentMilitaryStrength`; then **`PrepareForWar(WarType.ImperialistWar, OwnerEmpire)`**. |
| Same file | **`PrepareToAttackXenophobic`** | Deterministic | Same idea; **`PrepareForWar(WarType.GenocidalWar, OwnerEmpire)`**. |
| **`Ship_Game/Gameplay/Relationship.cs`** | **`DoConservative`** / **`DoRuthless`** / **`DoAggressive`** / **`DoXenophobic`** | State + personality | `switch(Posture)` (Friendly / Neutral / Hostile) and personality; set **`theyArePotentialTargets`** via e.g. `TheyArePotentialTargetAggressive(us, them)`. These determine who enters `potentialTargets` for the PrepareToAttack* methods. |
| **`Ship_Game/AI/EmpireAI/EmpireAI.RunWarPlanner.cs`** | **`DeclareWarOn`** | Execution | Actually declares war (sets `AtWar`, `ActiveWar`, breaks treaties). The **decision** to declare is upstream: **PrepareForWar** goal and the RunDiplomaticPlanner logic above. |
| **`Ship_Game/Commands/Goals/PrepareForWar.cs`** | **`PrepareForWar`** (goal) | Goal lifecycle | Goal runs until conditions met; then triggers **`DeclareWarOn`**. Decision to *start* preparing is in RunDiplomaticPlanner. |

**Logic check:** “Decide war” is **deterministic**: strength ratios, distance, personality, posture. No `DecideWar` method by name; the decision is **PrepareToAttack* + PrepareForWar**. Replacing with LLM would mean: (1) replace **PrepareToAttackClosest/Weakest/Xenophobic** (or the choice of which to call and on whom) with a single “should we prepare war, and on whom?” LLM call, or (2) inject at **Relationship.Do*** level so “theyArePotentialTargets” comes from LLM.

---

### 1.3 Research Choice

| Location | Method | Logic type | Notes |
|----------|--------|------------|--------|
| **`Ship_Game/AI/Research/ChooseTech.cs`** | **`PickResearchTopic`** | Scripted or priority-based | `ScriptType` = Scripted (follows `Strategy.TechPath`) or Random. Random path uses **`ResearchPriorities`** (Wars, Economics, ResearchDebt, etc.) and **`ScriptedResearch(command, "RANDOM", GetPriorities().TechCategoryPrioritized)`**. |
| **`Ship_Game/AI/EmpireAI/EmpireAI.RunResearchPlanner.cs`** | **`RunResearchPlanner`** | Entry | Calls **`TechChooser.PickResearchTopic(command)`**. |
| **`Ship_Game/AI/Research/ResearchPriorities.cs`** | Priorities | Deterministic | Scores for tech categories from empire state (wars, economy, etc.). |

**Logic check:** **Deterministic**: scripted path or cost/priority scoring. No `ChooseResearch` by name; the choice is **PickResearchTopic** (and inside it, script or random with priorities). Good candidate for `await LLM_Bridge.AskDecision()` for “next tech” given current priorities and tree.

---

### 1.4 Relationship State & GetRelationship

- **GetRelationship:** No single `GetRelationship` method; code uses **`Empire.GetRelations(other)`** (e.g. **`Ship_Game/Empire_Relationship.cs`** / relationship lookups). Returns **`Relationship`** which holds Trust, Anger, treaties, WarHistory, etc.
- Relationship **updates** (trust gain, anger, posture) are computed in **`Relationship_trust.cs`**, **`Relationship.cs`** (e.g. **UpdateTrust**, **AddAnger***, **ChangeToFriendlyIfPossible**). These are deterministic formulas (personality, pace, thresholds).

**Summary of hotspots for LLM override**

- **Diplomacy response:** **`EmpireAI.DiplomacyOffers.cs`** → **`AnalyzeOffer`** (and optionally **`AnalyzePeaceOffer`** / **`ProcessAlliance`**).
- **War preparation (who to attack):** **`EmpireAI.RunDiplomaticPlanner.cs`** → **`PrepareToAttackClosest`** / **`PrepareToAttackWeakest`** / **`PrepareToAttackXenophobic`**, and/or **`Relationship.cs`** → **`DoConservative`** / **`DoAggressive`** / **`DoRuthless`** / **`DoXenophobic`** (theyArePotentialTargets).
- **Research:** **`AI/Research/ChooseTech.cs`** → **`PickResearchTopic`**.

---

## 2. Memory Model — Does the AI Have “Memory”?

### 2.1 What Exists (Structured State)

The game does **not** use a generic “event log” or “narrative history” list. It does have **structured relationship and war state** that functions as memory:

| Location | Data | Description |
|----------|------|-------------|
| **`Ship_Game/Gameplay/Relationship.cs`** | **`Relationship`** | **WarHistory** (`Array<War>`), **TrustEntries** / **FearEntries** (with TurnTimer, TrustCost/FearCost, Type), **Trust**, **TrustUsed**, **Anger_*** (ships in borders, territorial, military, diplomatic), **TurnsKnown**, **TurnsAbove95**, **TurnsInNap**, **TurnsInOpenBorders**, **TurnsAllied**, **NumTechsWeGave**, **turnsSinceLastContact**, **TurnsSinceLastTechTrade** / **TurnsSinceLastTechDemand**, **TurnsSinceLastThreathened**, **StolenSystems**, **WarnedSystemsList**, **HaveRejected_*** (NAPact, TRADE, OpenBorders, Alliance, DemandTech), **HaveInsulted_Military**, **HaveComplimented_Military**, **HaveWarnedTwice/Thrice**. |
| Same | **`War`** (ActiveWar / WarHistory entries) | Start/End dates, WarType, contested systems, etc. |
| **`Ship_Game/Gameplay/Relationship_trust.cs`** | Trust gain | Uses **WarHistory.Count** (e.g. trust gain reduced by past wars). |

So: **AI “memory” exists** in the form of **Relationship + War state**: past wars, trust/fear entries, anger, treaty durations, rejection flags, and stolen systems. Enough to build a **summary for an LLM** (e.g. “We have been at war 2 times; trust 45; they rejected NAP last 20 turns ago; we have trade treaty 50 turns”).

### 2.2 What Is Absent

- No **tradeHistory** list (e.g. “we traded tech X on date Y”); trade is reflected in treaties and **NumTechsWeGave** / **TurnsSinceLastTechTrade**.
- No **generic event list** (e.g. “Empire A declared war on B”, “Player offered peace”) that is explicitly stored for AI narrative. Notifications exist for the player but are not a dedicated AI memory store.

### 2.3 Recommendation

- **Short term:** Use existing **Relationship** (and **War** / **WarHistory**) to build a **compact context string** for LLM (trust, anger, treaties, last war count, key rejection flags, threat). No new persistence required.
- **Long term:** If you want richer narrative context, add a small **“History System”**: append-only list of **high-level events** (war declared/ended, treaty signed/broken, offer accepted/rejected, demand made) per pair or per empire, with a cap (e.g. last 50 events). That would **feed the LLM** without replacing current logic; current logic can remain as fallback or for non-LLM empires.

**Conclusion:** **AI memory exists** in Relationship + War state. It is sufficient for a first LLM integration. A dedicated **History System** is **optional** for better narrative context.

---

## 3. State Machine vs Behavior Tree

### 3.1 Ship-Level AI (Tactical / Operational)

- **Control:** **Simple enum state machine.**
- **File:** **`Ship_Game/AI/ShipAI/AIState.cs`** — **`enum AIState`** (DoNothing, Combat, HoldPosition, AwaitingOrders, AttackTarget, Escort, SystemTrader, Orbit, Colonize, MoveTo, Bombard, AssaultPlanet, Explore, Resupply, Refit, Scrap, etc.).
- **Usage:** **`Ship.AI.State`** is read/written across movement, combat, trade, and UI. No BehaviorTree found; transitions are driven by **ShipAI** logic (goals, orders, combat).

### 3.2 Empire-Level AI (Strategic)

- **Control:** **Goal list + personality-driven branches.** No single “state enum” for empire.
- **Goals:** **`EmpireAI.GoalsList`** (**`Ship_Game/AI/Goal.cs`** — **GoalType** enum includes PrepareForWar, EmpireDefense, BuildShips, etc.). Goals are updated in **EmpireAI** (e.g. **EmpireAI.cs** Run* methods).
- **Flow:** **`EmpireAI.cs`** runs **RunDiplomaticPlanner()**, **RunResearchPlanner()**, and **RunWarPlanner()** (and other planners). Diplomacy uses **personality switch** → **Do*Relations()** → **Relationship.Do*()** → **PrepareToAttack***. So: **personality-based branching + goal list**, not a classic FSM or BehaviorTree.

**Conclusion:** **Enum state (ship)** and **goal list + personality switch (empire)**. No BehaviorTree. Overriding with async LLM does not require changing a BT; it requires replacing the **decision methods** that currently compute outcomes synchronously.

---

## 4. Complexity of Overriding with Async Web Request

| Decision point | Complexity | Notes |
|----------------|------------|--------|
| **AnalyzeOffer** | **Medium–High** | Single entry, clear I/O. Callers (**DiplomacyScreen**, **Relationship**) are **synchronous**. To use **`await LLM_Bridge.AskDecision()`**: (1) Make **AnalyzeOffer** async and return same contract (response string + side effects), or (2) Make callers async and propagate async up (DiplomacyScreen is UI-driven; Relationship is called from many places). (3) Serialize **Offer** + **Empire** context (relationship summary, trust, anger) to prompt; parse LLM response to one of the existing response keys (or extend key set). (4) Consider **timeout/fallback** to current deterministic logic if LLM is slow or down. |
| **Peace / Alliance** | **Medium** | Same as AnalyzeOffer if handled inside **AnalyzeOffer**; otherwise separate async methods with same async propagation issues. |
| **Prepare war (who to attack)** | **Medium** | Decision is in **RunDiplomaticPlanner** (PrepareToAttack* and Relationship.Do*). Options: (1) **Async “should we prepare war, on whom?”** before calling PrepareForWar — requires one async point per empire per tick (or per relation), or (2) **Deferred decision**: set a “pending LLM war decision” and resolve next frame/turn. RunDiplomaticPlanner is called from **EmpireAI** update loop; making it async would require that loop to support async (e.g. queue decisions and apply when ready). |
| **Research (PickResearchTopic)** | **Medium** | Single entry (**RunResearchPlanner** → **PickResearchTopic**). If research is chosen once per “topic finished” or similar, async is easier: **await LLM_Bridge.AskDecision()** and then set chosen tech. Need to **defer** research choice until LLM responds (e.g. “no topic” or “use fallback” until then). |

**Overall:** The main difficulty is **call-site sync → async**: **DiplomacyScreen** and **Relationship** call **AnalyzeOffer** synchronously. Introducing **await LLM_Bridge.AskDecision()** implies either (a) async all the way up and a non-blocking UI (e.g. “Evaluating…” then show result), or (b) a **deferred pipeline**: send request, continue with fallback or “pending,” then apply LLM result when it arrives (e.g. next frame or next turn). Option (b) avoids turning the whole update path async and is often easier to integrate.

---

## 5. File Path Reference (Strategic Decisions)

| Purpose | File path |
|---------|-----------|
| Diplomacy offer analysis & acceptance | **`Ship_Game/AI/EmpireAI/EmpireAI.DiplomacyOffers.cs`** — `AnalyzeOffer`, `AnalyzePeaceOffer`, `ProcessPeace`, `ProcessAlliance`, `ProcessQuality` |
| Diplomatic planner (personality → relations → war prep) | **`Ship_Game/AI/EmpireAI/EmpireAI.RunDiplomaticPlanner.cs`** — `RunDiplomaticPlanner`, `PrepareToAttackClosest/Weakest/Xenophobic` |
| Relationship behavior (posture, offers, “potential target”) | **`Ship_Game/Gameplay/Relationship.cs`** — `DoConservative`, `DoAggressive`, `DoRuthless`, `DoXenophobic` |
| War declaration execution | **`Ship_Game/AI/EmpireAI/EmpireAI.RunWarPlanner.cs`** — `DeclareWarOn`, `DeclareWarOnViaCall` |
| Prepare-for-war goal | **`Ship_Game/Commands/Goals/PrepareForWar.cs`** |
| Research choice | **`Ship_Game/AI/Research/ChooseTech.cs`** — `PickResearchTopic`; **`Ship_Game/AI/EmpireAI/EmpireAI.RunResearchPlanner.cs`** |
| Relationship state (memory) | **`Ship_Game/Gameplay/Relationship.cs`** — `Relationship`; **`Ship_Game/Gameplay/Relationship_trust.cs`** |
| Ship AI state (enum) | **`Ship_Game/AI/ShipAI/AIState.cs`** |
| Empire AI entry / planners | **`Ship_Game/AI/EmpireAI/EmpireAI.cs`** |
| Diplomacy UI (calls AnalyzeOffer) | **`Ship_Game/GameScreens/DiplomacyScreen/DiplomacyScreen.cs`** |
