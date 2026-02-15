# Audit Report 03: Gameplay Systems (UI, AI, Combat)

**Scope:** Static analysis of UI, AI, and Combat subsystems for DOTS migration.  
**Focus:** Business logic, coupling, and physics dependencies.

---

## 1. UI & Input Coupling

### 1.1 Trace: Major UI Actions

#### Pause / Game Speed (equivalent to "End Turn" in real-time 4X)

| Step | Location | What happens |
|------|----------|----------------|
| Input | `UniverseScreen.HandleInput()` | `input.PauseGame` → direct toggle |
| Handler | `UniverseScreen.HandleInput.cs:242-243` | `UState.Paused = !UState.Paused` |
| Game speed | `UniverseScreen.HandleInput.cs:331` | `HandleGameSpeedChange(input)` → `UState.GameSpeed = GetGameSpeedAdjust(...)` |

**Conclusion:** UI writes directly to `UniverseState.Paused` and `UniverseState.GameSpeed`. No command or event layer.

#### Move Unit / Attack (Right-Click)

| Step | Location | What happens |
|------|----------|----------------|
| Input | `UniverseScreen.ShipGroupMove.cs:19-42` | `Input.RightMouseClick` / `Input.RightMouseReleased` |
| Dispatch | `HandleShipSelectionAndOrders()` → `MoveSelectedShipsToMouse()` | Branches on `SelectedFleet` / `SelectedShip` / `SelectedShipList` |
| Commands | `ShipMoveCommands.cs` | `RightClickOnShip()`, `RightClickOnPlanet()` |
| Game logic | Same file + ShipAI | e.g. `selectedShip.AI.OrderAttackSpecificTarget(targetShip)`, `ship.AI.AddEscortGoal(targetShip)`, `ship.OrderToOrbit(planet, ...)`, `Universe.Player.AI.AddGoalAndEvaluate(new MarkForColonization(...))` |

**Conclusion:** UI calls game logic directly: `Ship.AI.Order*`, `Ship.OrderToOrbit`, `Player.AI.AddGoalAndEvaluate`. No CommandBus or event bus.

#### Other UI → Logic

- **Save/Load:** `GamePlayMenuScreen` → `Save_OnClick` / `Load_OnClick` → direct screen/state calls.
- **Colony/Planet:** `PlanetListScreen`, `ColonyScreen` → direct access to `Planet`, `Empire`, and UI state.
- **Ship orders (buttons):** `OrdersButton.cs` toggles flags on `Ship` / `Carrier` (e.g. `ship.TransportingFood = !input.RightMouseClick`).
- **Stance buttons:** `StanceButtons.cs` → `OnOrderButtonClicked` → ship/fleet order methods.

### 1.2 Rating: **Tightly Coupled**

- **No Command/Event pattern:** No `CommandBus.Send(...)` or `EventBus.Publish(...)`; UI handlers invoke game logic directly.
- **Direct references:** `UniverseScreen` holds `UState`, `Player`, `SelectedShip`, `SelectedFleet` and passes them into `ShipMoveCommands` and other helpers that mutate `Ship.AI`, `Empire.AI`, and `UniverseState`.
- **Implications for DOTS:** Extracting gameplay into jobs/systems will require an explicit command or event layer so the main thread can enqueue actions and the simulation can consume them without UI dependencies.

---

## 2. AI & Decision Making

### 2.1 Ship AI

- **Location:** `Ship_Game/AI/ShipAI/` (`ShipAI.cs`, `ShipAI.DoAction.cs`, `ShipAI.OrderAction.cs`, `ShipAI.Combat.cs`, `CombatAI.cs`, etc.)
- **Pattern:** **Finite State Machine (FSM) + order queue.**
  - **State:** `AIState` enum (~38 values): `Combat`, `HoldPosition`, `AttackTarget`, `Escort`, `MoveTo`, `Bombard`, `AssaultPlanet`, `Flee`, `Resupply`, etc. (`AI/ShipAI/AIState.cs`)
  - **Queue:** `OrderQueue` (`SafeQueue<ShipGoal>`) holds waypoints and high-level orders (e.g. attack, orbit, colonize).
  - **Execution:** Per-frame update chooses behavior from `State` (e.g. `DoCombat`, `DoBoardShip`, `ThrustOrWarpToPos` for move). State transitions are explicit (e.g. `ChangeAIState(AIState.Boarding)`).
- **Scoring / heuristics:**
  - **Utility module AI:** `UpdateUtilityModuleAI()` runs on a timer (e.g. 1s) for module-level decisions.
  - **Combat:** Target selection via `UpdateCombatTarget()`, `IsTargetValid()`, and `PotentialTargets`; no single global “score” function; eligibility and proximity drive choice.
  - **Combat tactics:** Dedicated classes (e.g. `Artillery`, `BroadSides`, `Evade`, `HoldPosition`) in `AI/CombatTactics/`; behavior is state/stance-driven rather than a single utility score.

### 2.2 Empire AI

- **Location:** `Ship_Game/AI/EmpireAI/` (`EmpireAI.cs`, `RunMilitaryPlanner`, `RunEconomicPlanner`, `RunResearchPlanner`, etc.)
- **Pattern:** **Goal-based with step functions and scoring heuristics.**
  - **Goals:** `GoalsList` (`Array<Goal>`). Each goal has a `Step` index and `Steps` (array of `Func<GoalStep>`). Main loop: `GoalsList[i].Evaluate()` advances steps or removes goals (`Goal.cs`).
  - **Managers:** “RunManagers” (e.g. military, economic, research, war, diplomacy) run first; they create/update goals. Then goals are evaluated. So: **manager layer** produces goals; **goal layer** executes steps.
- **Scoring / heuristics (examples):**
  - **Planet value (expansion):** `PlanetRanker` (`AI/ExpansionAI/PlanetRanker.cs`): `Value = rawValue / DistanceMod / EnemyStrMod`, with `rawValue = planet.ColonyPotentialValue(empire)`, distance and enemy strength modifiers. Used for colonization decisions.
  - **War state:** `WarScore` / `GetWarScoreState()` (`StrategyAI/WarGoals/WarScore.cs`, `War.cs`): compares military strength and threat to derive `WarState` (e.g. Dominating).
  - **Tech/ship picks:** `ShipPicker` (`AI/Research/ShipPicker.cs`): `techScore` built from cost, role multipliers, and randomization; used for research/ship choices.
  - **Ship building:** `ShipBuilder` (`EmpireAI/ShipBuilder.cs`): `GetColonyShipScore()`, `GetRefiningScore()` for design selection.
  - **Risk:** `EmpireRiskAssessment`: ratio of scores (e.g. `Them.TotalScore / us.TotalScore`) for diplomatic/strategic decisions.

### 2.3 AI Architecture Summary

| Layer | Pattern | Notes |
|-------|--------|--------|
| **Ship** | FSM + order queue | `AIState` + `OrderQueue`; state-driven methods (DoCombat, etc.); no behavior tree; some utility timers. |
| **Empire** | Goal/step + scoring | Goals with `Evaluate()` and steps; managers create goals; scoring in PlanetRanker, WarScore, ShipPicker, ShipBuilder, risk. |

**Classification:** **Hybrid** — FSM + queue at ship level; goal-based with **scoring heuristics** at empire level (not a single Utility AI formula, but multiple scoring functions for different decisions).

---

## 3. Combat Resolution

### 3.1 Entry Points

- **Attack / damage:** `Attack()` not found as a single global; damage flow is: **projectile/beam touch** → **module/ship damage**.
- **Projectile hit:** `Projectile.Touch()` (`Gameplay/Projectile.cs:846`) is invoked from narrow-phase collision when a hit is detected.
- **Damage application:** `ShipModule.Damage()` / `DamageExplosive()` (`Ships/ShipModule.cs`, `Ship_ModuleGrid.cs`); buildings: `ApplyDamageAndRemoveIfDestroyed` (`Building.cs`, `Combat.cs`).

### 3.2 Collision / Hit Detection (Critical for Physics)

**Spatial pipeline:**

1. **Broad phase:** Custom spatial structure (e.g. quadtree) produces overlap pairs (ships, projectiles, beams). See `UniverseObjectManager`, `SpatialManager`, `NarrowPhase`.
2. **Narrow phase:** `NarrowPhase.Collide()` (`Spatial/NarrowPhase.cs`) processes pairs:
   - **Projectile vs ship:** `HitTestProj()`:
     - If projectile moves > 15 units in one sim step: **ray test** — `ship.RayHitTestSingle(prevPos, center, proj.IgnoresShields)` (ray from previous position to current).
     - Else: **point test** — `ship.HitTestSingle(center, proj.Radius, proj.IgnoresShields)` (point + radius vs module quadrants).
   - **Beam vs ship:** `HitTestBeam()` → `ship.RayHitTestSingle(beamStart, beamEnd, ...)` and `hitModule.RayHitTest(..., out distanceToHit)`.
   - **Beam vs projectile/circle:** `victim.Position.RayCircleIntersect(victim.Radius, beamStart, beamEnd, out distanceToHit)`.

**Implementation of hit tests:**

- `Ship_ModuleGrid.HitTestSingle()`: iterates module quadrants overlapping the circle (grid/geometry, no Unity API).
- `Ship_ModuleGrid.RayHitTestSingle()`: ray vs module geometry; `ShipModule.RayHitTest()` uses `Position.RayCircleIntersect(...)` (circle/segment math).
- No use of Unity `Physics`, `Rigidbody`, `OnCollisionEnter`, or Unity `Raycast`.

### 3.3 Physics Dependencies: **None (pure math)**

| Check | Result |
|-------|--------|
| `OnCollisionEnter` | Not used. |
| `Rigidbody` | Not used. |
| `Physics.*` (Unity) | Not used. |
| Unity `Raycast` | Not used. |
| **Actual implementation** | Custom AABB/quadtree broad phase; narrow phase uses `Vector2`, `RayCircleIntersect`, grid quadrant enumeration, and `RayHitTestSingle` (all custom math). |

**Conclusion:** Combat resolution is **pure math and custom geometry**. No Unity Physics dependency. Suitable for a deterministic, multi-threaded or DOTS-style simulation once the data (positions, velocities, radii, module geometry) is available in the new architecture.

### 3.4 Damage Flow (no physics)

- **Projectile → module:** `Projectile.Touch()` → `ArmorPiercingTouch(module, parent, hitPos)` → module `Damage()` / explosion handling.
- **Beam:** `Beam.Touch()` applies damage over time; hit position from ray math.
- **Explosions:** `DamageExplosive()` distributes damage by quadrant/distance on the module grid; all arithmetic.

---

## 4. Output Deliverables Summary

| Deliverable | Result |
|-------------|--------|
| **UI coupling** | **Tightly coupled.** UI calls game logic directly (`UState.Paused`, `Ship.AI.Order*`, `Player.AI.AddGoalAndEvaluate`). No Command/Event pattern. |
| **AI architecture** | **Ship:** FSM + order queue. **Empire:** Goal/step system with **scoring heuristics** (PlanetRanker, WarScore, ShipPicker, ShipBuilder, risk ratios). Hybrid, not a single Utility AI. |
| **Physics in combat** | **No physics dependencies.** Combat uses custom spatial (quadtree, AABB), ray/circle and point-vs-module math. No Unity Physics or Raycast. **No rewrites required for physics;** only data and execution model need to align with DOTS. |

---

## 5. Recommendations for DOTS Extraction

1. **UI:** Introduce a **command or event queue** (e.g. “PauseToggle”, “SetSpeed”, “OrderMove”, “OrderAttack”) produced by UI and consumed by the simulation so UI never touches `UniverseState` or `Ship.AI` directly.
2. **Ship AI:** FSM and order queue are good candidates for a **state/command component + system**; keep state enum and queue semantics, replace object references with entities/IDs where possible.
3. **Empire AI:** Goals and steps can map to **goal components + systems**; scoring functions (PlanetRanker, WarScore, etc.) are pure functions on data and can run in jobs if inputs are in component/data form.
4. **Combat:** Reuse **pure-math hit detection and damage formulas**; replace `Ship`/`ShipModule` references with entity IDs and structured buffers so narrow phase and damage application can run in jobs.
