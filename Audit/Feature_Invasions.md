# Planetary Invasion & Ground Combat — Feature Analysis

**Static audit of the StarDrive 4X codebase.**  
Focus: Invasion flow, combat resolution, and visuals.

---

## 1. Context Switch: How Invasion is Initiated

### 1.1 Invasion Start

Invasion is **not** started by a dedicated "StartInvasion" call. It begins when **troops land on a hostile planet**:

| Trigger | Location | Flow |
|---------|----------|------|
| **Troop lands** | `Troop.TryLandTroop()` | `AssignTroopToRandomFreeTile()` or `AssignTroopToNearestAvailableTile()` → `AssignTroopToTile()` |
| **Landing sources** | `CombatScreen` (Land All / drag), `Ship.OrderLandAllTroops` | Troops launch from carrier ships, land via `TryLandTroop()` |
| **Entry point** | `Troop.AssignTroopToTile()` | Calls `planet.SetInGroundCombat(Loyalty, notify: true)` |

**No separate scene load.** Invasion runs on the same universe simulation. Ground combat occurs in-place on the planet.

### 1.2 Combat Screen: UI Overlay (Not New Scene)

The **CombatScreen** is a **UI panel overlaying the strategy map**, not a separate scene:

- **`workersPanel`** — `UniverseScreen` has a single `PlanetScreen workersPanel` that can be `ColonyScreen`, `UnownedPlanetScreen`, `UnexploredPlanetScreen`, or **`CombatScreen`**.
- **Activation:** `OpenCombatMenu(planet)` creates `new CombatScreen(this, planet)` and sets `LookingAtPlanet = true`.
- **When shown:** When the player clicks a planet with `combatView` and one of:
  - `p.Owner == Player && combatView` (own colony, combat mode)
  - `p.WeAreInvadingHere(Player)` (player has troops on enemy planet)
  - `p.System.OwnerList.Contains(Player)` or `p.OurShipsCanScanSurface(Player)` (visibility)
- **Camera:** `SnapViewTo(planet.Position, ...)` — camera stays in universe view, focused on the planet.

**Flow:** Strategy Map → Click planet (combat mode) → `SnapViewColony(p, combatView)` → `OpenCombatMenu(p)` → `CombatScreen` replaces `ColonyScreen` as `workersPanel`.

### 1.3 No GroundCombatManager

There is **no** `GroundCombatManager` class. Combat is driven by:

- **`TroopManager`** — Per-planet. Lives in `Planet.Troops`. `Update()` calls `ResolvePlanetaryBattle()` and `MakeCombatDecisions()`.
- **`Combat`** — Per engagement. Created by `CombatScreen.StartCombat(attacker, defender, tile, planet)`.

---

## 2. State Machine: Space → Invasion → Result

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│ SPACE (Strategy Map)                                                             │
│ - Fleet with troop carriers orbits planet                                        │
│ - Player/AI orders "Land Troops" or fleet task "Assault Planet"                  │
└────────────────────────────────────┬────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│ LANDING                                                                         │
│ - Troop.TryLandTroop(planet, tile)                                              │
│ - AssignTroopToTile() → planet.AddTroop(troop, tile)                            │
│ - planet.SetInGroundCombat(empire, notify: true)                                │
│ - Landing damage: DamageTroop(planet.TotalInvadeInjure, ...) from AA buildings  │
└────────────────────────────────────┬────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│ GROUND COMBAT (TroopManager.Update)                                             │
│ - InCombatTimer > 0 triggers ResolvePlanetaryBattle()                           │
│ - MakeCombatDecisions() → PerformGroundActions(troop, tile) per tile            │
│ - SpotClosestHostile() → AcquireTarget() → CombatScreen.StartCombat()           │
│ - Combat: RollDamage() → DealDamage() (each tick when Timer < 3)                │
│ - Troops move toward enemies (MoveTowardsTarget)                                │
│ - Buildings can attack (PerformGroundActions(building, tile))                   │
└────────────────────────────────────┬────────────────────────────────────────────┘
                                     │
                    ┌────────────────┴────────────────┐
                    ▼                                 ▼
┌───────────────────────────────────┐   ┌───────────────────────────────────────────┐
│ INVADERS WIN                                                      │ DEFENDERS WIN │
│ InvadingForces > 0 && DefendingForces == 0                        │ DefendingForces > 0 && InvadingForces == 0 │
│ DetermineNewOwnerAndChangeOwnership()                             │ LaunchNonOwnerTroops()                     │
│ → Ground.ChangeOwnerByInvasion(newOwner, Level)                   │ AbortLandingPlayerFleets()                 │
│ → SetOwner, notifications, assimilate traits, etc.                │ IncreaseTrustAlliesWon()                   │
└───────────────────────────────────┘   └───────────────────────────────────────────┘
```

---

## 3. Combat Logic

### 3.1 Grid / Tactical Map

- **Grid:** `PlanetGridSquare` 7×5 tiles (`SolarSystemBody.TileMaxX = 7`, `TileMaxY = 5`).
- **Troops:** On tiles via `PlanetGridSquare.TroopsHere`. Move using `TileDirection` (8-way).
- **Buildings:** `BuildingOnTile`, some `IsAttackable` (turret/AA). `InvadeInjurePoints` for landing damage.
- **Range:** Troop `ActualRange` (1+), Building range 1. `SpotClosestHostile(ourTile, range, ...)`.

### 3.2 Resolution: Per-Engagement Simulation (Not Auto-Calc)

Ground combat is **tactical simulation**, not a single auto-calc:

- Each **engagement** is a `Combat` instance. Created when a troop/building acquires a target.
- `TroopManager.ResolvePlanetaryBattle()` iterates `Ground.ActiveCombats` and calls `combat.ResolveDamage(isViewing)` when `Timer < 3` and `Phase == 1`.
- Combat runs over multiple simulation ticks. Damage is resolved once per combat when the timer fires.

### 3.3 Mathematical Formula: Ground Combat Damage

**File:** `Ship_Game/Combat.cs` — `RollDamage()`

```
attackValue = (defender is Troop) ? SoftAttack or HardAttack (by defender TargetType)
             : (defender is Building) ? HardAttack

damage = 0
for index = 0 to (attacker.Strength - 1):
    if Random(0, 100) < attackValue:
        damage += 1

return damage
```

- **Attacker:** Troop or Building. Uses `AttackerStats`: `Strength`, `SoftAttack`, `HardAttack`.
- **Troop stats:** `ActualSoftAttack = (int)(SoftAttack + 0.05 * Level * SoftAttack)`, `ActualHardAttack` same. `ActualStrengthMax = (StrengthMax + Level) * (1 + GroundCombatModifier)`.
- **Building stats:** `Strength`, `HardAttack`, `SoftAttack` from `Building`.
- **Defender type:** Troop `TargetType` (Soft/Hard) or Building → always Hard.

**Effective model:** Each "strength point" is one independent roll with `attackValue%` chance to deal 1 damage. Total damage is sum of successful rolls.

### 3.4 Victory Conditions

| Condition | Result |
|-----------|--------|
| `InvadingForces > 0 && DefendingForces == 0` | Invaders win → `ChangeOwnerByInvasion(newOwner, planet.Level)` |
| `DefendingForces > 0 && InvadingForces == 0` | Defenders win → `LaunchNonOwnerTroops()`, `AbortLandingPlayerFleets()` |

**Forces:** `TroopManager.Forces` — counts defending troops + attackable buildings vs invading troops (non-allied, non-owner).

---

## 4. Visuals

### 4.1 Combat Screen Layout

- **2D grid:** `PlanetGridSquare.ClickRect` — rectangles for each tile.
- **Troop sprites:** Texture atlas from `Textures/Troops/{TexturePath}/`, e.g. `idle_path`, `attack_path` frames.
- **Orbital assets:** List of ships in orbit with troops (Land All, Launch All, Bombard).

### 4.2 Effects During Combat

| Asset | Type | When | Source |
|-------|------|------|--------|
| **SmallExplosion** | 2D sprite animation | On hit (isViewing) | `CombatScreen.AddExplosion(grid, size)` |
| **sd_explosion_12a_bb** | TextureAtlas | size ≤ 3 | `Textures/sd_explosion_12a_bb` |
| **sd_explosion_14a_bb** | TextureAtlas | size > 3 | `Textures/sd_explosion_14a_bb` |
| **sd_troop_attack_miss** | SFX | Miss | `GameAudio.PlaySfxAsync` |
| **sd_troop_attack_hit** | SFX | Hit | `GameAudio.PlaySfxAsync` |
| **Explo1** | SFX | Troop killed | `GameAudio.PlaySfxAsync` |

### 4.3 No Separate Prefabs

No Unity-style prefabs. Used structures:

- **Troop** — Data from XML; `TexturePath`, `idle_path`, `attack_path` for sprites.
- **PlanetGridSquare** — Grid tile; no 3D mesh.
- **CombatScreen** — UI with buttons, scroll lists, grid overlay.
- **SmallExplosion** — Instantiated in `Explosions` array, drawn for ~2.25s, then removed.

### 4.4 Strategy Map (No Invasion-Specific VFX)

On the strategy map, planets in combat show an icon (`IndicatesThatGroundCombatIs` tooltip). No extra particles or effects for invasion; combat VFX are limited to the CombatScreen overlay.

---

## 5. Assets Spawned During Invasion

| Asset | Spawn Point | Disposal |
|-------|-------------|----------|
| **CombatScreen** | `OpenCombatMenu(planet)` | Replaced when switching planet or closing |
| **Combat** | `CombatScreen.StartCombat(...)` | Removed from `Planet.ActiveCombats` when `Done` |
| **SmallExplosion** | `CombatScreen.AddExplosion(rect, size)` in `DealDamage()` | Removed when `Time > Duration` (2.25s) |
| **Troop** (on planet) | `Troop.TryLandTroop()` → `planet.AddTroop()` | Removed when killed or launched |

**Note:** Troops are not "spawned" at invasion start; they are moved from carrier ships to the planet. The only runtime spawns are Combat instances and SmallExplosion visuals.

---

*Audit performed via static code analysis. Engine: StarDrive (XNA/MonoGame).*
