# XNA Simulation Loop → Unity DOTS System Group Mapping

**Context:** Legacy XNA sim runs on a dedicated thread (`SimThread`), driven by `DrawCompletedEvt` (sim waits for draw each frame). Each step runs `SingleSimulationStep(FixedSimTime)` at a fixed 60 FPS (modulo GameSpeed/iterations).

**Goal:** Map this to Unity ECS `ComponentSystemGroups` and Jobs, with sync points and time handling.

---

## 1. System Group Mapping

### 1.1 SingleSimulationStep Breakdown (from Audit 01 + code)

```
SingleSimulationStep(FixedSimTime timeStep)
├── InvokePendingSimThreadActions()
├── ProcessTurnEmpires(timeStep)     → UpdateEmpires() per empire
├── UState.Objects.Update(timeStep)  → see 1.2
├── UpdateMiscComponents(timeStep)   → see 1.3
└── EndOfTurnUpdate(updated, timeStep)
```

### 1.2 Proposed ComponentSystemGroup Hierarchy

Suggested group order (execution order = dependency order). All under a **fixed-step** parent so `DeltaTime` is consistent.

```
SimulationSystemGroup (FixedStep)           // FixedSimTime equivalent
├── PendingActionsSystemGroup               // InvokePendingSimThreadActions
│   └── ConsumePendingSimActionsSystem      // ECB / main-thread command execution
│
├── EmpireTurnSystemGroup                   // ProcessTurnEmpires
│   ├── EmpireUpdateSystem                  // empire.Update(UState, timeStep) per empire
│   └── EmpireTurnSyncPoint                 // barrier: empires done
│
├── ObjectUpdateSystemGroup                 // UState.Objects.Update
│   ├── ListSyncSystem                      // UpdateLists: ApplyChanges, remove inactive
│   ├── SolarSystemAssignmentSystemGroup    // UpdateAllSystems
│   │   ├── UpdateSolarSystemShipsJob       // Parallel.For systems → IJobParallelFor
│   │   └── PlanetsTreeUpdateSystem
│   ├── ShipUpdateSystemGroup
│   │   └── ShipUpdateJob                  // ship.Update + UpdateModulePositions
│   ├── ProjectileUpdateSystemGroup
│   │   └── ProjectileUpdateJob
│   ├── SpatialUpdateSystem                 // Spatial.Update(objects)
│   ├── StructuralChangeSystem              // RemoveInActiveAndApplyChanges (ECB playback)
│   ├── CombatResolutionSystemGroup         // Spatial.CollideAll + NarrowPhase
│   │   ├── BroadPhaseCollisionJob
│   │   └── NarrowPhaseCollisionJob
│   ├── EmpireContactsAndBordersSystemGroup
│   │   └── UpdateContactsAndBordersJob    // per empire
│   ├── SensorUpdateSystemGroup
│   │   └── ShipSensorUpdateJob
│   ├── ShipAISystemGroup
│   │   └── ShipAIUpdateJob
│   └── VisibleObjectsSystem                // UpdateVisibleObjects (frustum cull)
│
├── MiscComponentsSystemGroup               // UpdateMiscComponents
│   ├── AnomalyUpdateSystem
│   ├── ExplosionManagerUpdateSystem
│   ├── BombUpdateSystem
│   ├── ShieldManagerUpdateSystem
│   ├── FTLManagerUpdateSystem
│   └── SpaceJunkUpdateSystem
│
└── PostEmpireUpdateSystemGroup             // EndOfTurnUpdate
    └── PostEmpireUpdateJob                 // UpdateMilitaryStrengths, UpdateMoneyLeeched, DysonSwarm
```

- **SimulationSystemGroup** runs at fixed timestep (see Time section).
- **Sync points** in XNA: (1) wait for Draw (DrawCompletedEvt), (2) after empire updates before objects, (3) after spatial/collision before sensors/AI, (4) after structural removal. In DOTS these become **system order** and **ECB playback** (see §2).

---

## 2. Threading Model

### 2.1 Replacing Parallel.For with Jobs

| XNA Code | Current Parallel Use | Proposed DOTS Replacement |
|----------|----------------------|----------------------------|
| `UpdateEmpires` | Sequential loop (no Parallel) | **IJobChunk** over `Empire` entities or **single-thread system** if empire count is small; optional **IJobParallelFor** by empire index. |
| `UpdateSystems` (systems count) | `Parallel.For(UState.Systems.Count, UpdateSystems, MaxTaskCores)` | **IJobParallelFor** over system entities or **IJobChunk** with **NativeArray** of system indices. Each chunk does `FindNearby` (spatial query) + assign ships to system. |
| `UpdateShips` (ships) | `Parallel.For(allShips.Length, UpdateShips, MaxTaskCores)` | **IJobEntity** (or **IJobChunk**) over `Ship` entities; **Burst**-compatible if ship logic is moved to components. |
| `UpdateProjectiles` | `Parallel.For(allProjectiles.Length, UpdateProjectiles, MaxTaskCores)` | **IJobEntity** over `Projectile` entities. |
| `UpdateSensors` | `Parallel.For(allShips.Length, UpdateSensors, MaxTaskCores)` | **IJobEntity** over `Ship` (sensor component). |
| `UpdateContactsAndBorders` | `Parallel.For(allEmpires.Length, ...)` | **IJobParallelFor** over empire range or **IJobChunk** over empire entities. |
| `UpdateAI` (ships) | `Parallel.For(allShips.Length, UpdateAI, MaxTaskCores)` | **IJobEntity** over `Ship` with AI component; likely **Low** Burst initially (complex FSM). |
| `EndOfTurnUpdate` (PostEmpireUpdate) | `Parallel.For(wereUpdated.Count, PostEmpireUpdate, ...)` | **IJobParallelFor** over empires that were updated (or **IJobChunk** filtered by “updated this turn”). |

**Spatial/Collision:**  
- **Spatial.Update(objects):** In DOTS this becomes updating a spatial structure (e.g. NativeQuadTree or Unity’s spatial module) from entity positions; can be **IJobParallelFor** over entities to insert/update, then a single-thread or small-job **Build** step.  
- **CollideAll:** Broad-phase in current code can stay as a single native call or be split into **BroadPhaseJob** (produce pairs) + **NarrowPhaseJob** (consume pairs, apply damage). Narrow phase that mutates health/destroy should use **ECB** for destroy/death.

### 2.2 Sync Points and EntityCommandBuffers

| XNA Sync Point | Purpose | DOTS Approach |
|----------------|---------|---------------|
| **DrawCompletedEvt.WaitOne()** | Sim thread waits for main thread draw to finish before next step. | **Frame boundary:** Simulation group runs in `FixedStepSimulationSystemGroup`; no separate sim thread. Structural changes are deferred with **ECB** and played back at sync points. |
| **InvokePendingSimThreadActions()** | Run UI-deferred actions (e.g. build, scrap) on sim thread. | **Main-thread system** or **ECB consumer:** One system that runs on main thread and consumes a **NativeQueue** or **ECB** of “commands” (build, scrap, change loyalty). Alternatively, **EntityCommandBufferSystem** (e.g. `EndSimulationEntityCommandBufferSystem`) plays back structural changes. |
| **UpdateLists / RemoveInActiveAndApplyChanges** | Remove dead ships/projectiles, apply loyalty changes, then spatial sees them. | **Structural changes** (destroy entity, add component) go through **ECB**. A **SyncPointSystem** or **Barrier** runs after **SpatialUpdate** and before **CombatResolution**; the **ECB** that destroys entities is played back at start of next frame or at a dedicated **EntityCommandBufferSystem** in the group. So: “remove inactive” = **ECB.DestroyEntity**; “ApplyChanges” = apply any pending component/layout changes before spatial and combat. |
| **After collision (NarrowPhase)** | Damage/death applied; entities may be destroyed. | **NarrowPhase** writes to **Health** component and/or **ECB.DestroyEntity**. Use **ECB** for all structural changes from jobs; play back via **EntityCommandBufferSystem** so that next frame (or next system in group) sees updated world. |

**Summary:**  
- **No dedicated sim thread** in Unity; fixed step runs in **FixedStepSimulationSystemGroup**.  
- **Parallel.For** → **IJobParallelFor** / **IJobEntity** / **IJobChunk** with **JobHandle** dependencies.  
- **Structural changes** (add/remove/destroy) → **EntityCommandBuffer**; playback at **EntityCommandBufferSystem** (e.g. end of group or beginning of next).  
- **PendingSimThreadActions** → Command queue consumed by a **main-thread system** or encoded into **ECB**/custom commands.

---

## 3. Time: FixedSimTime → SystemAPI.Time.DeltaTime

| XNA Concept | Implementation | DOTS Equivalent |
|-------------|----------------|-----------------|
| **FixedSimTime** | `FixedSimTime.FixedTime` = 1/CurrentSimFPS (e.g. 1/60), scaled by GameSpeed. | **Fixed timestep:** Use **FixedStepSimulationSystemGroup** (or custom **FixedStepSystemGroup**) so that **SystemAPI.Time.DeltaTime** is the fixed step (e.g. 1/60). Unity’s **FixedStepSimulationSystemGroup** runs at **Fixed Timestep** in **Project Settings → Time**. |
| **CurrentSimFPS** | 60 + SimFPSModifier. | **Time.fixedDeltaTime** = 1/60 (or your target). No need to expose FPS modifier unless you want variable sim rate. |
| **GameSpeed** | Multiplies target sim time advancement; more steps per frame. | Option A: **Scale** `DeltaTime` by GameSpeed in a **TimeScaleSystem** (set **Time.timeScale** or a custom **SimDeltaTime** component). Option B: Run **N** fixed steps per frame when GameSpeed > 1 (loop in a single system or group). |
| **Paused** | `timeStep.FixedTime == 0` or skip step. | **Time.timeScale = 0** or a **Paused** tag component that disables simulation systems (or skip **SimulationSystemGroup** when paused). |

**Recommendation:**  
- **SimulationSystemGroup** (or **FixedStepSimulationSystemGroup**) as parent.  
- **SystemAPI.Time.DeltaTime** (or **Time.DeltaTime**) = fixed step inside that group.  
- **GameSpeed:** Store in a singleton **GameSpeed** component; a **TimeScaleSystem** or the group’s **RateManager** multiplies the fixed step by GameSpeed so logic still uses “one step” but wall-clock advances faster.

---

## 4. Mapping Table: XNA Method → Proposed DOTS System + Burst

| XNA Method / Block | Proposed DOTS System / Job | Burst Eligibility | Notes |
|--------------------|---------------------------|-------------------|--------|
| **InvokePendingSimThreadActions** | `ConsumePendingSimActionsSystem` (main thread) or command queue consumer | **Low** | Touches UI/orders; may call into non-Burst code. |
| **ProcessTurnEmpires** / **UpdateEmpires** | `EmpireUpdateSystem` or `EmpireUpdateJob` (per empire) | **Low** | Empire.Update does diplomacy, victory, research, goals; managed refs, heavy logic. |
| **UpdateLists** (ApplyChanges, remove inactive) | `ListSyncSystem` + ECB playback for destroy | **Medium** | ApplyChanges can be “sync native lists”; remove inactive = ECB.DestroyEntity. |
| **UpdateSolarSystemShips** / **UpdateSystems** | `UpdateSolarSystemShipsJob` (IJobParallelFor over systems) | **High** | Pure spatial query + list fill; Burst if spatial is native. |
| **UState.PlanetsTree.UpdateAll** | `PlanetsTreeUpdateSystem` or job | **High** | Tree update from positions. |
| **UpdateAllShips** (ship.Update, UpdateModulePositions) | `ShipUpdateSystem` / `ShipUpdateJob` (IJobEntity) | **Medium** | Physics/movement Burst-friendly; module/rendering may need managed. |
| **UpdateAllProjectiles** | `ProjectileUpdateJob` (IJobEntity) | **High** | Movement and lifetime; good Burst candidate. |
| **Spatial.Update(objects)** | `SpatialUpdateSystem` (rebuild quadtree or spatial hash from entities) | **High** | Native array of positions/radii → spatial structure. |
| **Objects.RemoveInActiveAndApplyChanges** | Structural change system + **EntityCommandBufferSystem** | **N/A** | ECB playback. |
| **Spatial.CollideAll** (broad phase) | `BroadPhaseCollisionJob` | **High** | Overlap pairs; native. |
| **NarrowPhase.Collide** | `NarrowPhaseCollisionJob` + ECB for damage/destroy | **High** | Pure math + ECB for structural changes. |
| **UpdateAllEmpireContactsAndBorders** | `UpdateContactsAndBordersJob` (IJobParallelFor empires) | **Medium** | Depends on border/influence logic (floats/ints). |
| **UpdateAllSensors** | `ShipSensorUpdateJob` (IJobEntity) | **High** | Spatial queries + visibility; Burst if query is native. |
| **UpdateAllShipAI** | `ShipAIUpdateJob` (IJobEntity) | **Low** | FSM, goals, targeting; lots of branches and possibly managed. |
| **UpdateVisibleObjects** | `VisibleObjectsSystem` (frustum cull, fill visible lists) | **High** | AABB vs frustum; native. |
| **UpdateMiscComponents** (anomalies, explosions, bombs, shields, FTL, junk) | `AnomalyUpdateSystem`, `ExplosionUpdateSystem`, `BombUpdateSystem`, `ShieldUpdateSystem`, `FTLUpdateSystem`, `SpaceJunkUpdateSystem` | **Medium–Low** | Some (bombs, FTL) can be Burst; explosions/shields may have effects. |
| **UpdateClickableItems** | `UpdateClickableItemsSystem` (main thread) | **Low** | UI/presentation. |
| **EndOfTurnUpdate** (PostEmpireUpdate) | `PostEmpireUpdateJob` (IJobParallelFor over updated empires) | **Medium** | Military strength, money leeched; math-heavy. |
| **Empire.Update** (inside ProcessTurnEmpires) | Logic inside `EmpireUpdateSystem` or split into empire sub-systems | **Low** | Goals, diplomacy, victory, research; managed. |

---

## 5. Summary

- **Group order:** PendingActions → EmpireTurn → ObjectUpdate (Lists → Systems → Ships → Projectiles → Spatial → Structural → Combat → Contacts → Sensors → AI → Visible) → Misc → PostEmpire.
- **Threading:** Replace **Parallel.For** with **IJobParallelFor** / **IJobEntity** / **IJobChunk**; use **EntityCommandBuffer** for structural changes and play back at **EntityCommandBufferSystem**.
- **Sync points:** DrawCompletedEvt → frame boundary; remove-inactive and collision damage → ECB playback at defined system positions.
- **Time:** **FixedSimTime** → **FixedStepSimulationSystemGroup** + **SystemAPI.Time.DeltaTime**; GameSpeed via **Time.timeScale** or custom **SimDeltaTime**/rate manager.

This gives a direct mapping from the XNA sim loop to a DOTS system hierarchy and a clear path for Burst where logic is data-oriented and native-friendly.
