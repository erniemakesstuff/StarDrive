# StarDrive BlackBox — Persistence & Serialization Analysis

**Goal:** Determine if complex, non-deterministic data (e.g., LLM narratives) can be injected into save files.

---

## 1. Serialization Format Identification

### Main Save Files (`.sav`)

| Aspect | Value |
|--------|-------|
| **Protocol** | **Binary** — custom `BinarySerializer` |
| **Entry Point** | `SavedGame.Save()` / `SavedGame.Deserialize()` |
| **Location** | `Ship_Game\SavedGame.cs` |
| **Human-Readable?** | **No** — binary blob, not JSON/XML |

```csharp
// SavedGame.cs - Line 88-90
BinarySerializer.SerializeMultiType(writer, new object[] { header, state }, Verbose);
```

**Critical:** The primary save format is **binary**. LLM narratives cannot be inspected or edited in `.sav` files without decoding the binary schema. **Not suitable for easy debugging of LLM outputs.**

### Other Persistence Formats

| Format | Used For | Human-Readable? | Serializer |
|--------|----------|-----------------|------------|
| **YAML** | Races (`RaceSave`), New Game Setups (`SetupSave`), Fleet Designs, Blueprints | **Yes** | `YamlSerializer.SerializeOne()` / `SerializeRoot()` |
| **Custom Text** | Ship designs (`.design`), Hulls | Yes | Custom key-value parser |
| **Binary** | Main save (`.sav`) | No | `BinarySerializer` |

**No JSON, Newtonsoft.Json, JsonUtility, Protobuf, or BinaryFormatter** in the save pipeline.

---

## 2. Root State Structure

### Root Data Class

| Class | Role | Contents |
|-------|------|----------|
| **HeaderData** | Save metadata | `Version`, `SaveName`, `StarDate`, `PlayerName`, `RealDate`, `ModName`, `Time` |
| **UniverseState** | Game state | Entire universe: Empires, Ships, Planets, SolarSystems, etc. |

```csharp
// SavedGame.cs
var header = new HeaderData { Version = SaveGameVersion, SaveName = ..., StarDate = ..., ... };
BinarySerializer.SerializeMultiType(writer, new object[] { header, state }, Verbose);
```

### Object Graph vs DTO

| Approach | Used? | Notes |
|----------|-------|-------|
| **Full object graph** | **Yes** | `UniverseState` and its `[StarData]` fields serialize the actual domain objects (Empire, Ship, Planet, etc.), not DTOs. |
| **Partial DTOs** | Yes (selective) | e.g., `Ship` uses `[StarDataSerialize] OnSerialize()` to write `ModuleSaveData[]` instead of raw modules; `UniverseState.SaveState` gathers designs before save. |

**Conclusion:** Save is largely **object-graph based**. Domain objects are serialized directly with `[StarData]` fields. There is no global DTO layer; DTOs appear only in specific cases (modules, designs).

---

## 3. Schema Rigidity & Version Compatibility

### Field Matching

The binary format uses **field names** for mapping. In `TypeInfo` (BinarySerializerReader):

```csharp
// TypeInfo.cs - Field matching
f.Field = us.GetFieldOrNull(f.Name);  // Match stream field name to current type
```

### Forward Compatibility (Adding New Fields)

| Scenario | Result |
|----------|--------|
| **Add `public string DiplomaticHistory`** | Safe — old saves omit the field; stream has fewer fields; new field keeps default (e.g. `null`). |

The reader iterates over **stream fields only** (`instanceType.Fields.Length`). Extra fields in the current type are never read and retain defaults.

### Backward Compatibility (Removing Fields)

| Scenario | Result |
|----------|--------|
| **Remove a field** | Safe — `GetFieldOrNull(oldName)` returns `null`; `ReadUserClassField` skips when `fi.Field == null`. |

Unit tests in `BinarySerializerTests.cs` cover:
- `ContainsRemovedFieldTypes` — removed field is skipped
- `ContainsDeletedTypes` — deleted nested type is skipped  
- `ContainsMovedTypes` — type moved to another namespace/class still works

### Version Checks

| Version | Purpose |
|---------|---------|
| `SavedGame.SaveGameVersion` (20) | Display/metadata; not used for schema branching |
| `BinarySerializer.CurrentVersion` (1) | Binary format version; mismatch logs warning but does not block load |

### Callbacks

| Mechanism | Exists? | Purpose |
|-----------|---------|---------|
| `[OnBeforeSerialize]` | No | Not used (Unity-specific) |
| `ISerializationCallbackReceiver` | No | Not used |
| `[StarDataSerialize]` | Yes | Called before scan; can inject dynamic fields (e.g. `Ship.OnSerialize()` → `ModuleSaveData[]`) |
| `[StarDataDeserialized]` | Yes | Called after deserialization; used for fixup (e.g. `Ship.OnDeserialized(UniverseState us)`) |

---

## 4. Risk Assessment: Adding New Fields

### Can you add `public string diplomaticHistory` without breaking legacy saves?

**Yes.** The binary serializer is tolerant:

1. Old saves do not include the new field.
2. Reader processes only fields present in the stream.
3. New field remains at its default value (`null` for `string`).

### Important Constraints

- New field must be `[StarData]` on a `[StarDataType]` class.
- Type must be supported by the serializer (primitives, `string`, `Array<T>`, `Map<K,V>`, other `[StarDataType]` types).
- Avoid changing field order in a way that breaks the stream layout (stream drives iteration, not current type).
- Avoid removing or renaming fields that old saves still use, unless you accept data loss for that field.

---

## 5. LLM Narrative Injection — Feasibility

### Option A: Add Field to Existing Binary Save

| Pros | Cons |
|------|------|
| Single save file | Binary format — hard to inspect or edit LLM output |
| Forward compatible | Must re-save from game to persist; cannot easily inject from external tools |
| Simple implementation | |

**Verdict:** Technically feasible but **poor for debugging** LLM content.

### Option B: Parallel Human-Readable Sidecar

Create a companion file (e.g. `MySave.sav.narrative.yaml` or `.json`) loaded with the save:

| Pros | Cons |
|------|------|
| Human-readable | Two files to manage |
| Easy to edit/inject LLM output | Requires load logic to merge sidecar into runtime state |
| Simple schema (e.g. `EmpireId → narrative`) | |

**Verdict:** **Recommended** for LLM narratives — matches existing YAML usage (Races, Setups, Blueprints).

### Option C: Use Existing YAML Pipeline

Add a new YAML-serialized store (e.g. `NarrativeStore`) following the pattern of `RaceSave`, `SetupSave`:

| Pros | Cons |
|------|------|
| Same toolchain as Races/Setups | Separate from main save; must be associated by save name/path |
| Human-readable | |
| Proven in codebase | |

**Verdict:** Solid option if narratives are structured and reusable.

### Recommendation

For LLM narratives:

1. Prefer **Option B or C** — keep narratives in a human-readable format (YAML or JSON).
2. Use the main binary save for gameplay state only.
3. If you must store narratives inside the binary save, add an `[StarData] public string NarrativeBlob` (or similar) to a suitable type (e.g. `Empire`, `Relationship`). This will be forward compatible but not human-readable in the `.sav` file.

---

## 6. Quick Reference

| Question | Answer |
|----------|--------|
| Protocol for main save? | Binary (custom `BinarySerializer`) |
| Human-readable? | No |
| Root classes? | `HeaderData` + `UniverseState` |
| Object graph or DTO? | Mostly object graph; some DTOs (e.g. modules) |
| Add new field — breaks old saves? | No — safe, defaults applied |
| Remove field — breaks load? | No — skipped |
| `[OnBeforeSerialize]`? | No — use `[StarDataSerialize]` instead |
| `ISerializationCallbackReceiver`? | No |
| Suitable for LLM injection? | Prefer YAML/JSON sidecar; binary possible but not debuggable |
