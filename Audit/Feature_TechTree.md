# StarDrive BlackBox — Technology & Research System Audit

## 1. Node Structure

### Primary Class: `Technology`

**Location:** `Ship_Game\Technology.cs`

The tech tree node is the `Technology` class. Parent/child relationships use **string UIDs** in data, resolved at load time into **direct object references**.

| Field | Type | Description |
|-------|------|-------------|
| `UID` | string | Unique identifier |
| `IconPath` | string | Path to icon (e.g. `icons_techroot_starships`) |
| `Cost` | float | Base science cost |
| `RootNode` | int | Non-zero = root tech; value controls display order |
| `Secret` | bool | Hidden until revealed by another tech |
| `LowPriorityCostMultiplier` | float | Cost multiplier when low priority (default 1) |
| `MaxLevel` | int | Multi-level tech (default 1) |
| `MultiLevelCostMultiplier` | float | Cost scaling per level (default 1.4) |
| **Links (data)** | | |
| `LeadsTo` | `Array<LeadsToTech>` | Direct children — `{ UID: string }` |
| `ComesFrom` | `Array<LeadsToTech>` | Direct parents — `{ UID: string }` |
| **Links (resolved)** | | |
| `Children` | `Technology[]` | Resolved from `LeadsTo` via `ResolveLeadsToTechs()` |
| `Parents` | `Technology[]` | Resolved from `ComesFrom` via `ResolveComeFromTechs()` |

**Parent/Child Linking:**

- **Source of truth:** `LeadsTo` on the parent lists child UIDs.
- **Resolution:** `ResourceManager.LoadTechTree()` calls `tech.ResolveLeadsToTechs()` for each tech.
- **Graph shape:** Directed graph with multiple parents allowed (not a strict tree).

### Cost Definition

| Aspect | Implementation |
|--------|----------------|
| **Base cost** | `Technology.Cost` (science points) |
| **Final cost** | `Technology.ActualCost(UniverseState)` applies: `P.Pace`, `SettingsResearchModifier`, optional size-based scaling |
| **Per-turn time** | Not used; research uses science points per turn, not fixed turn counts |
| **Multi-level** | Level N cost = `Cost * MultiLevelCostMultiplier^(Level-1)` |

```csharp
// Technology.ActualCost()
return us.P.Pace * (Cost * multiplierToUse.Clamped(1, 25)).RoundDownTo10();
```

---

## 2. JSON Representation of a Tech Node

Example for **Ace Training**:

```json
{
  "UID": "Ace Training",
  "IconPath": null,
  "Cost": 410,
  "RootNode": 0,
  "Secret": false,
  "MaxLevel": 1,
  "TechnologyTypes": ["ShipGeneral"],
  "Dependencies": {
    "LeadsTo": ["FighterTheory"],
    "ComesFrom": []
  },
  "Rewards": {
    "ModulesUnlocked": [
      { "ModuleUID": "CombatEngine_2x1", "Type": null }
    ],
    "HullsUnlocked": [],
    "BuildingsUnlocked": [],
    "TroopsUnlocked": [],
    "BonusUnlocked": [
      {
        "Name": "Top Guns",
        "BonusType": "Bonus Fighter Levels",
        "Bonus": 2,
        "Type": null,
        "Tags": []
      }
    ],
    "EventsTriggered": [],
    "TechsRevealed": []
  },
  "RacialRestrictions": {
    "HiddenFromAllExcept": [],
    "HiddenFrom": [],
    "UnlockedAtGameStart": []
  },
  "RequiresResearchStations": false,
  "RequiresMiningOps": false
}
```

Example for **StarshipConstruction** (root tech):

```json
{
  "UID": "StarshipConstruction",
  "Cost": 50,
  "RootNode": 3,
  "Dependencies": {
    "LeadsTo": ["Corvettes", "Spaceport", "WarpTheory", "Fleet Supply"],
    "ComesFrom": []
  },
  "Rewards": {
    "ModulesUnlocked": ["ShipyardRepairDrone", "Sensor1x2", "Structure", "..."],
    "HullsUnlocked": [
      { "Name": "Misc/HaulerSmall", "ShipType": "Terran" },
      { "Name": "Platforms/Platform Base", "ShipType": null }
    ],
    "BonusUnlocked": [
      { "Name": "StarDrive Enhancement", "BonusType": "FTL Speed Bonus", "Bonus": -0.65 }
    ]
  }
}
```

---

## 3. State: How "Unlocked" Is Tracked

### Empire-Level State

| Storage | Type | Purpose |
|---------|------|---------|
| `Empire.TechnologyDict` | `Map<string, TechEntry>` | One `TechEntry` per tech UID |
| `Empire.UnlockedTechs` | `TechEntry[]` | Filter of `TechEntries` where `Unlocked == true` |

### TechEntry (Per-Empire, Per-Tech)

**Location:** `Ship_Game\TechEntry.cs`

| Field | Type | Meaning |
|-------|------|---------|
| `UID` | string | Tech identifier |
| `Progress` | float | Current research progress |
| `Discovered` | bool | Visible in tree (e.g. from root or revealed) |
| `Unlocked` | bool | Fully researched |
| `Level` | int | Multi-level progress (1..MaxLevel) |

**Validation summary:**

- Unlocked: `entry.Unlocked == true`
- Unlocked techs: `Empire.UnlockedTechs` = `TechEntries.Filter(e => e.Unlocked)`
- No `HashSet<int>` of IDs; lookup is `Empire.GetTechEntry(uid)` → `TechEntry.Unlocked`

---

## 4. "Can I Research This?" Logic

### Entry Point

`Empire.CurrentTechsResearchable()` returns techs that can be researched right now.

### Conditions (all must hold)

1. `tech.CanBeResearched`  
   - `!Unlocked` OR (multi-level and `0 < Level < MaxLevel`)
2. `tech.Discovered`  
   - Tech is visible in the tree
3. `tech.shipDesignsCanuseThis || tech.Tech.BonusUnlocked.NotEmpty`  
   - Either usable by ship designs or has bonuses
4. `empire.HavePreReq(tech.UID)`  
   - Prerequisites satisfied

### Prerequisite Logic

```csharp
// Empire.HavePreReq(techId) -> GetTechEntry(techId).HasPreReq(this)

public bool HasPreReq(Empire empire)
{
    TechEntry preReq = GetPreReq(empire);  // Find the tech that LeadsTo this one

    if (preReq == null || preReq.UID.IsEmpty())
        return false;  // No parent = root tech; roots have no prereq

    // Prereq satisfied if: parent is unlocked OR parent is not discovered (edge case)
    if (preReq.Unlocked || !preReq.Discovered)
        return true;

    return false;  // Parent not unlocked yet
}

// GetPreReq: Find the tech whose LeadsTo contains this tech's UID
public TechEntry GetPreReq(Empire empire)
{
    foreach (TechEntry entry in empire.TechEntries)
    {
        foreach (Technology.LeadsToTech leadsToTech in entry.Tech.LeadsTo)
        {
            if (leadsToTech.UID == UID)
            {
                if (entry.Tech.IsRootNode || !entry.IsHidden(empire))
                    return entry;
                return entry.GetPreReq(empire);  // Skip hidden techs, go to their parent
            }
        }
    }
    return null;
}
```

### Visibility / Discovery

- Root techs (`RootNode != 0`) are discovered at game start (for the empire’s race).
- Non-root techs become discovered when a parent is unlocked or via `TechsRevealed` on another tech.
- `IsHidden(empire)` can hide a tech based on race, `RequiresResearchStations`, `RequiresMiningOps`, etc.

---

## 5. Modifiers: What Happens on Research Complete

### Trigger Flow

1. `EmpireResearch.ApplyResearchPoints()` → `tech.AddToProgress()` → on completion:
2. `Empire.UnlockTech(tech, TechUnlockType.Normal, null)`
3. `tech.Unlock(us)` → `UnlockTechContentOnly(us, us, bonusUnlock: true)`
4. `tech.UnlockBonus(us)` plus unlocks for modules, hulls, buildings, troops

### Unlock Bonus Application

`TechEntry.UnlockBonus(Empire empire)` applies `Technology.UnlockedBonus` entries to `EmpireData`:

| BonusType | Effect (simplified) |
|-----------|---------------------|
| Research Bonus | `data.Traits.ResearchMod += Bonus` |
| FTL Speed Bonus | `data.FTLModifier *= (1 + Bonus)` |
| STL Speed Bonus | `data.SubLightModifier += Bonus` |
| Troop Strength Modifier | `data.Traits.GroundCombatModifier += Bonus` |
| Fuel Cell Bonus | `data.FuelCellModifier += Bonus` |
| Module HP Bonus | `data.Traits.ModHpModifier += Bonus` |
| … | Many more in `UnlockOtherBonuses()` |

### Unlock Modules / Hulls / Buildings / Troops

- **Modules:** `Empire.UnlockEmpireShipModule(moduleUID)`
- **Hulls:** `Empire.UnlockEmpireHull(hullName, techUID)` + `UpdateShipsWeCanBuild()`
- **Buildings:** `Empire.UnlockEmpireBuilding(buildingName)`
- **Troops:** `Empire.UnlockEmpireTroop(troopName)`

### Notification

- `Notifications.AddResearchComplete(tech.UID, Empire)` (player only)

---

## 6. UI: How Lines Between Nodes Are Drawn

### Mechanism

- No LineRenderer or UI line library.
- Lines are drawn using textured quads (horizontal/vertical line textures) via `SpriteBatch`.

### Flow

**File:** `Ship_Game\GameScreens\Universe\ResearchScreenNew.cs`

1. `DrawConnectingLines(batch)` is called in `Draw()` before nodes.
2. For each parent (root or tree node), `DrawConnectingLinesFromParentToChildren()`:
   - For each child in `parent.Entry.Children`:
     - Compute `branchMidPoint`, `verticalEnd`, `endPos` from node rects.
     - Draw vertical segment: `DrawResearchLineVertical(batch, branchMidPoint, verticalEnd, child.Unlocked)`
     - Draw horizontal segment: `DrawResearchLineHorizontal(batch, verticalEnd, endPos, child.Unlocked, gradient: true)`
   - Draw parent→branch: `DrawLineFromParentToBranchMiddle(batch, parent, anyTechsComplete)`

### Textures

| Texture | Use |
|---------|-----|
| `ResearchMenu/grid_horiz` | Horizontal line (incomplete) |
| `ResearchMenu/grid_horiz_complete` | Horizontal line (complete) |
| `ResearchMenu/grid_horiz_gradient` | Horizontal gradient (incomplete) |
| `ResearchMenu/grid_horiz_gradient_complete` | Horizontal gradient (complete) |
| `ResearchMenu/grid_vert` | Vertical line (incomplete) |
| `ResearchMenu/grid_vert_complete` | Vertical line (complete) |

### Geometry

```csharp
// Horizontal: RectF from left to right, 5px tall
RectF r = new(left.X + 5, left.Y - 2, (right.X - left.X) - 5, 5);
batch.Draw(texture, r, Color.White);

// Vertical: texture stretched vertically
RectF r = new(top.X - texture.CenterX, top.Y + 1, texture.Width, (bottom.Y - top.Y) - 1);
batch.Draw(texture, r, Color.White);
```

### Node Layout

- `TreeNode.RightPoint` / `RootNode.RightPoint`: connection point on the right of the node.
- `GetBranchMidPoint(parent)`: midpoint between parent and children.
- Layout uses a grid: `GridWidth`, `GridHeight`; nodes positioned via `GetCurrentCursorOffset()` and `ClaimedSpots`.

---

## 7. Quick Reference

| Question | Answer |
|----------|--------|
| Node class | `Technology` (template) + `TechEntry` (per-empire state) |
| Parent/child links | `LeadsTo` (children) / `ComesFrom` (parents); UIDs resolved to `Children[]` / `Parents[]` |
| Cost | Science points via `Cost` + `ActualCost(UniverseState)` |
| Unlocked tracking | `TechEntry.Unlocked` in `Empire.TechnologyDict` |
| Can research? | `CanBeResearched && Discovered && (shipDesignsCanuseThis \|\| BonusUnlocked) && HavePreReq()` |
| Prereq check | Parent (from `LeadsTo`) must be `Unlocked` |
| On complete | `UnlockBonus()`, `UnlockModules()`, `UnlockBuildings()`, `UnLockHulls()`, `UnlockTroops()` |
| Line drawing | `SpriteBatch.Draw()` with `grid_horiz*` and `grid_vert*` textures |
