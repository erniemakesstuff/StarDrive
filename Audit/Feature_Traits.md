# Trait and Modifier System — Factions/Races Audit

**Goal:** Document the modifier architecture, how traits are applied (e.g. "+10% Ship Speed"), and player customization (Race Creation).

---

## 1. Modifier Architecture

### 1.1 Classes Found

| Class | File | Role |
|-------|------|------|
| **RacialTrait** | `Data/RacialTrait.cs` | Race/faction stat block. Many named float fields (DodgeMod, ModHpModifier, RepairMod, InBordersSpeedBonus, etc.). Base values from race XML; then **aggregated** with selected **RacialTraitOption** values. |
| **RacialTraitOption** | `Data/RacialTraitOption.cs` | One selectable trait (e.g. "Militaristic"). Same field names as RacialTrait; values are **added** (or multiplied) into RacialTrait in **LoadTraitConstraints**. |
| **DTrait** | `EmpireData.cs` | **Diplomatic** personality (Cunning, Ruthless, etc.). Territorialism, NAPact, Alliance, Trade, Trustworthiness, etc. Used for diplomacy thresholds, not ship stats. |
| **ETrait** | `EmpireData.cs` | **Economic** personality. Name, EconomicResearchStrategy, ColonyGoalsPlus, ShipGoalsPlus. Used for AI/strategy. |
| **WeaponTagModifier** | `EmpireData.cs` | Per-**WeaponTag** (Kinetic, Beam, Missile, …) bonuses: **Speed**, **Range**, **Rate**, **Turn**, **Damage**, **ExplosionRadius**, **ShieldDamage**, **ArmorDamage**, **ShieldPenetration**, **HitPoints**, **ArmourPenetration**. All **percent or flat** as documented in code. Filled by **tech unlocks**. |
| **EmpireHullBonuses** | `Ships/EmpireHullBonuses.cs` | **Cached** per (Empire, ShipHull): **FuelCellMod**, **PowerFlowMod**, **RepairRateMod**, **ShieldMod**, **HealthMod**. Built from **EmpireData** (Traits + tech) and **HullBonus**. |
| **PersonalityModifiers** | `EmpirePersonalityModifiers.cs` | Colonization, war, trust, etc. (personality-based). |
| **DifficultyModifiers** | `EmpireDifficultyModifers.cs` | Difficulty-level modifiers (production, research, ship cost, etc.). |

There are **no** classes named **Buff**, **Perk**, or a generic **Modifier** container. Effects are **named fields** on **RacialTrait**, **EmpireData**, or **WeaponTagModifier**.

### 1.2 Pattern: Decorator vs Stat Aggregation

**Stat aggregation** is used throughout. There is **no** decorator pattern (no wrappers around values).

- **RacialTrait:** Base values from race XML (e.g. `Races/MyRace.xml`). Then **RacialTrait.LoadTraitConstraints** runs over selected **RacialTraitOption** entries and:
  - **Additive:** `DiplomacyMod += trait.DiplomacyMod`, `DodgeMod += trait.DodgeMod`, `ModHpModifier += trait.ModHpModifier`, etc.
  - **Multiplicative:** `ExploreDistanceMultiplier *= trait.ExploreDistanceMultiplier`, `ConstructionRateMultiplier *= trait.ConstructionRateMultiplier`, `EnvTerran *= trait.EnvTerranMultiplier`, etc.
- **EmpireHullBonuses:**  
  `HealthMod = (1 + empire.data.Traits.ModHpModifier)`,  
  `RepairRateMod = (1 + empire.data.Traits.RepairMod) * hullBonus.RepairModifier * GlobalStats.Defaults.SelfRepairMultiplier`.  
  So final stat is **base × (1 + mod)** or **base × multiplier**.
- **Weapon (projectile):**  
  `p.DamageAmount += weaponTag.Damage * p.DamageAmount` (additive percent),  
  `p.Speed += weaponTag.Speed * ProjectileSpeed`, etc.  
  So **base + (mod × base)** = **base × (1 + mod)** for percent fields.

**Generic “modifier” data structure:** The game does **not** use a single generic type (e.g. `List<Modifier>`). Each system has its own **named floats**:

- **RacialTrait / RacialTraitOption:** One float per effect (DodgeMod, TargetingModifier, ShipCostMod, …).
- **WeaponTagModifier:** One float per weapon stat (Speed, Range, Damage, …) per WeaponTag.
- **EmpireHullBonuses:** One float per bonus type (HealthMod, RepairRateMod, ShieldMod, …).

So the **generic** idea is: **named modifiers**, applied either as **base × (1 + mod)** or **base + flat**, with no shared “Modifier” struct or decorator.

---

## 2. How a Trait Like "+10% Ship Speed" Is Applied

### 2.1 In-Borders Speed (Racial)

**Trait:** **InBordersSpeedBonus** (e.g. +0.5).

**Application:** **Not** on the Ship class every frame for “ship speed” in general. It is applied to **FTL (warp) speed** when the ship is in **own borders**.

**File:** `Ship_Game/Ships/Ship_Movement.cs`

- Each frame (or when FTL state is updated), the game computes **FTLModifier**:
  - Base from **Universe.P.FTLModifier** or **1f**.
  - If in **own borders**: `ftlModifier += Loyalty.data.Traits.InBordersSpeedBonus`.
  - If in **enemy** space: `ftlModifier *= Universe.P.EnemyFTLModifier`.
- **FTLModifier** is then stored on the ship and used for warp speed (e.g. in **ShipStats**: `WarpThrust / mass * e.data.FTLModifier`).

So: **Ship does not** read `Player.Traits` every frame for all stats. For this trait it **does** read **Loyalty.data.Traits.InBordersSpeedBonus** when updating FTL state, and the result is cached in **FTLModifier** until the next such update.

### 2.2 Other Examples: Cached vs Read-at-Use

| Effect | Where applied | Cached on spawn? |
|--------|----------------|------------------|
| **ModHpModifier** (module HP) | **EmpireHullBonuses.HealthMod** = (1 + ModHpModifier). Used in **ShipModule.ActualMaxHealth** = TemplateMaxHealth × Bonuses.HealthMod. | **Yes.** Module is created with **EmpireHullBonuses.Get(Loyalty, hull)**; Health and ActualMaxHealth set at creation. Refreshed only when **EmpireHullBonuses.RefreshBonuses(empire)** is called (e.g. tech change). |
| **RepairMod** | **EmpireHullBonuses.RepairRateMod**; used for ship repair rate. | **Yes** (in bonus object). |
| **DodgeMod** | **Ship.DodgeMultiplier()** returns `1 + Loyalty.data.Traits.DodgeMod`. | **No.** Read from **Loyalty.data.Traits** when the getter is called (e.g. when resolving hits). |
| **TargetingModifier** | Used in targeting/combat logic (empire-level). | Read at use. |
| **Ship cost** | **ShipDesign_Stats** / cost calculation: `cost += cost * e.data.Traits.ShipCostMod`. | N/A (design/construction time). |
| **Weapon damage/speed** | **Weapon.AddModifiers**: uses **Owner.Loyalty.data.WeaponTags[tag]** (Speed, Damage, etc.). | **No.** Read when firing (projectile is built with modifiers applied). |

So: **mix of cached (HP, repair, hull bonuses) and read-at-use (dodge, FTL context, weapon tags).** The Ship does **not** “check Player.Traits every frame” for everything; it checks when the specific stat is needed, and some stats are baked in at spawn via **EmpireHullBonuses**.

---

## 3. How the Game Calculates a Final Stat (e.g. Ship Attack)

There is **no** single formula that combines “Hull + Tech + Race” for one “Ship Attack” value. Different stats are combined at different points.

### 3.1 Ship “Strength” (Combat Rating)

- **BaseStrength** (design): From **ShipBuilder.GetModifiedStrength(surfaceArea, offensiveSlots, offense, defense)** — **no** trait or tech in that function. It’s **offense + defense** (or a variant for low-offense ships).
- **Offense/defense** come from summing module contributions (weapons, armor, shields, etc.) at **design** time. So “attack” at design level is **hull + modules only**; race/tech don’t change BaseStrength directly.
- At **runtime**, weapon damage is modified by **WeaponTagModifier** (tech) when the weapon fires (see below). Race traits affect **dodge**, **module HP**, **repair**, etc., not a single “attack” number.

### 3.2 Weapon Damage (Hull + Tech + Race)

- **Base:** Weapon template (from module XML): base damage, range, speed, etc. Hull defines **slots**; design places **weapons** (modules) on the hull.
- **Tech:** **EmpireData.WeaponTags[WeaponTag]** (e.g. Kinetic, Beam). Tech unlocks add to these (e.g. **TechEntry.ApplyWeaponTagBonusToEmpire**). When a **Weapon** fires, **AddModifiers** does:
  - `p.DamageAmount += weaponTag.Damage * p.DamageAmount` (percent)
  - `p.Speed += weaponTag.Speed * ProjectileSpeed`
  - plus Range, Turn, HitPoints, ShieldDamage, ArmorDamage, etc.
- **Race:** No direct “race trait” applied in **AddModifiers**. Race affects **Pack** (e.g. **PackDamageModifier** applied elsewhere), **DodgeMod** (defense), **ModHpModifier** (module/ship survivability), not the weapon’s damage formula in **Weapon.cs**.

So for **weapon damage**:

- **Final damage** ≈ **base damage (from hull/module design)** × **(1 + WeaponTag.Damage)** + other flat/percent from WeaponTag + Pack/other if applicable.
- **Hull** = which slots and thus which weapons; **Tech** = **WeaponTags**; **Race** = indirect (dodge, HP, pack, etc.).

### 3.3 Module HP (Hull + Race + Tech)

- **Template:** **ShipModule.TemplateMaxHealth** from XML.
- **EmpireHullBonuses.HealthMod** = `(1 + empire.data.Traits.ModHpModifier)`. (Tech can change empire.data; then **RefreshBonuses** updates cache.)
- **ShipModule.ActualMaxHealth** = **TemplateMaxHealth × Bonuses.HealthMod**.
- So: **final module HP** = **template (design) × (1 + ModHpModifier)**. Hull defines layout; race (and any tech in empire.data) feeds **ModHpModifier**; result is cached at module creation.

### 3.4 Summary Formula (Conceptual)

- **Weapon damage (per shot):**  
  `baseDamage × (1 + WeaponTag.Damage) + flat` (and similar for speed/range).  
  Base from **design** (hull + modules); **WeaponTag** from **tech**; **race** not in this formula.
- **Module max HP:**  
  `TemplateMaxHealth × (1 + Traits.ModHpModifier)`.  
  **Hull** (slots) + **race** (trait); tech only if it changes **Traits** and bonuses are refreshed.
- **Dodge:**  
  `1 + Traits.DodgeMod` when the getter is called.
- **FTL in borders:**  
  `ftlModifier += Traits.InBordersSpeedBonus` when updating FTL state.

So the **generic** pattern is: **base (from design/hull/module) × (1 + mod)** or **base + mod**, with **mod** coming from **RacialTrait**, **WeaponTagModifier**, or **EmpireHullBonuses**, and no single “Ship Attack” pipeline that combines all three in one place.

---

## 4. Customization: Can Players Pick Traits?

**Yes.** Race creation / trait pick is implemented.

### 4.1 Where

- **Screen:** **RaceDesignScreen** (`GameScreens/NewGame/RaceDesignScreen.cs`).
- **Data:** **AllTraits** = list of **TraitEntry**; each **TraitEntry** has **Trait** (RacialTraitOption) and **Selected** (bool). Player toggles traits in the UI (e.g. **TraitsList** / **TraitsListItem**).
- **Output:** **GetRacialTraits()** builds a **RacialTrait** clone and sets **TraitSets[0].TraitOptions** to the list of **TraitName** of selected **TraitEntry**s. That **RacialTrait** is what gets passed to empire creation (e.g. **CreatingNewGameScreen** / **UniverseGenerator**).

### 4.2 When Traits Are Applied to the Race

- When the empire is **created** (e.g. in **UniverseState_Empires** or equivalent), **data.Traits.LoadTraitConstraints(isPlayer, Random, data.Name, …)** is called.
- **LoadTraitConstraints** loads **TraitSets** (e.g. player’s chosen set or AI random set), then for each **RacialTraitOption** whose **TraitName** is in the selected set, **adds** (or multiplies) that option’s values onto **RacialTrait** (DiplomacyMod, DodgeMod, ModHpModifier, etc.). So the **RacialTrait** instance that the empire uses is **base race + selected options**.

### 4.3 Constraints

- **TraitEntry** can have **ExcludedBy** (traits that exclude this one).
- **RacialTraitOption** has **Excludes** (and **Cost** for point-buy if used). So the game supports **trait pick with exclusions** and optional cost.

---

## 5. Generic “Modifier” Data Structure (Summary)

The codebase does **not** expose one generic “Modifier” type. Conceptually:

- **RacialTrait / RacialTraitOption:** Many **named float** fields. One namespace (DodgeMod, ShipCostMod, RepairMod, …). Aggregation: **base (race XML) + sum of selected option values** (or *= for some).
- **WeaponTagModifier:** **Named floats per WeaponTag** (Speed, Range, Damage, …). Applied as **base + mod × base** (percent) or **base + flat**.
- **EmpireHullBonuses:** **Named multiplier floats** (HealthMod, RepairRateMod, …) = **f(empire.data.Traits.*, hull, global)**. Applied as **base × multiplier**.

So the **generic** idea is:

- **Modifier** = a **named value** (no shared struct; names are fixed in code).
- **Application** = **base × (1 + mod)** or **base × mult** or **base + flat**, depending on the stat.
- **Source** = **RacialTrait** (race + selected options), **EmpireData** (tech, difficulty), **WeaponTagModifier** (tech per weapon tag), **EmpireHullBonuses** (cached combo of empire + hull).

---

## 6. File Reference

| Purpose | File |
|---------|------|
| Race trait definition and aggregation | `Data/RacialTrait.cs` (RacialTrait, LoadTraitConstraints, ApplyTraitToShip) |
| Selectable trait option (schema) | `Data/RacialTraitOption.cs` |
| Diplomatic / economic personality | `EmpireData.cs` (DTrait, ETrait) |
| Weapon-type modifiers (tech) | `EmpireData.cs` (WeaponTagModifier, WeaponTags) |
| Cached hull+empire bonuses | `Ships/EmpireHullBonuses.cs` |
| Apply weapon modifiers when firing | `Gameplay/Weapon.cs` (AddModifiers) |
| FTL / in-borders speed | `Ships/Ship_Movement.cs` |
| Dodge, explore distance, etc. | `Ships/Ship.cs`, `Ships/ShipStats.cs` |
| Module HP from bonuses | `Ships/ShipModule.cs` (ActualMaxHealth, Bonuses.HealthMod) |
| Race creation UI | `GameScreens/NewGame/RaceDesignScreen.cs`, `TraitsListItem.cs`, `TraitEntry.cs` |
| Trait list (data) | `Gameplay/RacialTraits.cs` (RacialTraits.TraitList), `Gameplay/TraitEntry.cs` |
