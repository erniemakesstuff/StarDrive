# Economic and Logistics Simulation — Static Analysis Audit

**Scope:** Resource definitions, income calculation, trade routes, global treasury, maintenance/upkeep, tax rate, and ship range/supply limits.

---

## 1. Resource Definition

### 1.1 Goods (Colony / Cargo Types)

**Location:** `Ship_Game/Good.cs`

- **Enum `Goods`:** `None`, `Production`, `Food`, `Colonists`. These are the abstract types for colony sliders and freighter cargo.
- **Class `Good`:** Data loaded from `/Goods/Goods.yaml` (via `ResourceManager.TransportableGoods`). Holds `RefiningRatio`, `MaxRichness`, `NameIndex`, `Weight`, `UID`, `IsGasGiantMineable`, `ExoticBonusType`, etc. Used for mining/exotic goods and cargo IDs; colony economy uses the enum only.

**Colony resources (per-planet):**

- **`ColonyResource`** (abstract, `Universe/SolarBodies/ColonyResource.cs`): Base for Food, Production, Research. Tracks:
  - **Percent** [0–1]: worker allocation
  - **GrossIncome** / **NetIncome**: per-turn before/after tax and consumption
  - **GrossMaxPotential** / **NetMaxPotential**: same at 100% allocation
  - **FlatBonus**, **YieldPerColonist**, **Tax** (from empire/planet)
- **`ColonyFood`**: Yield = `FoodYieldFormula(fertility, plusPerColonist)` = `fertility * (1 + plusPerColonist)`. Consumption for non-cybernetic; no tax.
- **`ColonyProduction`**: Yield = `ProdYieldFormula(richness, plusPerColonist, owner)` = `richness * (1 + plusPerColonist) * (1 + owner.data.Traits.ProductionMod)`. Tax from `Owner.data.TaxRate` (halved for AI non-player cybernetic). Consumption for cybernetic.
- **`ColonyResearch`**: From buildings only (`PlusResearchPerColonist`, `PlusFlatResearchAmount`). Tax = `TaxRate * ResearchTaxMultiplier`.
- **`ColonyMoney`** (same file): Not a “resource” in the slider sense; holds planet-level **credits**: GrossRevenue, NetRevenue, Maintenance, TaxRate, TroopMaint, etc.

**Money** is the only empire-wide numeric resource stored centrally (see §3). Food/Production/Research are per-planet and aggregated only for display or AI (e.g. `NetPlanetIncomes`).

---

## 2. Income Calculation

### 2.1 Planet Contribution: `Planet.UpdateIncomes()` → `ColonyResource.Update()`

**Location:** `Universe/SolarBodies/Planet/Planet.cs` (UpdateIncomes), `Universe/SolarBodies/ColonyResource.cs` (Update).

**Flow:**

1. **Consumption** = `(ConsumptionPerColonist * PopulationBillion) + TotalTroopConsumption`.
2. **Food.Update(** consumption **)** / **Prod.Update(** consumption **)** / **Res.Update(0)**:
   - **RecalculateModifiers()**: buildings add `PlusFoodPerColonist`, `PlusFlatFoodAmount`, etc.; then:
     - **GrossMaxPotential** = `YieldPerColonist * Planet.PopulationBillion`
     - **GrossIncome** = `FlatBonus + Percent * GrossMaxPotential`
     - **NetIncome** = `AfterTax(GrossIncome) - consumption`  
       where `AfterTax(x) = x - x*Tax`.
3. **Money.Update()** (ColonyMoney):
   - **TaxRate** = `Owner.data.TaxRate` × building tax modifiers.
   - **GrossRevenue** = `((PopulationBillion * IncomePerColonist) + IncomeFromBuildings) * TaxRate` (then × ExoticCreditsBonus if > 0).
   - **NetRevenue** = **GrossRevenue** − **Maintenance** (buildings + troop maint), with Maintenance × `Owner.data.Traits.MaintMultiplier`.

So planet **output** is:

- **Food/Prod/Res:**  
  **NetIncome** = **AfterTax(GrossIncome)** − consumption,  
  **GrossIncome** = FlatBonus + Percent × (YieldPerColonist × PopulationBillion).
- **Credits (planet):**  
  **NetRevenue** = GrossRevenue − Maintenance;  
  **GrossRevenue** = [(PopulationBillion × IncomePerColonist) + IncomeFromBuildings] × TaxRate (× exotic credits bonus).

Excess food/prod remainders are taxed at `TaxRate` and added to **ExcessGoodsIncome** (see Planet.ApplyResources).

### 2.2 Central “Income Calculation” (Empire)

**Location:** `Empire.cs` (properties and `DoMoney()`).

**Gross income (empire):**

```text
GrossIncome = GrossPlanetIncome
            + TotalTradeMoneyAddedThisTurn
            + ExcessGoodsMoneyAddedThisTurn
            + data.FlatMoneyBonus
            + TotalMoneyLeechedLastTurn
```

- **GrossPlanetIncome:** Sum of all owned planets’ `Money.GrossRevenue` (set in `UpdateNetPlanetIncomes()`).
- **TotalTradeMoneyAddedThisTurn:** From freighters delivering cargo; `TaxGoods()` adds taxed/mercantile/inter-empire tariff to `TradeMoneyAddedThisTurn`; `UpdateTradeIncome()` sets `TotalTradeMoneyAddedThisTurn = TotalTradeTreatiesIncome() + TradeMoneyAddedThisTurn`.
- **ExcessGoodsMoneyAddedThisTurn:** Sum of planets’ `ExcessGoodsIncome` (tax on excess food/prod remainders).
- **TotalMoneyLeechedLastTurn:** Espionage money leeched from this empire by others.

**Spending:**

```text
AllSpending = BuildingAndShipMaint
            + MoneySpendOnProductionThisTurn
            + TroopCostOnPlanets
            + EspionageCostLastTurn
```

- **BuildingAndShipMaint** = **TotalBuildingMaintenance** + **TotalShipMaintenance**.
- **TotalBuildingMaintenance** = `GrossPlanetIncome - (NetPlanetIncomes + TroopCostOnPlanets)` (i.e. gross − net planet revenue − troop maint = building maint).
- **TotalShipMaintenance:** Sum of `ship.GetMaintCost()` for all owned ships and projectors (see §3.2).

**Net income and turn application:**

```text
NetIncome = GrossIncome - AllSpending
```

Each turn, **DoMoney()**:

1. `MoneyLastTurn = Money`
2. `UpdateTradeIncome()` then `UpdateNetPlanetIncomes()` then `UpdateShipMaintenance()` (and related)
3. `remainingMoney = MoneyAfterLeech(NetIncome)` (subtract espionage leech to other empires)
4. `AddMoney(remainingMoney - EspionageCostLastTurn)`

So the **central income formula** is:

**Empire net change this turn** = **MoneyAfterLeech(GrossIncome − AllSpending) − EspionageCostLastTurn**,  
with **GrossIncome** and **AllSpending** as above.

### 2.3 Tax Rate

- **Empire:** `Empire.data.TaxRate` (0–1). Used in:
  - Planet **ColonyMoney** (revenue and building tax modifiers).
  - Planet **ColonyProduction** (and optionally Food) as `Tax` in `AfterTax(GrossIncome)`.
  - **Excess goods tax** (food/prod remainder) in `Planet.ApplyResources()`.
  - **Trade:** `TaxGoods()` uses `data.TaxRate` for mercantilism tax and traits.
- **Budget screen:** Tax slider and “Treasury Goal”; optional **AutoTaxes** hands control to AI economic planner.

---

## 3. Global Resource Storage and Maintenance

### 3.1 Class Responsible for Global Resource Storage

**Empire** is the only global resource holder:

- **Money:** `[StarData] public float Money` (backed by `MoneyValue`). Modified by **AddMoney(float)**. No separate “Treasury” or “PlayerEconomy” class; the empire *is* the treasury.
- **NetPlanetIncomes,** **GrossPlanetIncome,** **TotalShipMaintenance,** etc. are cached aggregates recomputed in **UpdateNetPlanetIncomes()** / **UpdateShipMaintenance()** and used for **NetIncome** / **GrossIncome** / UI (e.g. BudgetScreen).

Food/Production/Research are not stored at empire level; they exist only as per-planet **ColonyResource** state and **Planet.Storage** (ColonyStorage). Empire-level “income” for credits is the sum of planet **Money.NetRevenue** plus trade and other bonuses.

### 3.2 Maintenance and Upkeep

**Building maintenance:**  
Per building: `Building.Maintenance` (and `ActualMaintenance(planet)` with planet-specific logic). Summed in **ColonyMoney.Update()** and multiplied by **Owner.data.Traits.MaintMultiplier**. Also **TroopMaint** = troop count × **ShipMaintenance.TroopMaint** (0.1). Planet-level **SpaceDefMaintenance** and **GroundDefMaintenance** from orbitals and military buildings.

**Ship maintenance:**  
**Location:** `Ships/ShipMaintenance.cs` — **GetMaintenanceCost(Ship, Empire, troopCount)** → **GetBaseMaintenance(IShipDesign, Empire, numTroops)**.

- **Remnants / prototype ship:** 0.
- If **FixedUpkeep** > 0: use it.
- Else:
  - **UseUpkeepByHullSize** (Universe.P):  
    **maint** = (BaseHull.SurfaceArea + hangars area) × **0.01**
  - Else:  
    **maint** = ship **GetCost(empire)** × **0.004**
- Role multipliers (e.g. capital 0.5, corvette 0.9, station/platform 0.35/0.7, troop 0.5). Freighter/platform/station × **CivMaintMod**; if **Privatization** × 0.5.
- **maint** = maint × (1 + MaintMod) × ShipMaintMultiplier + numTroops × TroopMaint.
- If not subspace projector: **maint** × **Universe.P.ShipMaintenanceMultiplier**.
- Shipyards: × 0.4.

**Deduction:** Ship maintenance is not deducted in one place; it is included in **AllSpending**. So **NetIncome** already subtracts it, and **AddMoney(remainingMoney - EspionageCostLastTurn)** is the single place where treasury changes (after leech).

---

## 4. Trade Routes

### 4.1 How Trade Routes Are Stored: List (per Ship), Not Graph

- **Storage:** Each **Ship** (freighter) has **`[StarData] public Array<int> TradeRoutes`** — a **list of planet IDs** the freighter is allowed to service (`Ship_Trade.cs`). No global graph of “trade routes” between nodes.
- **Semantics:**  
  - **AddTradeRoute(Planet)** adds `planet.Id` if owner or trade-treaty or mineable/researchable.  
  - **RemoveTradeRoute(Planet)** removes that ID.  
  - **IsValidTradeRoute(Planet)** = `TradeRoutes.IsEmpty || TradeRoutes.Contains(planet.Id)`.  
  - **InTradingZones(Planet)** combines **AreaOfOperation** and **TradeRoutes**: if ship has both AO and trade routes, planet must be in AO **or** in the trade-route list; otherwise must be in AO **and** in the list.
- **Distance:** Used for **choosing** best route (e.g. **TryGetBestTradeRoute** uses **GetAstrogateTimeTo** / **GetAstrogateTimeBetween** and **TradeDistanceOk**), not for a global “connection graph.” Routes are “which planets this freighter is allowed to visit,” not edges in a graph.

So: **List of planet IDs per freighter**; no shared graph of trade connections. Empire trade logic (e.g. **Empire_Trade.DispatchOrBuildFreighters**) uses planet lists and freighter state, not a separate route graph.

### 4.2 Trade Income (Treaty-Based)

**Relationship.TradeIncome(us)** (diplomatic trade treaty, not cargo):

- **setupIncome** = (0.1 × Treaty_Trade_TurnsExisted − 3) clamped to [−3, 3].
- **demandDivisor** = 10 (alliance), 15 (open borders), else 20.
- **income** = them.MaxPopBillion / demandDivisor; **tradeCapacity** = (us.TotalPlanetsTradeValue / them.TotalPlanetsTradeValue) × (1 + Mercantile), cap 1; **maxIncome** = (income × tradeCapacity).LowerBound(3); **netIncome** = same clamp as setupIncome over [−3, maxIncome].
- Return **netIncome** (per-turn treaty trade value).

**TotalTradeMoneyAddedThisTurn** includes both this treaty income and **TradeMoneyAddedThisTurn** from **TaxGoods()** (freighter deliveries).

---

## 5. Logistics / Range: Supply and Fuel (Warp Range)

### 5.1 No “SupplyRange” or “Fuel” as a Distance Cap

- There is no **SupplyRange** constant that limits how far ships can go. **Supply** in code refers to **ShipResupply** (rearm/repair/troops) and **SupplyShuttles** (carriers resupplying other ships in range). **RepairDroneRange** (20000) and **HangarRange** (7500) are combat/support ranges, not “max distance from home.”
- **Fuel** in data: **FuelCell** modules (power storage), **FuelRefinery** building, **ReactorFuel** / **Fissionables** as cargo. These do not define a “fuel range” or “supply range” that blocks movement.

### 5.2 What Stops Ships from Going Too Far: Warp Power Duration

**Location:** `Ships/Ship_Warp.cs`, `Ships/ShipStats.cs`, `Ships/Power.cs`.

- **IsWarpRangeGood(float neededRange):**  
  `powerDuration = NetPower.PowerDuration(MoveState.Warp, PowerCurrent);`  
  **return** `ShipStats.IsWarpRangeGood(neededRange, powerDuration, MaxFTLSpeed)`.
- **ShipStats.IsWarpRangeGood(neededRange, powerDuration, maxFTLSpeed):**  
  **maxFTLRange** = **powerDuration × maxFTLSpeed**;  
  **return** **maxFTLRange >= neededRange**.

So the limit is **power (fuel cells) × warp time**, not a fixed “supply range”:

- **PowerDuration(moveState, currentPower):**  
  If **PowerStoreMax** == 0 → 0. Else **powerSupplyRatio** = PowerCurrent / PowerStoreMax; for **Warp** return **WarpPowerDuration × powerSupplyRatio**.
- **WarpPowerDuration** (and **SubLightPowerDuration**) come from **Power.Calculate()**: **PowerDuration(powerFlowMax, powerDraw, powerStore)** = powerStore / (powerDraw − powerFlowMax) when net drain > 0 (i.e. time until storage is empty at current draw).

So: **Max warp distance** ≈ **WarpPowerDuration × (PowerCurrent / PowerStoreMax) × MaxFTLSpeed**. When power is depleted, the ship cannot sustain warp; there is no separate “supply line” or “fuel range” number. Resupply (ShipResupply / SupplyShuttles) restores ordnance/repair/troops, not “fuel” for range—power is restored by **PowerFlowMax** (reactors) over time, and fuel cells only store that power.

---

## 6. Summary

| Topic | Finding |
|-------|--------|
| **Resource definition** | **Goods** enum: None, Production, Food, Colonists. **Good** class from Goods.yaml for mining/cargo. Colony resources: **ColonyFood**, **ColonyProduction**, **ColonyResearch** (per-planet); **ColonyMoney** for planet credits. |
| **Planet output** | **GrossIncome** = FlatBonus + Percent × (YieldPerColonist × PopulationBillion). **NetIncome** = AfterTax(GrossIncome) − consumption. **Money.NetRevenue** = GrossRevenue − Maintenance. |
| **Central income formula** | **NetIncome** = **GrossIncome** − **AllSpending**. **GrossIncome** = GrossPlanetIncome + TotalTradeMoneyAddedThisTurn + ExcessGoodsMoneyAddedThisTurn + FlatMoneyBonus + TotalMoneyLeechedLastTurn. **AllSpending** = BuildingAndShipMaint + MoneySpendOnProductionThisTurn + TroopCostOnPlanets + EspionageCostLastTurn. Turn: **AddMoney(MoneyAfterLeech(NetIncome) − EspionageCostLastTurn)**. |
| **Trade routes** | **List** per ship: **Ship.TradeRoutes** = **Array\<int\>** (planet IDs). No global graph. Distance used only for choosing best route (astrogate time). |
| **Global resource storage** | **Empire** holds **Money** (float). All other aggregates (NetPlanetIncomes, TotalShipMaintenance, etc.) are cached on Empire. Food/Prod/Res are only on planets. |
| **Maintenance / upkeep** | Building maintenance per planet in **ColonyMoney**; ship maintenance via **ShipMaintenance.GetBaseMaintenance** (cost or surface-area × modifiers × Universe.ShipMaintenanceMultiplier). Deducted implicitly via **NetIncome** in **DoMoney()**. |
| **Tax rate** | **Empire.data.TaxRate**; used for planet revenue, excess goods tax, and trade **TaxGoods()**. |
| **Range / “fuel”** | No SupplyRange. Ship range limit: **warp** limited by **PowerStore** (fuel cells) and **WarpPowerDuration** × **MaxFTLSpeed**. **IsWarpRangeGood(neededRange)** = (powerDuration × maxFTLSpeed) >= neededRange. |

---

*Audit completed from static analysis of C# sources; no code was executed.*
