# Assets & Visuals — Static Analysis Report

**Static audit of the StarDrive 4X codebase for Data-Oriented (DOTS) extraction.**  
Focus: Visual assets, prefab equivalents, animations, and VFX/particle structures.

> **Engine Note:** StarDrive uses a **custom XNA/MonoGame engine**, not Unity. There are no Unity Prefabs, Animator controllers, or MonoBehaviour scripts. This report maps the audit questions onto the equivalent structures.

---

## 1. Prefab Hierarchy Equivalents

### 1.1 Unit/Building Visual Definitions

StarDrive does **not** use Unity-style prefabs. Visual entities are defined by:

| Type | Definition File | Data Structure | Visual Loading |
|------|-----------------|----------------|----------------|
| **Ships** | `.hull` files (e.g. `Content/Hulls/Terran/Shuttle.hull`) | `ShipHull` | `ShipHull.LoadModel()` → `StaticMesh` → `SceneObject` |
| **Weapons/Projectiles** | `.xml` (e.g. `Content/Weapons/...`) | `WeaponTemplate` | `ModelPath`, `AnimationPath`, `WeaponTrailEffect`, `WeaponDeathEffect` |
| **Planets** | `PlanetTypes.yaml` | `PlanetType` | `PlanetRenderer` (MeshSphere, MeshRings, MeshGlowRing) |
| **Stations** | Hull + `SpacePortModel` in Empire data | `SpaceStation` | `StaticMesh.LoadMesh()` for inner/outer models |
| **Thrusters** | `ThrusterZone[]` in Hull | `Thruster` | `StaticMesh` "Effects/ThrustCylinderB" |
| **Shields** | Hardcoded path | `ShieldManager` | `StaticMesh` "Model/Projectiles/shield" |

**Core file:** `Ship_Game/Ships/ShipHull.cs` — `LoadModel()` loads mesh and creates `SceneObject`.

### 1.2 “Dumb” vs “Smart” Structures

#### Reusable “Dumb” Prefabs (Safe for DOTS)

- **`ParticleSettings`** — YAML-defined (`3DParticles/Particles.yaml`). Pure data: texture, effect path, duration, color ranges, blend modes. No logic.
- **`ParticleEffect.ParticleEffectData`** — YAML-defined (`3DParticles/ParticleEffects.yaml`). Composition of emitters; templates only.
- **`Explosion`** — YAML-defined (`Explosions.yaml`). Type + path + scale. Pure config.
- **`PlanetType`** — Mesh + textures (PlanetMesh, DiffuseMap, etc.). Data-driven.
- **`ShipHull`** — ModelPath, MeshOffset, Animated, Thrusters. Pure metadata; mesh loaded separately.
- **`StaticMesh`** — Geometry + vertex buffers. No gameplay logic.
- **`Thruster`** — Mesh + noise texture + effect. Purely visual.
- **`DrawableSprite`** (SunType animations) — AnimationPath + AnimationSpeed. Visual-only sprite animation.

#### “Smart” Structures (Logic Attached; Need Refactor for DOTS)

| Structure | Logic Attached | Location |
|-----------|----------------|----------|
| **`Ship`** | `ShipModuleDamageVisualization` (particle emitters per damaged module), `EngineTrail.Update()` calls | `Ship.cs`, `Ship_Update.cs`, `EngineTrail.cs` |
| **`ShipModuleDamageVisualization`** | Owns `ParticleEmitter` (Lightning, Dust, Smoke, Flame) per module; `Update()` spawns particles | `ShipModuleDamageVisualization.cs` |
| **`Projectile`** | Owns `TrailEffect` (ParticleEffect), `DeathEffect`; spawns `BeamFlash`, `Sparks`, etc. | `Projectile.cs` |
| **`Bomb`** | `TrailEmitter`, `FireTrailEmitter` (ParticleEmitter); `Explosion.AddParticle()` | `Bomb.cs` |
| **`SpaceJunk`** | `ProjTrail`, `FlameTrail` (ParticleEmitter) | `SpaceJunk.cs` |
| **`MiningBays`** | `FireEmitters[]`, `SmokeEmitters[]` (ParticleEmitter per bay) | `MiningBays.cs` |
| **`LaunchShip`** | `FlameTrail` (ParticleEmitter), `Flash.AddParticle()` | `LaunchShip.cs` |
| **`PlanetCrash`** | `TrailEmitter`, `FireTrailEmitter`, `FlameTrail` | `PlanetCrash.cs` |
| **`ConstructionShip`** | Direct `AddParticle()` (BlueSparks, Sparks, Lightning, Flash) | `ConstructionShip.cs` |
| **`SceneObj`** (main menu 3D) | `DustEmitter` (AsteroidParticles), `EngineTrail.AddParticle()` | `SceneObj.cs` |

**Script-stripping recommendation:**  
Extract particle/VFX **spawn requests** (position, effect name, scale) into a data-driven event system. Entities emit “spawn particle” events; a VFX system consumes them. Particle settings and effect templates remain pure data.

---

## 2. Animation Integration

### 2.1 No Unity Animator or Animation Events

StarDrive does **not** use Unity `Animator` or `AnimationEvent`. There is **no animation-driven gameplay logic** (e.g. `HitFrame()` → `ApplyDamage()`).

### 2.2 Animation Systems Present

| System | Purpose | Gameplay Coupling |
|--------|---------|-------------------|
| **XNAnimation** | Skinned mesh animation for `Animated=true` hulls (e.g. Ralyeh ships) | None. Bones updated in `SceneObject.UpdateAnimation()`. |
| **`UIBasicAnimEffect`** | UI fade/slide/scale, timeline-based | `FollowedByAction` callback when animation ends; can trigger UI logic or SFX. Not gameplay-critical. |
| **`DrawableSprite`** | Texture-sequence animation (SunType, explosions) | None. `Sprite.Update()` advances frame. |
| **Weapon `AnimationPath`** | Projectile texture sequences (e.g. plasma ball) | Visual only. |
| **UI YAML `Animation` / `SpriteAnim`** | Main menu planet/city lights, etc. | Timeline parameters (Params, MinColor, MaxColor). No callbacks in data. |

### 2.3 Logic-Driven vs Animation-Driven

- **Logic-driven animation:** Gameplay updates state; animation reflects it (e.g. ship rotation, thruster direction). This is the dominant pattern. ✅ Reusable.
- **Animation-driven logic:** Animation event triggers gameplay. ❌ **Not present** in this codebase.

**Conclusion:** No refactor needed for animation events driving gameplay. Animation is purely visual.

---

## 3. VFX / Particles

### 3.1 Architecture

- **`ParticleManager`** — Singleton. Holds all `IParticle` systems (BeamFlash, Explosion, ThrustEffect, EngineTrail, etc.) in `Tracked` and `ByName`.
- **`Particle`** (implements `IParticle`) — Shared particle system. Particles emitted via `AddParticle()`; rendering via `ParticleVertexBuffer`.
- **`ParticleEmitter`** — Per-entity emitter. Calls `AddParticle()` on an `IParticle` at a rate; tracks `PreviousPosition` for velocity.
- **`ParticleEffect`** — Composed effect (multiple emitters). Created per projectile/engine via `CreateEffect(effectName, pos, context)`.

### 3.2 Instantiation Model

| Component | Creation | Disposal | Pooling? |
|-----------|----------|----------|----------|
| **Particle (IParticle)** | Once at startup (from `Particles.yaml`) | On content reload | No — long-lived |
| **ParticleVertexBuffer** | On demand | `FreeVertexBuffer()` returns to pool | **Yes** — `GetReusableBuffer()` / `FreeVertexBuffer()` |
| **ParticleEmitter** | `IParticle.NewEmitter(...)` | GC when owning entity dies | **No** |
| **ParticleEffect** | `ParticleManager.CreateEffect(...)` | GC when owning entity dies; `DeathEffect = null` after one update | **No** |

### 3.3 PoolManager vs Instantiate/Destroy

- **No `Instantiate`/`Destroy`** for individual particles. Particles are added to shared systems via `AddParticle()`.
- **`ObjectPool<T>`** exists (`Ship_Game/Utils/ObjectPool.cs`) but is used for `IClearable` objects (e.g. pathfinding, AI), **not** for particles.
- **ParticleVertexBuffer** is pooled; **ParticleEffect** and **ParticleEmitter** are not.

**Recommendation for DOTS:**  
Pool `ParticleEffect` and `ParticleEmitter` instances to reduce GC pressure in combat with many projectiles/explosions. Particle buffers are already pooled.

### 3.4 Particle Spawn Call Sites

| Call Pattern | Usage |
|--------------|-------|
| `AddParticle(pos)` / `AddParticle(pos, vel, scale, color)` | One-shot: BeamFlash, Explosion, Sparks, Flash, Lightning, etc. |
| `NewEmitter(rate, pos, scale)` | Continuous: trails (ProjectileTrail, FireTrail, EngineTrail), damage smoke, mining bays |
| `CreateEffect(name, pos, context)` | Composed effects: weapon trails (RocketTrail, TorpTrail), death effects (Kinetic_Hit_Armor, etc.) |

---

## 4. Output Deliverables Summary

### 4.1 Reusable “Dumb” Prefabs (Safe to Use)

| Asset | Format | Notes |
|-------|--------|-------|
| `ParticleSettings` | YAML | 40+ types; texture, duration, blend, color |
| `ParticleEffect` templates | YAML | EngineThrust1, RocketTrail, Kinetic_Hit_*, etc. |
| `Explosion` definitions | YAML | Ship, Projectile, Photon, Warp |
| `PlanetType` meshes/textures | YAML + content | PlanetMesh, DiffuseMap, etc. |
| `ShipHull` metadata | .hull | ModelPath, MeshOffset, Animated, Thrusters |
| `StaticMesh` | .obj / content | Geometry only |
| `Thruster` mesh | Content | ThrustCylinderB |
| `ShieldManager` mesh | Content | shield model |

### 4.2 “Smart” Prefabs Needing Script Stripping

| Entity | Logic to Extract |
|--------|------------------|
| Ship | `ShipModuleDamageVisualization` → damage VFX events; `EngineTrail` → thrust VFX events |
| Projectile | `TrailEffect`/`DeathEffect` creation → effect spawn events |
| Bomb | Trail + explosion particle spawn → events |
| SpaceJunk | Trail emitters → events |
| MiningBays | Fire/Smoke emitters → events |
| LaunchShip | Flame trail + flash → events |
| ConstructionShip | Repair sparks/lightning → events |
| PlanetCrash | Trail + fire → events |
| SceneObj (main menu) | Dust + engine trail → events |

### 4.3 Animation Events Driving Game Logic

**Finding:** Animation does **not** drive gameplay logic. No `HitFrame()`-style callbacks. Animation is visual-only. No refactor required.

---

## 5. DOTS Extraction Recommendations

1. **ParticleSettings & ParticleEffect templates** — Keep as data. Map to DOTS VFX config components.
2. **VFX spawn sites** — Replace direct `AddParticle`/`NewEmitter`/`CreateEffect` calls with a `SpawnVFXEvent` component or command buffer.
3. **ParticleEffect / ParticleEmitter** — Consider pooling for high-volume combat.
4. **Ship/Projectile/Bomb** — Split “visual spawn” from simulation; entities emit events; VFX system binds effects by name.
5. **StaticMesh / SceneObject** — Mesh + transform; compatible with DOTS rendering (GPU instancing, etc.).

---

*Audit performed via static code analysis. Engine: StarDrive (XNA/MonoGame).*
