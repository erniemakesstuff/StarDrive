# Ship Designer & Customization System — Audit for Reuse

**Goal:** Reuse the logic for attaching modules to hulls (slot model, verification, blueprint schema, UI binding).

---

## 1. The Hull Model

### 1.1 Class Defining a Ship Hull

**File:** `Ship_Game/Ships/ShipHull.cs`

- **ShipHull** is a **POCO** (no MonoBehaviour). Loaded from **custom text files** (e.g. `.hull` or legacy conversion).
- **Slots** are **not** Vector3 world positions or separate child GameObjects. They are **integer grid positions** plus a **restriction type**.

### 1.2 How Slots Are Defined

**Slot type:** **`HullSlot`** (lives in `Ship_Game/Gameplay/ModuleSlot.cs`).

```csharp
// HullSlot — used only in hull definitions
public sealed class HullSlot
{
    public readonly Point Pos;   // integer (gridX, gridY), e.g. [0, 1]
    public readonly Restrictions R;  // I, IO, O, E, IE, OE, IOE, xI, xIO, xO
    public Point GetSize() => new Point(1, 1);  // each hull slot is 1x1
}
```

- **Pos:** 2D integer **grid** position (e.g. 0-based). Grid is **row-major**; cell size in world is **16 units** (used in ModuleGridFlyweight / Ship_ModuleGrid).
- **No Vector3** on the hull for slot positions; world position is derived as **`(Pos - GridCenter) * 16`** when needed (see SlotStruct).
- **ShipHull** also has:
  - **Size** (Point): grid width × height
  - **GridCenter** (Point): offset from grid top-left to “center” slot
  - **HullSlots** (array): one entry per 1×1 slot; multiple slots can be covered by one multi-cell module later
  - **ThrusterZone[]**: Vector3 Position + Scale (for VFX), separate from module slots
  - **MeshOffset**, **ModelPath**, **IconPath**: visuals

**Hull file format (slot layout):** Slots are defined in a **text grid** in the hull file (e.g. rows of tokens like `"I "`, `"IO"`, `"O "`, `"IOC"`, `"EC "`). Each token maps to a **Restrictions** enum; position is (column, row). A slot marked `C` (e.g. `IOC`) is the **GridCenter**.

**Conclusion:** Slots are **integer (X,Y) + Restrictions**. No child GameObjects; no Vector3 slot positions stored on the hull. World position is **grid → (pos - center) * 16**.

---

## 2. The Module Data

### 2.1 Class for Ship Modules

**File:** `Ship_Game/Ships/ShipModule.cs` (runtime instance), **`Ship_Game/ShipModuleFlyweight.cs`** (template/cache).

- **ShipModule** extends **GameObject** (custom, not Unity). Runtime instances are created from **templates**.
- **Templates** come from **XML** (e.g. `ShipModule_XMLTemplate` in ShipModuleFlyweight.cs). Loaded/cached via **ResourceManager.GetModuleTemplate(uid)**.
- **Not ScriptableObjects.** Module definitions are **file-based** (XML); the game uses a central cache (e.g. ResourceManager) keyed by **Module UID**.

### 2.2 Fit Restrictions (Cannot Put Engine in Weapon Slot)

**File:** `Ship_Game/Gameplay/Restrictions.cs`

```csharp
public enum Restrictions
{
    I,    // Internal
    IO,   // Internal Or external (any except E)
    O,    // External (outer)
    E,    // Engine
    IE,   // Internal or Engine
    OE,   // External or Engine
    IOE,  // Any slot
    xI,   // only I
    xIO,  // only IO
    xO    // only O
}
```

- **Hull slot** has **HullRestrict** (one of these).
- **Module** has **Restrictions** (what slot types it can go in).
- Fit is decided by **SlotStruct.CanSlotSupportModule(ShipModule)** (see §3.2). So “slot verification” is **hull restriction × module restriction** (I/O/E and exclusive variants).

**Additional constraints (not in enum):**

- **Size:** Module has **XSize**, **YSize** (1×1, 2×2, etc.). It must fit in a **contiguous rectangle** of hull slots; each covered slot must **CanSlotSupportModule**.
- **Hangar / ship class:** Modules can have **PermittedHangarRoles**, **MaximumHangarShipSize**, and hull-type flags (FighterModule, CruiserModule, etc.) used for filtering in the UI and validation.

---

## 3. Persistence (The Blueprint)

### 3.1 ShipDesign and Blueprint

**File:** `Ship_Game/Ships/ShipDesign.cs`, **`Ship_Game/Ships/ShipDesign_WriterParser.cs`**

- **ShipDesign** is the “blueprint”: Name, Hull (hull ID), Role, Style, **DesignSlots** (array of **DesignSlot**), **GridInfo** (Size, Center), ModuleGridFlyweight **Grid**, etc.
- **DesignSlot** (in `Ship_Game/Gameplay/ModuleSlot.cs`) is one placed module:

```csharp
public class DesignSlot
{
    public Point Pos;           // grid position (top-left of module)
    public string ModuleUID;    // which module template
    public Point Size;          // 1,1 or 2,2 etc.
    public int TurretAngle;     // 0..360
    public ModuleOrientation ModuleRot;  // Normal, Left, Right, Rear
    public string HangarShipUID; // optional, for hangar modules
}
```

### 3.2 How User-Created Design Is Saved

- **Not JSON.** Custom **key=value text format**, one key per line; then **N lines of module rows** (no key prefix).
- Written by **ShipDesignWriter** (`Ship_Game/Ships/ShipDesignWriter.cs`): writes **ASCII** (e.g. `key=value\n`). Design slots are written as one line per slot in a fixed format.

**Save sequence (from ShipDesign_WriterParser.cs CreateShipDataText):**

1. Header: Version, Name, Hull, Role, ModName, Style, Description, Size, GridCenter, IconPath, SelectIcon, FixedCost, FixedUpkeep, DefaultCombatState, ShipCategory, HangarDesignation, IsShipyard, IsOrbitalDefense, IsCarrierOnly, EventOnDeath.
2. **ModuleUIDs** = semicolon-separated list of unique module UIDs (index 0 = first UID).
3. **Modules** = count N.
4. N lines: each line is one design slot: **`gridX,gridY;moduleUIDIndex;sizeX,sizeY;turretAngle;moduleRot;hangarShipUID`**. Trailing optional fields omitted if default (e.g. size 1,1 or angle 0).

**Parsing (same file):** `ParseDesignSlot(line, moduleUIDs)` splits by `;`; **moduleUIDIndex** is used to look up **moduleUIDs[index]** to get **ModuleUID** string.

### 3.3 Schema of a Saved Ship Design (JSON-like representation)

Below is the **logical schema** of the saved design. On disk it is **plain text key=value + line-based module list**, not JSON. This JSON is the equivalent structure for reuse (e.g. for a JSON export or DOTS blueprint).

```json
{
  "Version": 1,
  "Name": "string",
  "Hull": "string",
  "Role": "string",
  "ModName": "string",
  "Style": "string",
  "Description": "string",
  "Size": { "X": 0, "Y": 0 },
  "GridCenter": { "X": 0, "Y": 0 },
  "IconPath": "string",
  "SelectIcon": "string",
  "FixedCost": 0,
  "FixedUpkeep": 0.0,
  "DefaultCombatState": "string",
  "ShipCategory": "string",
  "HangarDesignation": "string",
  "IsShipyard": false,
  "IsOrbitalDefense": false,
  "IsCarrierOnly": false,
  "EventOnDeath": "string",
  "ModuleUIDs": [ "uid1", "uid2", "..." ],
  "Modules": [
    {
      "Pos": { "X": 0, "Y": 0 },
      "ModuleUIDIndex": 0,
      "Size": { "X": 1, "Y": 1 },
      "TurretAngle": 0,
      "ModuleRot": 0,
      "HangarShipUID": null
    }
  ]
}
```

**On-disk text format (concise):**

- Lines 1–many: `Key=Value` (Version, Name, Hull, …).
- One line: `ModuleUIDs=uid1;uid2;...`
- One line: `Modules=N`
- Comment (optional): `# gridX,gridY;moduleUIDIndex;sizeX,sizeY;turretAngle;moduleRot;hangarShipUID`
- N lines: `x,y;idx;wx,wy;turretAngle;moduleRot;hangarShipUID` (missing optional fields omitted).

**Example module line:** `2,1;0;2,2;90;1;` = grid (2,1), first UID, 2×2, turret 90°, rotation 1 (e.g. Left), no hangar.

---

## 4. Slot Verification (How It Checks If a Module Fits)

### 4.1 Entry Point (Designer UI)

**File:** `Ship_Game/GameScreens/ShipDesign/DesignModuleGrid.cs`

- **ModuleFitsAtSlot(SlotStruct slot, ShipModule module, bool logFailure)** is the main API used when placing a module (and for drawing green/red preview).

### 4.2 Steps

1. **Slot and module non-null.**
2. **Bounds:** Build **ModuleRect** = rectangle of the module at this slot:  
   `(slot.Pos.X .. slot.Pos.X + module.XSize - 1, slot.Pos.Y .. slot.Pos.Y + module.YSize - 1)`.  
   Check **IsInBounds(ModuleRect)** (within grid 0..Width-1, 0..Height-1).
3. **For each grid cell (x,y) in that rectangle:**
   - **SlotStruct target = Grid[x + y * Width].**
   - If **target == null** → slot doesn’t exist in hull → **false**.
   - If **!target.CanSlotSupportModule(module)** → **false**.

### 4.3 CanSlotSupportModule (Restriction Match)

**File:** `Ship_Game/SlotStruct.cs`

- **SlotStruct** has **HullRestrict** (from HullSlot.R).
- **CanSlotSupportModule(ShipModule module):**
  - If **module.Restrictions == IOE** or **== HullRestrict** → **true**.
  - If **module.Restrictions** is one of I, IO, O, E, IE, OE: use **IsPartialMatch(HullRestrict, module.Restrictions)** (e.g. I matches I/IO/IE; O matches O/IO/OE; E matches E/IE/OE).
  - If **module.Restrictions** is **xI** / **xIO** / **xO**: hull must be **exactly** I / IO / O.

So: **slot verification = bounds check + every covered slot exists + every covered slot’s HullRestrict is compatible with module.Restrictions.**

---

## 5. UI Binding (Drag-and-Drop vs Click-to-Place)

### 5.1 No IDragHandler on Module Icons

- **Searched:** No **IDragHandler** or **OnDrag** on module list items. The designer does **not** use Unity-style drag-and-drop for modules onto the grid.
- **Flow:** **Click** module in list → **select “active” module** → **click** on grid to **place** (or replace).

### 5.2 How UI Maps to Data

| Step | Class / File | What happens |
|------|--------------|--------------|
| Select module | **ModuleSelectScrollList** (`GameScreens/ShipDesign/ModuleSelectScrollList.cs`) | **OnItemClicked** → **Screen.SetActiveModule(module.UID, Normal, 0, …)**. |
| Active module | **ShipDesignScreen** (`ShipDesignScreen.cs`) | **ActiveModule** (ShipModule template instance). **SetActiveModule** → **SpawnActiveModule** → **CreateDesignModule** (template from ResourceManager). |
| Cursor → grid | **ShipDesignScreenInput** (`ShipDesignScreenInput.cs`) | **GetSlotUnderCursor()** = **ModuleGrid.WorldToGridPos(CursorWorldPosition2D)** then **ModuleGrid.Get(gridPos)**. So **screen/world → grid (Point)** then **SlotStruct** at that cell. |
| Place | Same | **HandlePlaceNewModule(input, SlotUnderCursor)**. If **ActiveModule != null** and slot valid: **InstallActiveModule(new SlotInstall(slotUnderCursor, ActiveModule))**. |
| Install | **ShipDesignScreen** | **SlotInstall.UpdateCanInstallTo(ModuleGrid)** calls **ModuleGrid.ModuleFitsAtSlot(Slot, Mod)**. **TryInstallTo** → **ModuleGrid.InstallModule(slot, mod)** (and mirror if symmetric). |
| Grid storage | **DesignModuleGrid** | **PlaceModule** sets **slot.ModuleUID**, **slot.Module**, **slot.Tex**; for multi-cell modules, other cells get **Parent** pointing at root slot. |

**World ↔ grid:**  
- **DesignModuleGrid.WorldToGridPos(Vector2)** = `(Floor(worldPos.X/16), Floor(worldPos.Y/16)) + GridCenter` (grid is centered).  
- **GridPosToWorld(Point)** = **WorldTopLeft + (gridPos * 16)**.

So: **UI binding is click-to-select + click-to-place**, with **WorldToGridPos** + **Get(Point)** mapping cursor to **SlotStruct**; placement then uses **ModuleFitsAtSlot** and **InstallModule**.

---

## 6. Core Classes Involved in the Ship Designer UI

| Class | File | Role |
|-------|------|------|
| **ShipDesignScreen** | `GameScreens/ShipDesign/ShipDesignScreen.cs` | Main screen; **ActiveModule**, **ModuleGrid**, **SetActiveModule**, **InstallActiveModule**, **SlotInstall**. |
| **ShipDesignScreenInput** | `GameScreens/ShipDesign/ShipDesignScreenInput.cs` | Input; **GetSlotUnderCursor**, **HandlePlaceNewModule**, **HandleModuleSelection**, arc/turret. |
| **ShipDesignScreenDraw** | `GameScreens/ShipDesign/ShipDesignScreenDraw.cs` | Draw grid, modules, **ModuleFitsAtSlot** preview (green/red), active module under cursor. |
| **DesignModuleGrid** | `GameScreens/ShipDesign/DesignModuleGrid.cs` | Grid of **SlotStruct**; **ModuleFitsAtSlot**, **PlaceModule**, **RemoveModule**, **InstallModule**, **WorldToGridPos**, **Get(Point)**. |
| **SlotStruct** | `SlotStruct.cs` | One cell in design grid; **HullRestrict**, **CanSlotSupportModule**, **Module**, **Parent** (for multi-cell). |
| **ModuleSelectScrollList** | `GameScreens/ShipDesign/ModuleSelectScrollList.cs` | List of modules; **OnItemClicked** → **SetActiveModule**. |
| **ModuleSelectListItem** | `GameScreens/ShipDesign/ModuleSelectListItem.cs` | One module entry in list. |
| **ModuleSelection** | `GameScreens/ShipDesign/ModuleSelection.cs` | Wrapper/UI for module list and active module info. |
| **ShipHull** | `Ships/ShipHull.cs` | Hull definition; **HullSlots** (HullSlot[]), **Size**, **GridCenter**. |
| **HullSlot** | `Gameplay/ModuleSlot.cs` | **Pos** (Point), **R** (Restrictions). |
| **DesignSlot** | `Gameplay/ModuleSlot.cs` | Saved slot: **Pos**, **ModuleUID**, **Size**, **TurretAngle**, **ModuleRot**, **HangarShipUID**. |
| **ShipDesign** | `Ships/ShipDesign.cs` | Blueprint; **DesignSlots**, **BaseHull**, **GridInfo**, **Grid** (ModuleGridFlyweight). |
| **ShipDesign_WriterParser** | `Ships/ShipDesign_WriterParser.cs` | **Save** (CreateShipDataText), **ParseDesignSlot**, load design from file. |
| **ShipModule** | `Ships/ShipModule.cs` | Module instance; **Pos**, **Restrictions**, **XSize**, **YSize**, **UID**. |
| **ShipModuleFlyweight** / **ShipModule_XMLTemplate** | `ShipModuleFlyweight.cs` | Template from XML; **Restrictions**, size, etc. |

---

## 7. Summary for Reuse

- **Hull:** Slots = **integer (X,Y) + Restrictions**; world = **(Pos - GridCenter) * 16**. No child objects.
- **Modules:** **Not ScriptableObjects**; XML templates + **Restrictions** (and size) define fit.
- **Blueprint:** **Key=value text + N lines** of `x,y;uidIndex;wx,wy;turret;rot;hangar`. Schema above can be used as JSON equivalent.
- **Slot verification:** **ModuleFitsAtSlot** = in bounds + every covered **SlotStruct.CanSlotSupportModule(module)** (Restrictions match).
- **UI:** **Click** list to set **ActiveModule**, **click** grid to get **SlotStruct** via **WorldToGridPos** + **Get**, then **ModuleFitsAtSlot** + **InstallModule**. No drag handler; logic is reusable without the existing UI.
