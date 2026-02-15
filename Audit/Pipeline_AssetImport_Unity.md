# Asset Import Pipeline — Legacy Content to Unity / DOTS

**Role:** Unity Tools Engineer  
**Context:** Ingest StarDrive custom formats (`.hull`, `.design`, `.yaml`, `.xml`) into Unity for Editor authoring and DOTS runtime.

**References:** Audit 02 (Data Model), Audit 04 (Assets & Visuals), Feature_ShipDesign.md, Feature_TechTree.md.

---

## 1. Schema Mapping: ScriptableObjects + BlobAssets

### 1.1 Ship Hull (from Feature_ShipDesign + ShipHull.cs)

**Legacy:** `.hull` custom text; `ShipHull` with `Size`, `GridCenter`, `HullSlots[]` (Pos + Restrictions), `ThrusterZone[]`, `ModelPath`, `IconPath`, `MeshOffset`, etc.

**Unity ScriptableObject (Editor authoring, references, inspector):**

```csharp
// Editor-only / Addressables reference
[CreateAssetMenu(fileName = "ShipHull", menuName = "StarDrive/Ship Hull")]
public class ShipHullAsset : ScriptableObject
{
    public string HullId;           // e.g. "Cordrazine/Dodaving"
    public string VisibleName;
    public string Style;
    public int GridWidth;
    public int GridHeight;
    public Vector2Int GridCenter;
    public List<HullSlotData> Slots = new();   // Pos (Vector2Int) + Restrictions (enum)
    public List<ThrusterZoneData> Thrusters = new();
    public string ModelPath;        // → Addressable or Prefab reference
    public Vector2 MeshOffset;
    public string IconPath;         // → Addressable Texture2D
    public bool Animated;
    public int Role;                // RoleName enum
}

[Serializable]
public struct HullSlotData
{
    public Vector2Int Pos;
    public Restrictions Restriction;  // enum I, IO, O, E, ...
}

[Serializable]
public struct ThrusterZoneData
{
    public Vector3 Position;
    public float Scale;
}
```

**DOTS BlobAsset (runtime, no GC, cache-friendly):**

```csharp
// BlobAsset - built from ShipHullAsset or imported .hull
public struct ShipHullBlob
{
    public int GridWidth;
    public int GridHeight;
    public int2 GridCenter;
    public BlobArray<HullSlotBlob> Slots;
    public BlobArray<ThrusterZoneBlob> Thrusters;
    public float2 MeshOffset;
    public int Animated;            // bool as int for Blob
    public int Role;
    // Model/Icon: use BlobString for path or separate BlobAssetReference<RenderMeshConfig>
}

public struct HullSlotBlob
{
    public int2 Pos;
    public byte Restriction;        // enum
}

public struct ThrusterZoneBlob
{
    public float3 Position;
    public float Scale;
}
```

---

### 1.2 Ship Design / Blueprint (from Feature_ShipDesign JSON schema)

**Legacy:** Key=value text + N lines of `x,y;uidIdx;wx,wy;turret;rot;hangar`.

**ScriptableObject:**

```csharp
[CreateAssetMenu(menuName = "StarDrive/Ship Design")]
public class ShipDesignAsset : ScriptableObject
{
    public int Version = 1;
    public string DesignName;
    public ShipHullAsset Hull;       // Object reference
    public string Role;
    public string Style;
    public Vector2Int Size;
    public Vector2Int GridCenter;
    public string IconPath;
    public float FixedCost;
    public float FixedUpkeep;
    public int DefaultCombatState;
    public int ShipCategory;
    public int HangarDesignation;
    public bool IsShipyard, IsOrbitalDefense, IsCarrierOnly;
    public string EventOnDeath;
    public List<string> ModuleUIDs = new();   // or ModuleAsset[]
    public List<DesignSlotData> Modules = new();
}

[Serializable]
public struct DesignSlotData
{
    public Vector2Int Pos;
    public int ModuleUIDIndex;      // index into ModuleUIDs
    public Vector2Int Size;
    public int TurretAngle;
    public int ModuleRot;           // ModuleOrientation
    public string HangarShipUID;
}
```

**BlobAsset:**

```csharp
public struct ShipDesignBlob
{
    public BlobString Name;
    public BlobAssetReference<ShipHullBlob> Hull;
    public int Role;
    public int2 Size;
    public int2 GridCenter;
    public float FixedCost;
    public float FixedUpkeep;
    public BlobArray<DesignSlotBlob> Modules;
    public BlobArray<BlobString> ModuleUIDs;
}

public struct DesignSlotBlob
{
    public int2 Pos;
    public ushort ModuleUIDIndex;
    public int2 Size;
    public int TurretAngle;
    public byte ModuleRot;
    public BlobString HangarShipUID;
}
```

---

### 1.3 Technology / Tech Tree (from Feature_TechTree)

**Legacy:** XML per tech; UID, Cost, LeadsTo[], ComesFrom[], Rewards (ModulesUnlocked, HullsUnlocked, BonusUnlocked, etc.).

**ScriptableObject:**

```csharp
[CreateAssetMenu(menuName = "StarDrive/Technology")]
public class TechnologyAsset : ScriptableObject
{
    public string UID;
    public string IconPath;
    public float Cost;
    public int RootNode;
    public bool Secret;
    public int MaxLevel;
    public float MultiLevelCostMultiplier;
    public List<string> LeadsTo = new();
    public List<string> ComesFrom = new();
    public TechnologyRewards Rewards;
}

[Serializable]
public class TechnologyRewards
{
    public List<string> ModulesUnlocked = new();
    public List<HullUnlock> HullsUnlocked = new();
    public List<BonusUnlock> BonusUnlocked = new();
    public List<string> TechsRevealed = new();
}
```

**BlobAsset (for runtime tree traversal and prereq checks):**

```csharp
public struct TechnologyBlob
{
    public BlobString UID;
    public float Cost;
    public int RootNode;
    public byte Secret;
    public int MaxLevel;
    public BlobArray<BlobString> LeadsTo;     // child UIDs
    public BlobArray<BlobString> ComesFrom;
    public TechnologyRewardsBlob Rewards;
}
```

---

## 2. Importer Strategy

### 2.1 ShipHull (.hull) — ScriptedImporter → Prefab + BlobAsset

**Recommendation:** Use a **Unity ScriptedImporter** for `.hull` that:

1. **Parses** the custom text (reuse or port `ShipHull` parser from `ShipHull.cs`).
2. **Produces:**
   - **Primary asset:** A **ShipHullAsset** (ScriptableObject) with slots, grid, thruster zones, and **string paths** for model/icon.
   - **Optional:** A **prefab** that references the hull asset + a **mesh prefab** (from ModelPath) for editor preview.
   - **BlobAsset:** Either generated in the same importer (e.g. `BlobAssetStore` / custom build step) or in a **Baker** that runs on the ShipHullAsset.

**Flow:**

- **Raw:** `Content/Hulls/Terran/Shuttle.hull`
- **Importer:** `ShipHullImporter : ScriptedImporter` → `context.AddObjectToAsset("Hull", hullAsset)`; set `context.SetMainObject(hullAsset)`.
- **Blob:** A **Baker** (e.g. `ShipHullBaker : Baker<ShipHullAsset>`) converts `ShipHullAsset` → `BlobAssetReference<ShipHullBlob>` and stores in a **BlobAssetStore** or subscene. Runtime systems request hull by ID and get the blob.

**Why not Prefab as main asset?** Hull is **data** (slot layout, restrictions). The **mesh** is a separate asset (model). So: **Hull = ScriptableObject (+ optional Blob)**; **Prefab** only if you need a visual editor placeholder (mesh + hull reference).

---

### 2.2 Ship Design (.design)

- **Option A:** **ScriptedImporter** for `.design` → **ShipDesignAsset** (ScriptableObject). Hull reference resolved by **HullId** → `AssetDatabase.FindAssets("t:ShipHullAsset HullId:...")` or a registry.
- **Option B:** **Editor tool** that loads `.design` files in bulk and creates `ShipDesignAsset` assets; no importer. Prefer **ScriptedImporter** for consistency and re-import on file change.

Design references **ModuleUIDs**; modules are separate (e.g. from XML). So: **Design importer** creates the design asset with **string** ModuleUIDs; at runtime (or in a Baker) resolve UID → `ModuleTemplateAsset` / `BlobAssetReference<ModuleTemplateBlob>`.

---

### 2.3 Texture Atlases / Folder-Based Lookup (XNA .xnb or raw folders)

**Legacy:** Lookup by path string, e.g. `ResourceManager.Texture("Textures/Troops/...")`, `"NewUI/slider_crosshair"`, `"TechIcons/star"`. Content can be `.xnb` (XNA compiled) or loose textures in folders.

**Unity mapping:**

| Legacy | Unity approach |
|--------|----------------|
| Path string `"Category/Name"` | **Addressables address** = same string, e.g. `"Textures/Troops/..."` or a normalized key `"troops_..."`. |
| Folder per category | **Addressables group** per category (e.g. `Textures/Troops`, `NewUI`, `TechIcons`). Each asset has **address** = logical name (folder-relative or full path). |
| Atlas (multiple sprites in one texture) | **SpriteAtlas** for UI; or **Texture2DArray** if you need array indexing (e.g. by icon index). For "one texture per logical resource", one Addressable per texture. |

**Pipeline:**

1. **Batch conversion:** If source is `.xnb`, use a **conversion tool** (e.g. XNB extractor or custom reader) to output **PNG/DDS** into a folder mirroring the path structure.
2. **Import:** Normal Texture2D import (or **ScriptedImporter** for custom formats). Place under a **Resources** or **Addressables** root, e.g. `Assets/Content/Textures/Troops/...`.
3. **Addressables:** Create **Addressables** entries with **address** = legacy path (e.g. `Textures/Troops/unit_01`) so runtime can request `Addressables.LoadAssetAsync<Texture2D>("Textures/Troops/unit_01")` equivalent.
4. **TextureArrays:** If you need **Texture2DArray** (e.g. for batching many icons): an **Editor script** or **ScriptedImporter** can collect textures in a folder and build a **Texture2DArray** asset; expose index in a small config (e.g. `IconId → array index`).

**No native .xnb in Unity:** So "Raw File" for textures is either **extracted PNG/DDS** or a **custom importer** that reads .xnb and outputs Texture2D (requires XNB parsing in C#).

---

### 2.4 Particles / VFX (YAML)

**Legacy:** `Particles.yaml`, `ParticleEffects.yaml`, `Explosions.yaml` — pure data (texture path, duration, blend, emitters).

- **ScriptedImporter** for `.yaml` under a known path (e.g. `3DParticles/`) → **ScriptableObject** (e.g. `ParticleSettingsAsset`, `ParticleEffectAsset`). Schema mirrors audit 04.
- **Runtime:** Either use ScriptableObject references or **Bake** to BlobAssets for DOTS VFX system (e.g. `ParticleEffectBlob` with BlobString texture path, float duration, etc.).
- **Unity VFX Graph:** Optional second step: an **Editor tool** generates **VisualEffectAsset** from your YAML-derived SO for use with VFX Graph; or keep a custom particle system that reads your SO/Blob.

---

## 3. Mesh Generation Pipeline

**Legacy:** `StaticMesh.LoadMesh(content, ModelPath, Animated)` — loads `.obj` or custom format; used for ships, projectiles, thrusters, shields.

**Options:**

### 3.1 Batch Convert to FBX (recommended for ships)

1. **Export from legacy:** Use existing or new tool to export ship models (from `.obj` or custom) to **FBX** (or keep OBJ; Unity imports OBJ).
2. **Place in Unity:** Put FBX (or OBJ) under e.g. `Assets/Content/Models/Ships/...` with same **relative path** as legacy `ModelPath` (e.g. `Model/Ships/Terran/Shuttle/ship08` → `Assets/Content/Models/Ships/Terran/Shuttle/ship08.fbx`).
3. **Import:** Default **Model Importer**; optionally **ScriptedImporter** override to set scale/rig for animations.
4. **Reference:** ShipHullAsset’s **ModelPath** becomes a **path** that a **Baker** or **runtime loader** maps to:
   - **Addressable** (e.g. prefab or mesh asset), or
   - **Direct reference** if you replace ModelPath with `GameObject` / `Mesh` reference in the SO.

**Result:** Ship hull SO references mesh/prefab by path or reference; **Baking** produces entities with **RenderMesh** (or **RenderMeshArray**) from that mesh.

### 3.2 Runtime Loader (custom format)

If you **keep** custom mesh format at runtime:

1. **Importer:** ScriptedImporter for `.obj` or custom extension → **Mesh** asset (Unity Mesh) and optionally a **MeshFilter** prefab. No FBX.
2. **Runtime:** No loader; use the imported Mesh in **RenderMesh**.
3. If format is **truly** custom (not OBJ): implement a **reader** in Unity that builds `UnityEngine.Mesh` from your format; run it in **ScriptedImporter** to produce a Mesh asset. Then DOTS **RenderMesh** uses that mesh.

### 3.3 DOTS RenderMesh

- **Baker:** For each **ShipHullAsset** (or entity that references a hull), a **Baker** adds **RenderMesh** (or **RenderMeshUnmanaged**) using the mesh from the hull’s ModelPath.
- **Shared mesh:** Same hull → same mesh; use **SharedComponent** or **BlobAsset** to avoid duplicating mesh data. **RenderMeshArray** is for multiple meshes per entity; for one mesh per ship, **RenderMesh** + shared mesh reference is enough.

**Proposed pipeline (batch):**

```
Legacy .obj / custom model
    → [Batch export to FBX or keep OBJ]
    → Place under Assets/Content/Models/...
    → Unity Model Importer (or ScriptedImporter)
    → Mesh / Prefab
    → Hull SO references Mesh (by path or ref)
    → Baker: Hull SO → Entity + RenderMesh(mesh from SO)
```

---

## 4. Asset Import Pipeline Diagram

End-to-end: **Raw File → Unity Importer → ScriptableObject → Baking → Entity / BlobAsset**.

```text
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                        ASSET IMPORT PIPELINE (Legacy → Unity / DOTS)                      │
└─────────────────────────────────────────────────────────────────────────────────────────┘

  RAW FILES                    UNITY IMPORTERS              AUTHORING ASSETS
  ─────────                    ──────────────              ────────────────

  .hull (text)         ─────►   ShipHullImporter      ─────►   ShipHullAsset (SO)
  (slot grid,                      (parse key=value              + optional Prefab
   thruster zones)                   + slot grid)                     (preview)

  .design (text)       ─────►   ShipDesignImporter    ─────►   ShipDesignAsset (SO)
  (key=value + module lines)        (parse; resolve HullId)        (refs Hull + ModuleUIDs)

  .xml (tech)          ─────►   TechnologyImporter   ─────►   TechnologyAsset (SO)
  (UID, Cost, LeadsTo,             or XmlImporter                 (refs for rewards)
   Rewards)

  .yaml (particles)    ─────►   YamlImporter /        ─────►   ParticleSettingsAsset (SO)
  (Particles, Effects)             ScriptedImporter

  .xnb / .png (tex)    ─────►   Texture2D Importer   ─────►   Texture2D (or SpriteAtlas)
  (folder path = key)              + Addressables                Address = legacy path

  .obj / .fbx (model)  ─────►   Model Importer       ─────►   Mesh / Model Prefab
  (ModelPath in hull)              (default or custom)           (referenced by Hull SO)


                                    ▼
  ─────────────────────────────────────────────────────────────────────────────────────
                                    BAKING SYSTEM (DOTS)
  ─────────────────────────────────────────────────────────────────────────────────────

  AUTHORING ASSETS                 BAKERS                         RUNTIME ARTIFACTS
  ────────────────                 ──────                         ─────────────────

  ShipHullAsset            ─────►   ShipHullBaker           ─────►   BlobAssetReference<ShipHullBlob>
  (in subscene or                  (Convert SO → Blob)                (in BlobAssetStore or world)
   blob build)

  ShipDesignAsset          ─────►   ShipDesignBaker          ─────►   BlobAssetReference<ShipDesignBlob>
                                    (Resolve Hull + Module refs)      + Entity with DesignRef component

  TechnologyAsset          ─────►   TechTreeBaker            ─────►   BlobAssetReference<TechTreeBlob>
  (all techs)                       (Build graph blob)                (single blob for full tree)

  ShipHullAsset            ─────►   ShipEntityBaker          ─────►   Entity + RenderMesh
  + Mesh ref                         (Hull + Mesh → entity)            + HullBlobRef + Transform


  ─────────────────────────────────────────────────────────────────────────────────────
  RUNTIME FLOW
  ─────────────────────────────────────────────────────────────────────────────────────

  Game load / spawn ship:
    Request Hull by Id  ──►  BlobAssetStore.Get<ShipHullBlob>(id)
    Request Design      ──►  BlobAssetReference<ShipDesignBlob>
    Instantiate         ──►  Entity with RenderMesh (from Hull’s mesh) + components (HP, slots from blob)
```

**Summary:**

- **Raw:** `.hull`, `.design`, `.xml`, `.yaml`, textures, meshes.
- **Importer:** ScriptedImporters (or built-in for texture/model) produce **ScriptableObjects** (and optionally prefabs/meshes). Texture paths mapped to **Addressables**.
- **Baking:** Bakers turn SOs into **BlobAssets** and/or **Entities** with **RenderMesh**; BlobAssets live in a store or world.
- **Runtime:** Systems resolve hull/design by ID, read from blobs; rendering uses **RenderMesh** and mesh from the referenced asset.

---

## 5. File Reference

| Output | Location |
|--------|----------|
| Ship Design schema (JSON-like) | `Audit/Feature_ShipDesign.md` §3.3 |
| Tech tree schema (JSON) | `Audit/Feature_TechTree.md` §2 |
| Hull / slot / design structures | `Audit/Feature_ShipDesign.md` §1–3 |
| Assets & visuals (mesh, particles, texture) | `Audit/04_Assets_Visuals.md` |
| Data model (grid, serialization) | `Audit/02_DataModel_Analysis.md` |
