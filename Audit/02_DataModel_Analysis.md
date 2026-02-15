# Data Model Analysis — Map/Grid & Unit Definitions

**Static audit of the StarDrive 4X codebase for Data-Oriented (DOTS) extraction.**  
Focus: Grid implementation, Unit/Ship data model, and serialization.

---

## 1. Grid Implementation

### 1.1 Planet / Colony Grid (Planet Tiles)

| Aspect | Finding |
|--------|--------|
| **Primary type** | `PlanetGridSquare` — colony surface tile (building, troops, habitability). |
| **Storage** | **`Array<PlanetGridSquare> TilesList`** (flat list, not 2D array). |
| **Lookup** | `Planet.GetTileByCoordinates(int x, int y)` uses **linear search**: `TilesList.Find(pgs => pgs.X == x && pgs.Y == y)` → **O(n)** per lookup. |
| **Dimensions** | Static `SolarSystemBody.TileMaxX = 7`, `TileMaxY = 5` (fixed 7×5 grid). Tiles created row-major in `Planet_Generate.cs` (y then x). |
| **Coordinate system** | **2D offset (rectangular)**. No hex; 8-neighbor `TileDirection` (N, NE, E, SE, S, SW, W, NW). |

**Core file:**  
`Ship_Game/Universe/SolarBodies/PlanetGridSquare.cs`  
`Ship_Game/Universe/SolarBodies/Planet/Planet.cs` (e.g. `GetTileByCoordinates`, `TileArea`)  
`Ship_Game/Universe/SolarBodies/Planet/Planet_Generate.cs` (tile creation, `FindTileUnderMouse`)

**View coupling:**

- `PlanetGridSquare` does **not** inherit `MonoBehaviour`. It is a **plain data class** with `[StarDataType]`.
- **View-only field:** `Rectangle ClickRect` — screen hit-test rect; set by **View** (ColonyScreen, CombatScreen, EmpireScreen), not by simulation. Not serialized via `[StarData]`.
- No `GameObject` or `MeshRenderer` on the tile type itself.

**Grid ↔ Screen (View layer only):**

- Tiles are stored by (X, Y). Screen rect is computed in UI/combat when drawing:
  - `ClickRect = new Rectangle(GridPos.X + pgs.X * xSize, GridPos.Y + pgs.Y * ySize, xSize, ySize)`  
  (e.g. `CombatScreen.cs`, `ColonyScreen.cs`).
- **WorldToGrid** for planet: no continuous world space; “world” is grid (X,Y). Picking uses **HitTest** on `ClickRect` with mouse position → `FindTileUnderMouse` = `TilesList.Find(pgs => pgs.ClickRect.HitTest(mousePos))`.

**Conclusion (planet grid):**

- **Data:** Pure data aside from `ClickRect` (view concern; can be moved to a separate view component in DOTS).
- **Storage:** List + linear lookup is **slow** for coordinate access; DOTS should use **`Cell[,]` or flat array** indexed by `x + y * TileMaxX` for O(1) access.

---

### 1.2 Ship Module Grid (Slot Layout)

| Aspect | Finding |
|--------|--------|
| **Primary type** | Ship slots are a **rectangular grid of 16×16 world units** per cell. |
| **Storage** | **`short[] ModuleIndexGrid`** (Width × Height) in `ModuleGridFlyweight`. Indices into `ShipModule[] ModuleSlotList`. Sparse (many -1). **Array-backed, O(1)**. |
| **Coordinate system** | **2D offset grid** in “grid-local” space; origin at hull grid top-left, center from `ShipGridInfo.Center`. |

**Core file (Grid Math):**  
`Ship_Game/Ships/ModuleGridFlyweight.cs`  
`Ship_Game/Ships/Ship_ModuleGrid.cs` (Ship-partial: World ↔ Grid conversions)

**Extracted coordinate conversion algorithms**

- **Cell size:** 16 units per slot (all formulas use 16f).
- **Grid center (grid-local):**  
  `GridLocalCenter = (info.Center.X * 16f, info.Center.Y * 16f)` (from `ShipGridInfo`).

**Grid local position ↔ Grid point (integer cell):**

```csharp
// ModuleGridFlyweight.cs (pure, no rotation)
public Point GridLocalToPoint(in Vector2 localPos)
{
    return new Point((int)Math.Floor(localPos.X / 16f),
                     (int)Math.Floor(localPos.Y / 16f));
}
```

**World ↔ Grid (Ship-local, with rotation):**

```csharp
// Ship_ModuleGrid.cs (Ship partial)

// World → Grid-local position
public Vector2 WorldToGridLocal(in Vector2 worldPoint)
{
    Vector2 offset = worldPoint - Position;
    return RotatePoint(offset.X, offset.Y, -Rotation) + Grid.GridLocalCenter;
}

// World → Grid point (then clipped)
public Point WorldToGridLocalPoint(in Vector2 worldPoint)
{
    Vector2 gridLocal = WorldToGridLocal(worldPoint);
    return Grid.GridLocalToPoint(gridLocal);
}

// Grid-local position → World
public Vector2 GridLocalToWorld(in Vector2 localPoint)
{
    Vector2 centerLocal = localPoint - Grid.GridLocalCenter;
    return RotatePoint(centerLocal.X, centerLocal.Y, Rotation) + Position;
}

// Grid point → World (cell corner)
public Vector2 GridLocalPointToWorld(Point gridLocalPoint)
{
    return GridLocalToWorld(new Vector2(gridLocalPoint.X * 16f, gridLocalPoint.Y * 16f));
}
```

**Rotation helper (used above):**

```csharp
static Vector2 RotatePoint(double x, double y, double radians)
{
    double c = Math.Cos(radians), s = Math.Sin(radians);
    return new Vector2((float)(x * c - y * s), (float)(x * s + y * c));
}
```

**Summary:**

- **GridToWorld:** `local = (gridX*16, gridY*16)` → subtract `GridLocalCenter` → rotate by `Rotation` → add ship `Position`.
- **WorldToGrid:** world minus `Position` → rotate by `-Rotation` → add `GridLocalCenter` → `Floor(local/16)` for cell.

**View coupling:**

- `ModuleGridFlyweight` is **pure data** (no MonoBehaviour, no GameObject).
- `Ship` inherits `PhysicsObject` → `GameObject` → `SpatialObjectBase` (custom; **not** Unity `MonoBehaviour`). Rendering and scene representation are separate from this grid math; the math is **extractable as pure functions**.

---

## 2. Unit Data Model (Ships)

### 2.1 Static Stats (Design / Hull)

| Source | Type | Storage | Notes |
|--------|------|---------|--------|
| **Hull** | `ShipHull` | File-based (custom text format), not ScriptableObject | Size, MeshOffset, HullSlots, ThrusterZones, Role, Bonuses, etc. |
| **Design** | `ShipDesign` | File-based (custom writer/parser) + in-memory | Name, Hull, modules (DesignSlots), GridInfo, BaseHull, Role, fixed cost/upkeep. |
| **Legacy** | `LegacyShipData` | XML (`[XmlArray]`, `[XmlIgnore]`) | Used for legacy save/load and some design loading. |

- **No ScriptableObjects** for ship stats. Content is loaded from **files** (e.g. Hulls/, designs) and cached (e.g. `ResourceManager`, `GlobalStats`).
- **ShipDesign** holds `BaseHull` (ShipHull), `Grid` (ModuleGridFlyweight), `GridInfo` (ShipGridInfo). These are **shared** across all ship instances of that design.

### 2.2 Dynamic State (Runtime Ship)

| Aspect | Finding |
|--------|--------|
| **Root type** | `Ship` (partial) : `PhysicsObject` : `GameObject` : `SpatialObjectBase`. **Not** MonoBehaviour. |
| **Design reference** | `[StarData] public IShipDesign ShipData` — points to `ShipDesign` (template). |
| **Health** | On **GameObject**: `[StarData] public float Health`. On **Ship**: `HealthMax`, computed; damage/repair update `Health`. |
| **Other state** | Many `[StarData]` fields on `Ship`: Position, Velocity, Rotation, Loyalty, Fleet, PowerCurrent, Level, InCombat, Ordinance, AI, etc. |

**Conclusion:**

- **Dynamic state** is stored **on the Ship (GameObject) instance** — a single large object with mixed concerns (physics, combat, AI, grid refs). For DOTS, this should be split into **components / buffers** (position, health, design ref, etc.).
- **Static stats** are already **POCO-like** (ShipHull, ShipDesign, ModuleGridFlyweight) and file-driven; no MonoBehaviour. Reusable as read-only config/templates.

### 2.3 Key Class Classification

| Class | Classification | Notes |
|-------|----------------|-------|
| **PlanetGridSquare** | **Pure Data** | [StarDataType], no MonoBehaviour. Only view: `ClickRect` (can be view-only in DOTS). |
| **ModuleGridFlyweight** | **Pure Data** | No Unity types; shared grid layout + index array. Fully reusable. |
| **ShipGridInfo** | **Pure Data** | Size, Center, SurfaceArea for hull. |
| **ShipHull** | **Pure Data** | POCO from file; mesh/path refs are asset paths, not Unity objects. |
| **ShipDesign** | **Pure Data** | Template; holds Grid, GridInfo, DesignSlots. No MonoBehaviour. |
| **LegacyShipData** | **Pure Data** | XML POCO; legacy compatibility. |
| **Ship** | **Dirty (must refactor)** | Single large type mixing state, physics, AI, rendering. Not MonoBehaviour but “god object”; needs splitting into data components. |
| **ShipModule** | **Dirty (must refactor)** | Inherits `GameObject`; couples module state to game object. |
| **GameObject / PhysicsObject** | **Custom (not Unity)** | Custom hierarchy; no MonoBehaviour. Still “entity + mixed logic” style; good candidate for ECS components. |

---

## 3. Serialization

### 3.1 Mechanisms

| Mechanism | Usage |
|-----------|--------|
| **`[Serializable]`** | Used in **SDUtils** (e.g. `Map`, `Deque`, `SafeArray`) and **SynapseGaming** lighting. **Not** the main game save. |
| **`[StarDataType]` / `[StarData]`** | Main **binary save** schema. Types scanned and serialized via `BinarySerializer` (Data.Binary). |
| **`BinarySerializer.SerializeMultiType`** | Writes **multiple root objects** in one stream (e.g. header + state). |
| **JsonUtility** | **Not** used for core save/load. |
| **BinaryFormatter** | **Not** used; custom binary format. |

### 3.2 Save Game Root

- **File:** `Ship_Game/SavedGame.cs`
- **Root objects:**  
  `BinarySerializer.SerializeMultiType(writer, new object[] { header, state }, Verbose)`  
  - **header:** `HeaderData` (Version, SaveName, StarDate, PlayerName, ModName, Time).  
  - **state:** **`UniverseState`** — main root for game state.

**UniverseState** (in `Universe/UniverseState.cs`) holds:

- Empire list, solar systems, planets, spatial trees, influence tree, etc.
- All persistent game state is under this root.

### 3.3 Binary Serialization Stack

- **Entry:** `Ship_Game/Data/Binary/BinarySerializer.cs` (and `SerializeMultiType`).
- **Writer/Reader:** `BinarySerializerWriter.cs`, `BinarySerializerReader.cs`.
- **Type map:** `BinaryTypeMap`; user types get `BinarySerializer(type)`; supports `[StarDataType]` and `[StarData]` / `[StarDataConstructor]`.

---

## 4. Summary & DOTS-Oriented Recommendations

### 4.1 Grid Math (Reusable)

- **Planet:** 2D offset grid 7×5; no hex. Replace list + linear search with **flat array** index `x + y * TileMaxX` for O(1).
- **Ship module grid:** All **GridToWorld / WorldToGrid** logic is in:
  - **`Ship_Game/Ships/ModuleGridFlyweight.cs`** (GridLocalToPoint, cell size 16).
  - **`Ship_Game/Ships/Ship_ModuleGrid.cs`** (WorldToGridLocal, GridLocalToWorld, rotation around ship center).
- Formulas are **pure** (aside from reading ship Position/Rotation); can be moved into a **static or utility module** for DOTS.

### 4.2 Data Classes

- **Pure data (reusable):** `PlanetGridSquare`, `ModuleGridFlyweight`, `ShipGridInfo`, `ShipHull`, `ShipDesign`, `LegacyShipData`, and similar POCOs.
- **Refactor:** `Ship`, `ShipModule` — split into **data components** (position, health, design ref, grid ref) and separate systems (movement, combat, AI, rendering).

### 4.3 Serialization

- **Root save:** `UniverseState` (+ `HeaderData`).
- **Format:** Custom binary via `BinarySerializer` and `[StarDataType]`/`[StarData]`; no JsonUtility/BinaryFormatter for main save.
- For DOTS, a new serialization path can target **component buffers and shared config** while reusing the same logical schema (empires, systems, planets, ships, designs) if desired.

---

## 5. File Path Reference

| Purpose | File path |
|--------|-----------|
| Planet grid tile data | `Ship_Game/Universe/SolarBodies/PlanetGridSquare.cs` |
| Planet grid storage & lookup | `Ship_Game/Universe/SolarBodies/SolarSystemBody.cs`, `Ship_Game/Universe/SolarBodies/Planet/Planet.cs`, `Planet_Generate.cs` |
| Ship module grid math (core) | `Ship_Game/Ships/ModuleGridFlyweight.cs` |
| Ship World/Grid conversion | `Ship_Game/Ships/Ship_ModuleGrid.cs` |
| Ship design / hull (static) | `Ship_Game/Ships/ShipDesign.cs`, `Ship_Game/Ships/ShipHull.cs` |
| Ship runtime state | `Ship_Game/Ships/Ship.cs`, `Ship_Game/Gameplay/PhysicsObject.cs`, `Ship_Game/GameObject.cs` |
| Save root & binary entry | `Ship_Game/SavedGame.cs`, `Ship_Game/Data/Binary/BinarySerializer.cs` |
| Universe state (root type) | `Ship_Game/Universe/UniverseState.cs` |
