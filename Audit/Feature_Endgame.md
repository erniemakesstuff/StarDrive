# Audit: Endgame Logic, Victory Conditions, and Random Events

---

## 1. Victory Check

### 1.1 Where and How Often It Runs

- **Entry point:** Victory is evaluated inside **`Empire.Update(UniverseState UState, FixedSimTime timeStep)`** when the empire is the **player** (`us` = player).
- **Call chain:** Simulation loop → **`UpdateEmpires(timeStep)`** → for each non-defeated empire **`empire.Update(UState, timeStep)`**. The player is one of those empires.
- **Frequency:** **Once per player turn** (each time the player’s empire is updated in the turn cycle). There is no separate “victory phase”; it runs as part of the player’s turn update.

**Relevant code (excerpt):**

```csharp
// Empire.cs — inside Update(), when this == player (us)
ExecuteDiplomacyContacts();
CheckFederationVsPlayer(us);
Universe.Events.UpdateEvents(Universe);
// ...
if (!Universe.NoEliminationVictory)
{
    bool allEmpiresDead = true;
    foreach (Empire empire in Universe.Empires)
    {
        var planets = empire.GetPlanets();
        if (planets.Count > 0 && !empire.IsFaction && empire != this)
        {
            allEmpiresDead = false;
            break;
        }
    }
    if (allEmpiresDead)
    {
        Empire remnants = Universe.Remnants;
        if (remnants.Remnants.Story == Remnants.RemnantStory.None 
            || remnants.Remnants.NoPortals 
            || !remnants.Remnants.Activated)
        {
            Universe.Screen.OnPlayerWon();
        }
        else
        {
            remnants.Remnants.TriggerOnlyRemnantsLeftEvent();
        }
    }
}
for (int i = 0; i < OwnedPlanets.Count; i++)
{
    Planet planet = OwnedPlanets[i];
    if (planet.HasWinBuilding)
    {
        Universe.Screen.OnPlayerWon(GameText.AsTheRemnantExterminatorsSweep);
        return;
    }
}
```

- **Game over / win screens:** **`UniverseScreen.OnPlayerWon(LocalizedText title)`** sets `UState.GameOver = true` and adds **`YouWinScreen`**. **`OnPlayerDefeated()`** sets `GameOver`, clears objects, and adds **`YouLoseScreen`**. Defeat is triggered elsewhere (e.g. when the player has no planets / is marked defeated).

### 1.2 List of Implemented Victory Conditions

| Type | Condition | Notes |
|------|-----------|--------|
| **Elimination** | All other major empires have no planets (and are not factions). | Only if **`!Universe.NoEliminationVictory`** (e.g. sandbox sets this to true and disables this). If Remnants are present and active (story not None, have portals, activated), **no win yet** — **`TriggerOnlyRemnantsLeftEvent()`** runs instead (crisis/endgame). |
| **Remnants defeated (post-elimination)** | Same “all empires dead” check, but Remnants are **not** in crisis mode: `Story == None` **or** `NoPortals` **or** `!Activated`. | Then **`OnPlayerWon()`** with default title. |
| **Victory building** | Any owned planet has **`HasWinBuilding`** (any building with **`WinsGame == true`**). | **`OnPlayerWon(GameText.AsTheRemnantExterminatorsSweep)`**. Building category **`BuildingCategory.Victory`** exists; **`Building.WinsGame`** is set in data. **`HasWinBuilding`** is set when a building with **`WinsGame`** is constructed (e.g. in **`Planet_ConstructionQueue`**). |

There is **no** separate “Science victory”, “Diplomatic victory”, or “Score victory” check in this code path. Only **elimination** (with Remnants exception) and **victory building**.

---

## 2. Event System

### 2.1 RandomEventManager (Global Random Events)

- **Type:** **`RandomEventManager`** (**`UniverseState.Events`**). Holds **`RandomEvent ActiveEvent`**.
- **Update:** **`Universe.Events.UpdateEvents(Universe)`** is called **once per player turn** from **`Empire.Update`** (same place as victory check).

**Trigger: probability per turn (no MTTH)**

- Each turn, if **`ActiveEvent == null`**, **`TryEventSpawn(u)`** runs.
- **`TryEventSpawn`** rolls **`u.Random.RollDie(2000)`** (1–2000) and picks an event by range:

| Roll | Event | Effect (summary) |
|------|--------|-------------------|
| 1 | HyperSpaceFlux | Sets **ActiveEvent** (name, notification, **TurnTimer** 1–30, **InhibitWarp**). Notification sent. |
| 2–3 | ShiftInOrbit | One habitable planet: +0.1–0.5 max base fertility. Notification if relevant. |
| 4–5 | FoundMinerals | One owned planet: +MineralRichness. Notification. |
| 6 | VolcanicToHabitable | One “improveable” volcanic planet: change to Barren/Desert type, possibly add volcanoes. Notification. |
| 7–15 | Meteors | One habitable planet: spawn meteors (ships) targeting it. Notifications for player/allies. |

So spawn is **fixed probability per turn** (e.g. 1/2000 for Hyperspace Flux), **not** Mean Time To Happen.

**ActiveEvent lifecycle**

- **HyperSpaceFlux** is the only one that sets **ActiveEvent** and uses **TurnTimer**.
- **`UpdateEvents`**: if **ActiveEvent != null**, decrement **TurnTimer**; when **TurnTimer <= 0**, clear **ActiveEvent** and send “Hyperspace Flux has abated” notification.

**RandomEvent data structure (for global events):**

```text
RandomEvent
├── Name               (string)
├── NotificationString (string)
├── TurnTimer          (int)   — used for Hyperspace Flux duration
└── InhibitWarp        (bool)
```

### 2.2 Planet / Exploration Events (Separate System)

- **Source:** **ExplorationEvent** (loaded from XML into **`ResourceManager.EventsDict`**), keyed by event UID.
- **Trigger (planet tiles):**
  - **At planet generation:** **`SolarSystemBody`** can place an “event building” on a habitable body: picks from buildings with **`EventHere && !NoRandomSpawn`**, then **`Random.RollDice(selectedBuilding.EventSpawnChance)`**. If success, building is placed and **`PlanetGridSquare.SetEventOutComeNum(planet, building)`** assigns an **Outcome** (by chance) and stores **EventOutcomeNum**.
  - **When tile is used:** When a colony ship lands or an existing colony “activates” the tile (e.g. **PlanetGridSquare** logic), **`ResourceManager.Event(Building.EventTriggerUID).TriggerPlanetEvent(planet, empire, tile, screen)`** is called. So trigger is **tile-based + building `EventTriggerUID`** (and optionally **OutcomeWillLaunchEnemyShip** for early trigger on colonization).
- **No MTTH here either:** Planet events are tied to **building placement** (random at gen) and **tile interaction**, not a per-turn MTTH.

---

## 3. Crisis Logic (“Endgame Crisis”)

The **Remnants** faction acts as the main “endgame crisis”:

- **Remnants** are a special empire (**`UniverseState.Remnants`**). **`Remnants`** (class on that empire) holds **Activated**, **Story**, **StoryStep**, **Level**, portals, etc.

**Crisis triggering (when do Remnants “activate”):**

1. **Activation (story start):** When the player (and possibly others) have killed enough Remnant ships, **`IncrementKillsForStory(empire, xp)`** adds XP. When **StoryTriggerKillsXp >= ActivationXpNeeded**, **`Activate()`** is called:
   - Sets **Activated = true**, initializes level-up date.
   - Depending on **Story** (e.g. AncientBalancers, AncientExterminators, AncientWarMongers, …), adds goal **RemnantEngagements**, sends **AddRemnantsStoryActivation** notification, and can enable espionage/warning.

2. **“Only Remnants left” (all other empires dead):**  
   In the **victory check** above, when **allEmpiresDead** is true but Remnants are active (**Story != None**, have portals, **Activated**), the code does **not** call **OnPlayerWon()**. Instead it calls **`remnants.Remnants.TriggerOnlyRemnantsLeftEvent()`**:
   - If **OnlyRemnantLeft** already, return.
   - Else tries to get a story event with **TriggerWhenOnlyRemnantsLeft** for the current **Story**.
   - If found: notifies player with that event, sets **OnlyRemnantLeft = true**, and calls **`TriggerVsPlayerEndGame()`**.
   - If no such event: sets **Activated = false** so that a later victory check can grant normal win.

3. **TriggerVsPlayerEndGame() (crisis escalation):**
   - **AncientBalancers:** Create several portals (count depends on difficulty).
   - **AncientExterminators:** From existing portals, spawn multiple Behemoth ships.
   - **AncientWarMongers:** From portals, spawn ships and send them to orbit player planets (saturation and type depend on difficulty).
   - **AncientPeaceKeepers / AncientHelpers:** Set **Activated = false** (no spawn storm).

So the “crisis” is: **Remnants activate** when enough of their ships are killed; if the player then eliminates everyone else, **TriggerOnlyRemnantsLeftEvent** runs and **TriggerVsPlayerEndGame** spawns portals/ships. There is no separate “crisis” script; it’s driven by **RemnantStory** and the “only remnants left” branch of the victory check.

---

## 4. Narrative Hooks — How Event Choices Are Presented

### 4.1 Global Random Events (RandomEventManager)

- **No choices.** Only **notifications**: **`AddRandomEventNotification(message, iconPath, action, planet)`**. The player just sees a message (e.g. “Hyperspace Flux is inhibiting…”, “Planet X has shifted in its orbit”).

### 4.2 Planet / Exploration Events (ExplorationEvent + Outcome)

- **Trigger:** When a planet tile with an event building is activated, **ExplorationEvent.TriggerPlanetEvent(...)** is called. It resolves **Outcome** from **EventOutcomeNum** (already fixed for that tile by **Chance** at placement) via **TryGetOutcome**.
- **Presentation:** If the triggering empire is the player, an **`EventPopup`** is created and added to the screen:
  - **EventPopup** shows: **Title** (e.g. “{Outcome.LocalizedTitle} at {Planet.Name}”), **Image** (from **Outcome.Image** or default), **Description** (**Outcome.LocalizedDescr**), and optional lines (planet, money, science, etc.).
  - **Single action:** One button: **ConfirmText** (e.g. “Great!”) → **OnDismissClicked**. There are **no multiple choices** in this popup; the “choice” was effectively the **random outcome** (by **Chance**) when the tile was created.
- **Outcome execution:** **TriggerOutcome** / **CheckOutComes** apply the outcome (money, tech, troops, ships, tile changes, etc.). For **triggerNow** flows, outcome can be applied before the popup; otherwise it’s tied to the popup/flow in **CheckOutComes**.

So: **one outcome per tile**, chosen by weighted random at placement; the narrative hook is **one EventPopup with one confirm button**, not a choice tree.

### 4.3 Remnant Story Events

- **ExplorationEvent** entries in **EventsDict** can have **StoryStep**, **Story**, **TriggerWhenOnlyRemnantsLeft**, **AllRemnantStories**. They are shown via **AddRemnantUpdateNotify(expEvent, Owner)** (notification + likely same **EventPopup** pattern if an outcome is shown). Remnant “choices” are not a separate UI type; they use the same event/outcome pipeline.

---

## 5. Data Structures for Events

### 5.1 RandomEvent (Global)

```text
RandomEvent
├── Name               : string
├── NotificationString : string
├── TurnTimer          : int    (e.g. duration of Hyperspace Flux)
└── InhibitWarp        : bool
```

**Trigger:** One roll per turn (1–2000) in **TryEventSpawn**; ranges map to event type. **No** explicit “trigger conditions” struct; logic is in code.

### 5.2 ExplorationEvent (Planet / Remnant Story)

```text
ExplorationEvent
├── Name                  : string (localization nameId)
├── FileName              : string (debug)
├── StoryStep             : int    (Remnant story step)
├── Story                 : RemnantStory
├── AllRemnantStories     : bool
├── TriggerWhenOnlyRemnantsLeft : bool
└── PotentialOutcomes     : Array<Outcome>
```

**Trigger conditions (in code/data):**

- **Planet:** Building with **EventTriggerUID** pointing to this event, placed at gen with **EventSpawnChance**, or later when tile is activated; **EventOutcomeNum** selects outcome by **Chance**.
- **Remnant:** Matched by **StoryStep** + **Story** (or **AllRemnantStories**), and optionally **TriggerWhenOnlyRemnantsLeft**.

### 5.3 Outcome (Exploration / Planet Events)

```text
Outcome
├── Chance                  : int (weight for random pick)
├── TitleText, DescriptionText, ConfirmText : string (localization)
├── Image                   : string (path)
├── MoneyGranted            : int
├── TroopsToSpawn, TroopsGranted, FriendlyShipsToSpawn, PirateShipsToSpawn, RemnantShipsToSpawn : Array<string>
├── SecretTechDiscovered, UnlockTech : string
├── IndustryBonus, ScienceBonus     : float
├── GrantArtifact, RemoveTrigger, ReplaceWith, SelectRandomPlanet, OnlyTriggerOnce, AlreadyTriggered
├── NumTilesToMakeHabitable / Unhabitable, ChangeBaseFertility, ChangeBaseMaxFertility, ChangeRichness
└── (Artifact, SelectedPlanet used internally)
```

**Trigger:** Outcome is selected from **PotentialOutcomes** by weighted random (**Chance**) when **SetOutcomeNum** is called for a tile, or when a Remnant story event is resolved. **No** extra “trigger condition” on **Outcome** beyond being in the right **ExplorationEvent** and (for planet) being chosen for that tile.

---

## 6. Summary Table

| Topic | Finding |
|-------|--------|
| **Victory check frequency** | Once per **player turn**, inside **Empire.Update** (player branch). |
| **Victory condition types** | **Elimination** (all others dead; Remnants can block and trigger crisis), **Victory building** (any **WinsGame** building on an owned planet). No separate Science/Diplomatic/Score. |
| **Random events** | **RandomEventManager**: per-turn **probability roll** (e.g. 1/2000 for Hyperspace Flux). **No MTTH**; **TurnTimer** only for active Hyperspace Flux duration. |
| **Planet events** | **ExplorationEvent** + **Outcome**; triggered by **building placement** (EventSpawnChance at gen) and **tile activation**; outcome chosen by **Chance** at placement. No MTTH. |
| **Crisis** | **Remnants**: activate when **StoryTriggerKillsXp >= ActivationXpNeeded**. When all other empires are dead, **TriggerOnlyRemnantsLeftEvent** runs and **TriggerVsPlayerEndGame** spawns portals/ships by **RemnantStory** type. |
| **Event choices UI** | Global events: **notifications only**. Planet/Remnant events: **EventPopup** with **one confirm button**; “choice” is the pre-rolled **Outcome**, not a player selection. |
