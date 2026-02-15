# Technical Recommendation: Port Legacy Math to Burst vs. Adopt Unity Physics

**Role:** High-Performance Math Engineer  
**Context:** StarDrive uses custom `SpatialManager`, `NarrowPhase`, and `RayCircleIntersect` math. It does NOT use Unity Physics (per Audit 03).

**Target:** 10,000 ships with deterministic, parallel collision resolution.

---

## 1. Math Translation: XNA/SDGraphics vs. Unity.Mathematics

### 1.1 Current Types in Combat/Physics Path

| Location | Type | Precision | Usage |
|----------|------|-----------|-------|
| `SpatialObjectBase.Position` | `SDGraphics.Vector2` | **float** | Ship/projectile/beam positions |
| `SpatialObjectBase.Radius` | `float` | float | Collision radii |
| `AABoundingBox2D` | `float X1,Y1,X2,Y2` | **float** | Broad-phase AABB |
| `RayCircleIntersect()` | `Vector2`, `float` | **float** | Beam/circle hit detection |
| `NarrowPhase` (all) | `Vector2`, `float` | **float** | Full narrow-phase pipeline |
| Camera / projection | `Vector3d`, `Vector2d` | **double** | Screen-space projection only |

### 1.2 Double Precision Usage

**Double is used for:**

- `Vector3d CamPos`, `CamDestination` — camera position in world space
- `ProjectToScreenPosition(Vector3d)`, `UnprojectToWorldPosition3D()` — projection math
- `Vector2d` — screen-space drawing (circles, lines) to avoid precision loss at extreme zoom
- Universe size: ±2M to ±20M (Tiny to TrulyEpic); world rect can span 40M units

**Double is NOT used for:**

- Collision detection
- Ship/projectile/beam positions
- `RayCircleIntersect`, `HitTestSingle`, `RayHitTestSingle`
- Spatial partitioning (NativeSpatial uses `int`-based `AABoundingBox2Di` for C++ interop)

### 1.3 Float vs. Double for 10,000 Ships

| Factor | Float | Double |
|--------|-------|--------|
| **Combat scale** | Ships engage within ~50k units (system/engagement range) | N/A |
| **Module grid** | 16 units per slot; radii ~8–64 | N/A |
| **Float precision at 1M** | ~0.06 units (7 sig figs) | — |
| **Float precision at 10M** | ~0.6 units | — |
| **Relevance** | Combat is local; ships don't fight at universe extremes | Camera/projection only |

**Conclusion:** `float` is sufficient for collision. `Unity.Mathematics.float2`/`float3` are direct drop-ins. No `double2`/`double3` needed for combat/spatial.

**Migration:** Replace `SDGraphics.Vector2` → `float2`, `Vector3` → `float3` in collision path. Keep double only for camera/projection (outside Burst).

---

## 2. Spatial Partitioning: Legacy vs. DOTS Options

### 2.1 Current Implementation

| Implementation | Type | Notes |
|----------------|------|-------|
| **NativeSpatial** | C++ (SDNative.dll) | P/Invoke; Grid, Qtree, GridL2; `SpatialCollideAll` returns `CollisionPair*` |
| **Managed Qtree** | C# | `Qtree.Collider` AABB overlap in leaves; same `NarrowPhase.Collide` |
| **Broad-phase output** | `CollisionPair*` (A, B indices) | Pairs of object IDs from spatial structure |
| **Data flow** | `SpatialObjectBase[]` + indices | Managed refs; `NarrowPhase` uses `objects[pair.A]` |

### 2.2 Can We Reuse Broad-Phase in IJob?

**NativeSpatial:** No — P/Invoke cannot run in Burst. Must be replaced.

**Managed Qtree broad-phase logic:** Yes, but it needs to be rewritten as job-safe:

1. **Data:** `NativeArray<AABB2D>` (or `float4`), `NativeArray<int>` for IDs, `NativeArray<byte>` for loyalty/collision masks.
2. **Structure:** Either:
   - **NativeQuadtree** — port C# Qtree to `IJob` + `NativeArray` node storage; traverse leaves and test AABB overlaps within each leaf.
   - **Spatial hash / grid** — `NativeHashMap<int2, NativeList<int>>` or `DynamicBuffer<Bucket>`; hash cell key from position; test overlaps within cell + neighbor cells.
3. **Output:** `NativeList<CollisionPair>` (or `DynamicBuffer<CollisionPair>`).

### 2.3 DynamicBuffer vs. NativeQuadtree

| Approach | Pros | Cons |
|----------|------|------|
| **DynamicBuffer&lt;Bucket&gt;** | ECS-native; easy to grow; good for spatial hash buckets | Need to design bucket structure; hash tuning for 2D world |
| **NativeQuadtree** | Matches legacy; proven; good for sparse space | More complex; rebuild/update strategy; C# Qtree has pointer-based nodes |
| **Spatial hash (cell → entities)** | Simple; O(1) insert; good cache locality | Cell size tuning; edge cases at boundaries |

**Recommendation:** Implement a **spatial hash** or **grid-based broad phase** in a first iteration:

- Simpler than full quadtree port
- Deterministic with fixed cell size
- Works well with `IJobParallelFor` over cells
- Can later add a quadtree if needed for very sparse or non-uniform layouts

---

## 3. CollisionSystem Architecture

### 3.1 Pipeline (Current)

```
SpatialManager.CollideAll()
  → ISpatial.CollideAll()  [broad phase: C++ or C# Qtree]
  → CollisionPair[] 
  → NarrowPhase.Collide(timeStep, pairs, objects)
      → Beam: HitTestBeam → RayCircleIntersect, RayHitTest
      → Proj: HitTestProj → RayHitTestSingle / HitTestSingle
      → HandleBeamCollision, proj.Touch()
```

### 3.2 DOTS CollisionSystem Design

```
┌─────────────────────────────────────────────────────────────────────┐
│ 1. BroadPhaseJob (IJob)                                              │
│    Input:  NativeArray<LocalTransform> (or float3 position)           │
│            NativeArray<ColliderBlob> (radius, type, loyalty)          │
│    Output: NativeList<CollisionPair>                                  │
│    Logic:  Spatial hash / grid; AABB overlap test per cell            │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 2. NarrowPhaseJob (IJob) — or IJobParallelFor over pair chunks       │
│    Input:  CollisionPair*, positions, radii, types                    │
│            BlobAssetRef<BeamData>, BlobAssetRef<ProjData> (if needed) │
│    Output: DynamicBuffer<DamageEvent> (Entity, SourceEntity, HitPos,  │
│            Damage, IsBeam, etc.)                                      │
│    Logic:  Port of RayCircleIntersect, beam/proj branch logic         │
│            Pure math; no OOP; write events only                       │
└─────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────┐
│ 3. ApplyDamageSystem (main thread or job)                             │
│    Input:  DynamicBuffer<DamageEvent>                                 │
│    Logic:  Resolve events → modify ShipHealth, destroy entities, etc. │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.3 ColliderData as Blob

```csharp
// Conceptual Blob layout for collision
public struct ColliderBlob
{
    public float Radius;           // circle radius
    public BlobArray<float2> Hull; // optional: polygon hull for ships
    public ColliderType Type;      // Ship, Proj, Beam
    public int LoyaltyId;
    public byte CollisionMask;
}

public struct DamageEvent
{
    public Entity Victim;
    public Entity Source;
    public float2 HitPosition;
    public float Damage;
    public float2 Impulse;     // for explosions
    public byte DamageType;    // beam, proj, explosive
}
```

### 3.4 RayCircleIntersect in Burst

The existing algorithm is already Burst-friendly (pure math, no managed refs):

```csharp
// From SDGraphics.MathExt - port to Burst
public static bool RayCircleIntersect(float2 center, float radius,
    float2 rayStart, float2 rayEnd, out float distanceFromStart)
{
    float2 d = rayEnd - rayStart;
    float a = math.dot(d, d);
    if (a <= 1e-6f) { distanceFromStart = float.NaN; return false; }

    float2 dc = rayStart - center;
    float c = math.dot(dc, dc) - radius * radius;
    if (c < 0f) { distanceFromStart = 1f; return true; }

    float b = 2f * math.dot(d, dc);
    float det = b * b - 4f * a * c;
    if (det < 0f) { distanceFromStart = float.NaN; return false; }

    det = math.sqrt(det);
    float t2 = (-b - det) / (2f * a);
    float t1 = (-b + det) / (2f * a);
    if (t1 < 0f && t2 < 0f) { distanceFromStart = float.NaN; return false; }

    float t = math.min(math.abs(t2), math.abs(t1));
    if (t >= -1f && t <= 1f)
    {
        distanceFromStart = t * math.length(d);
        return true;
    }
    distanceFromStart = float.NaN;
    return false;
}
```

### 3.5 Beam / Ship-Module Logic

Legacy `HitTestBeam` for ships uses `ship.RayHitTestSingle` → per-module `RayCircleIntersect`. For DOTS:

1. **Option A:** Ship = entity with `DynamicBuffer<ModuleInstance>` (position, radius per module). Narrow phase iterates modules and runs `RayCircleIntersect` for each. Output `DamageEvent` with module or ship entity.
2. **Option B:** Ship = single circle for broad phase; narrow phase uses a **BlobAssetRef&lt;ShipModuleLayout&gt;** to get module positions relative to ship, transform to world, then ray-vs-circle.

Both are job-safe. Option A is closer to the current design.

---

## 4. Recommendation: Port Legacy Math to Burst

### 4.1 Do NOT Adopt Unity Physics

| Reason | Explanation |
|--------|-------------|
| **2D vs 3D** | StarDrive is 2D top-down; Unity Physics (PhysX) is 3D. Would need 3D colliders with fixed Z, extra overhead. |
| **Determinism** | PhysX is non-deterministic across platforms/threading. 4X and replay need deterministic sim. |
| **Custom model** | Beam weapons, module-level hits, shield arcs, intercept projectiles — all custom. PhysX does not match. |
| **Performance** | PhysX has sync and marshalling cost. 10k ships with custom collision is better served by tuned jobs. |
| **Legacy alignment** | `RayCircleIntersect`, AABB overlap, spatial hash are simple and already proven. Porting is low risk. |

### 4.2 Port Legacy Math to Burst — Summary

| Task | Approach |
|------|----------|
| **Math types** | `Vector2` → `float2`, `Vector3` → `float3` in collision path. No double in combat. |
| **Broad phase** | Replace NativeSpatial with spatial hash or grid in `IJob`; output `NativeList<CollisionPair>`. |
| **Narrow phase** | Port `NarrowPhase.Collide` logic to `IJob`; use `RayCircleIntersect` (Burst), branch on type (Beam/Proj). Output `DynamicBuffer<DamageEvent>`. |
| **Ship modules** | `DynamicBuffer<ModuleCollider>` or Blob with module layout; ray-vs-module in job. |
| **Damage application** | Separate system consumes `DamageEvent` buffer; updates health, spawns explosions, etc. |

### 4.3 Expected Performance (10,000 ships)

| Phase | Est. cost | Notes |
|-------|-----------|-------|
| **Broad phase** | O(N) insert + O(cells × pairs) | Spatial hash; most cells empty; combat localizes pairs |
| **Narrow phase** | O(pairs) | Pure math; highly parallelizable per pair |
| **Damage apply** | O(events) | Sequential or small parallel batches |

With Burst + Job System, 10k ships should be achievable at 60 sim ticks/sec on modern hardware, assuming combat is not uniformly dense across the entire map.

---

## 5. Implementation Roadmap

1. **Phase 1:** Extract `RayCircleIntersect`, AABB overlap, and `HitTest` logic into a Burst-compatible math library (`static` methods, `float2`).
2. **Phase 2:** Implement `BroadPhaseJob` with spatial hash; `CollisionPair` as `NativeList`.
3. **Phase 3:** Implement `NarrowPhaseJob` with Beam/Proj branches; output `DamageEvent` buffer.
4. **Phase 4:** Implement `ApplyDamageSystem` and wire to existing health/destruction logic.
5. **Phase 5:** Add ship module layout (Blob or buffer) and port `RayHitTestSingle` for beam-vs-ship.

---

## 6. Final Verdict

| Option | Verdict |
|--------|---------|
| **Port Legacy Math to Burst** | **Recommended** — Deterministic, 2D-native, matches existing design, low port effort. |
| **Adopt Unity Physics** | **Not recommended** — 3D, non-deterministic, poor fit for beams/modules, higher integration cost. |

**Conclusion:** Port the legacy spatial and narrow-phase logic to Burst-compatible jobs. Use `float2`/`float3`; no double in the collision path. Replace NativeSpatial with a spatial hash or grid. Output damage events to a buffer for a separate apply system. This path preserves behavior, supports 10k ships, and keeps the simulation deterministic.
