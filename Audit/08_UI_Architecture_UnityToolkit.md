# UI Architecture: Unity UI Toolkit Migration

**Role:** UI Architect  
**Context:** Legacy UI is immediate-mode XNA (`SpriteBatch.Draw`); logic is tightly coupled (e.g. `UniverseScreen.HandleInput` → `Ship.AI.Order*`). Target: **Unity UI Toolkit (USS/UXML)** with **ECS/DOTS** simulation and a clear **Data Binding + Command** layer.

**Reference:** Audit 03 (Systems Logic) — UI & Input coupling; Feature_Diplomacy, Feature_DiploScreen; ScreenManager, ShipMoveCommands, DiplomacyScreen.

---

## 1. View Model Definition (Data Binding Layer)

Instead of screens **polling** `Empire.data`, `Relationship`, `Planet`, etc., the simulation (or an ECS-to-UI bridge) **pushes** snapshot structs into ViewModels. The UI Document **binds to these ViewModels only**; it never holds references to `Empire`, `Ship`, or `Relationship`.

### 1.1 DiplomacyViewModel (from Audit 03 + DiplomacyScreen usage)

DiplomacyScreen currently reads: `Us`, `Them`, `UsAndThem`, `ThemAndUs` (Relationship), treaty flags, Trust, TotalAnger, Threat, War state, offers, dialog state, allied empires. Define a **struct** that is written by the bridge and read by the Diplomacy UXML:

```csharp
// Pseudo-code: ECS / game state pushes into this; UI Toolkit binds to it.
public struct DiplomacyViewModel
{
    // Identifiers (for debugging / analytics only; UI uses display names)
    public int UsEmpireId;
    public int ThemEmpireId;

    // Display
    public string UsName;
    public string ThemName;
    public string ThemPortraitOrVideoPath;  // was Empire.data.Traits.VideoPath
    public bool   AtWarWithThem;
    public string DialogStateName;         // e.g. "Declare War Imperialism"
    public string TheirDialogLine;         // localised line from dialogue system

    // Relation snapshot (from Relationship — no reference to Relationship)
    public float  Trust;                   // ThemAndUs.Trust
    public float  TotalAnger;              // ThemAndUs.TotalAnger
    public float  Threat;                  // ThemAndUs.Threat
    public int    TurnsKnown;
    public int    TurnsAllied;
    public bool   Treaty_Peace;
    public bool   Treaty_NAPact;
    public bool   Treaty_Trade;
    public bool   Treaty_OpenBorders;
    public bool   Treaty_Alliance;
    public int    PeaceTurnsRemaining;
    public bool   HaveRejected_Alliance;
    public bool   HaveRejected_NAPact;
    public bool   HaveRejected_TRADE;
    public bool   HaveRejected_OpenBorders;

    // War (if AtWarWithThem)
    public int    WarState;                // enum as int: Dominating, LosingBadly, etc.
    public float  WarGrade;                // 1–10

    // Offer being negotiated (current panel state)
    public OfferViewModel OurOffer;
    public OfferViewModel TheirOffer;
    public int    OurAttitude;             // Pleading / Respectful / Threaten
    public bool   DeclareWarEnabled;
    public bool   AcceptEnabled;
    public bool   RejectEnabled;

    // Third-party (for "Discuss" / "They are allied with X")
    public int[]  AlliedEmpiresAtWarIds;   // empire IDs
    public int[]  EmpiresTheyAreAlliedWithIds;
    public string[] AlliedEmpiresAtWarNames;
    public string[] EmpiresTheyAreAlliedWithNames;
}

public struct OfferViewModel
{
    public bool NAPact, TradeTreaty, OpenBorders, PeaceTreaty, Alliance;
    public string[] TechnologiesOffered;
    public string[] ColoniesOffered;
    public string[] ArtifactsOffered;
    public string[] EmpiresToWarOn;
    public string[] EmpiresToMakePeaceWith;
}
```

**Flow:** A **DiplomacyStateSystem** (or equivalent) runs when the diplomacy context is active; it reads ECS/legacy `Empire` + `Relationship` and writes **DiplomacyViewModel** into a **single** shared buffer or **IGameStateObserver** (see §4). The Diplomacy UIDocument’s root has a **binding** to that ViewModel (e.g. via a `MonoBehaviour` or UI Toolkit data binding) and refreshes labels, buttons, and sliders from it. **No** `Empire` or `Relationship` references in the view.

### 1.2 Other ViewModels (by screen)

- **ShipInfoViewModel** — selected ship: name, hull, role, health, orders, stance, cargo, waypoints (IDs/positions). Populated from ECS Ship + AI state.
- **PlanetInfoViewModel** — selected planet: name, owner, population, food/prod/res bars, build queue, garrison. From ECS/Planet.
- **BudgetViewModel** — tax rate, treasury, income breakdown, maintenance, trade totals. From Empire aggregates.
- **UniverseHUDViewModel** — resources, date, pause state, selected entity summary. From UniverseState + Player.

All follow the same rule: **ECS/simulation writes; UI reads**. No UI → game object references for simulation data.

---

## 2. Command Pattern: DOTS Input Components

Audit 03 identified that **HandleInput** calls `Ship.AI.Order*`, `Ship.OrderToOrbit`, `Player.AI.AddGoalAndEvaluate` directly. Replace with **commands** that the simulation consumes: UI only **creates entities** (or command buffers) with **IComponentData** describing the intent.

### 2.1 Draft: DOTS Input / Command Components

Commands are **intent**, not direct method calls. The **Input/Command System** creates entities with one of these; a **ShipOrderSystem** (or equivalent) reads them and performs the actual `OrderMove`, `OrderAttack`, etc., against the simulation.

```csharp
// All are IComponentData; lifetime one frame or until processed.
// Target and subject are Entity or stable ID (e.g. ShipId, PlanetId).

public struct OrderMoveCommand : IComponentData
{
    public EntityId SubjectShip;   // or Entity
    public float2   WorldPosition;
    public int      MoveOrderType; // Regular, AddWayPoint, Aggressive, StandGround
}

public struct OrderAttackCommand : IComponentData
{
    public EntityId SubjectShip;
    public EntityId TargetShip;
    public bool     Queue;         // queue vs replace
}

public struct OrderOrbitCommand : IComponentData
{
    public EntityId SubjectShip;
    public EntityId TargetPlanet;
    public bool     ClearOrders;
    public int      StanceType;
}

public struct OrderBombardCommand : IComponentData
{
    public EntityId SubjectShip;
    public EntityId TargetPlanet;
    public bool     ClearOrders;
}

public struct OrderColonizeCommand : IComponentData
{
    public EntityId ColonyShip;
    public EntityId TargetPlanet;
    public bool     ClearOrders;
}

public struct OrderEscortCommand : IComponentData
{
    public EntityId SubjectShip;
    public EntityId TargetShip;
}

public struct OrderTroopToShipCommand : IComponentData
{
    public EntityId TroopShip;
    public EntityId TargetShip;
}

public struct OrderScrapCommand : IComponentData
{
    public EntityId SubjectShip;
}

public struct OrderExploreCommand : IComponentData
{
    public EntityId SubjectShip;
}

// Empire-level (e.g. from colony screen or diplomacy)
public struct MarkForColonizationCommand : IComponentData
{
    public EntityId Planet;
    public EntityId Empire;
    public bool     IsManual;
}
```

Optional: a **tag** component to distinguish “player-issued” so the simulation can apply permissions or logging:

```csharp
public struct PlayerIssuedCommand : IComponentData { }
```

### 2.2 How UI Toolkit "OnClick" Creates the Entity

- **No** direct call to `Ship.AI` or `Empire.AI` from the UI.
- The UI holds only **context**: e.g. “selected ship entity ID”, “selected target entity ID”, “current stance modifier” (from a small UI state or from ViewModel).

**Flow:**

1. User clicks “Move” (or right-clicks on map) → UI event handler runs.
2. Handler has access to:
   - **Selection state** (e.g. `SelectedShipEntityId`, `SelectedPlanetEntityId` — from **IGameStateObserver** or a selection ViewModel).
   - **Current modifiers** (Queue, Ctrl, Alt — from input or UI toggles).
3. Handler calls a **CommandFactory** or **InputCommandBuffer** on the **main thread** (or a thread-safe queue consumed by the simulation):

```csharp
// Pseudo-code: called from UI Toolkit button / right-click handler
void OnMoveToPositionClicked(Vector2 worldPos)
{
    var cmd = new OrderMoveCommand
    {
        SubjectShip   = CurrentSelection.ShipEntityId,
        WorldPosition = worldPos,
        MoveOrderType = (int)(Input.Queue ? MoveOrder.AddWayPoint : MoveOrder.Regular) | (int)GetStanceFromModifiers()
    };
    CommandBuffer.Enqueue(cmd);  // or: EntityManager.CreateEntity(typeof(OrderMoveCommand), typeof(PlayerIssuedCommand)).SetComponentData(cmd);
}
```

4. **Simulation** (same or next frame): a **ProcessPlayerCommandsSystem** or **ShipOrderSystem** iterates entities with `OrderMoveCommand` (and optionally `PlayerIssuedCommand`), resolves `SubjectShip` to the actual ship, calls the equivalent of `ship.AI.OrderMoveTo(...)`, then **destroys** the command entity.

So: **UI Toolkit OnClick** → **enqueue / create command entity** → **simulation system** applies the order. UI never touches `Ship.AI`.

---

## 3. Screen Stack: XNA ScreenManager → Unity Strategy

### 3.1 XNA Behaviour (from codebase)

- **Storage:** `Array<GameScreen> GameScreens`; **PendingScreens** (queue) for thread-safe add.
- **AddScreen(screen):** push to `PendingScreens`; during update, dequeue and **AddScreenAndLoadContent** → append to `GameScreens`, call `LoadContent()`.
- **Current:** `GameScreens[GameScreens.Count - 1]` (top of stack).
- **Update:** iterate all screens; **Draw** iterates all (so underlying screens can be drawn if not full-screen).
- **IsPopup:** if true, screens “below” can still be drawn/updated (e.g. UniverseScreen under BudgetScreen).
- **ExitScreen / RemoveScreen:** removes one screen, UnloadContent, Dispose.
- **GoToScreen(screen, clear3D):** **ExitAll** (reverse order), then AddScreenAndLoadContent.

So: **stack of screens**; top is “current”; popups don’t replace the stack; **GoToScreen** clears entire stack and pushes one.

### 3.2 Mapping to Unity (Scene Management or UI Document stack)

**Option A — Single scene + UI Document stack (recommended for 4X HUD/menus)**

- One **Game** scene; **UIDocument** (or multiple) per “screen” (Universe HUD, Colony, Diplomacy, Budget, etc.).
- A **ScreenStack** service (MonoBehaviour or ECS singleton):
  - **Push(screenId or UIDocument):** add to a `List<ScreenEntry>`, set the top one **visible** (and optionally disable the one below for input).
  - **Pop():** remove top, show next.
  - **GoTo(screenId):** clear list, push one.
- **ScreenEntry** can hold: UIDocument reference, optional PanelSettings (for overlay vs full-screen), IsPopup (whether to render/draw the one below).
- Draw order: render stack from bottom to top (or use Unity’s sort order / panel order). No scene load for opening Budget/Diplomacy; only show/hide and bind ViewModel.

**Option B — Scene per “major” screen**

- Main menu = one scene; Universe = one scene; Colony = one scene. Use **SceneManager.LoadSceneAsync** and optionally **Additive** for overlay scenes.
- “Pop” = UnloadScene (overlay) or LoadScene back to Universe. **GoToScreen** = LoadScene(NewScreen).
- ViewModels still provided by a bridge (e.g. **IGameStateObserver**); scene contains only UIDocuments and bindings.

**Recommendation:** Use **Option A** for in-game screens (Universe, Colony, Diplomacy, Budget, Ship List, etc.) so that no scene reload is needed and the same **IGameStateObserver** can feed all of them. Use **Option B** only for main menu vs in-game transitions if you want a clear scene boundary.

---

## 4. IGameStateObserver: ECS Data → UI Toolkit Bridge

This interface is the **contract** for “simulation → UI” data. The **simulation** (or a dedicated bridge system) **pushes** state into it; the **UI** (or a presenter) **subscribes** or **polls** it to update ViewModels and enable/disable controls. No UI code holds **Empire** or **Ship** references for simulation data.

### 4.1 Pseudo-code interface

```csharp
/// <summary>
/// Bridge: ECS / game state → UI. Simulation (or a sync system) writes;
/// UI layer reads only through this. No direct references to Empire, Ship, Relationship in UI.
/// </summary>
public interface IGameStateObserver
{
    // ---- Selection (UI reads; simulation or selection system writes) ----
    EntityId? SelectedShipEntityId { get; }
    EntityId? SelectedPlanetEntityId { get; }
    EntityId[] SelectedFleetEntityIds { get; }
    void SetSelection(EntityId? ship, EntityId? planet, EntityId[] fleet);

    // ---- ViewModels: simulation pushes snapshots for the active context ----
    void SetDiplomacyState(in DiplomacyViewModel state);
    void SetShipInfoState(in ShipInfoViewModel state);
    void SetPlanetInfoState(in PlanetInfoViewModel state);
    void SetBudgetState(in BudgetViewModel state);
    void SetUniverseHUDState(in UniverseHUDViewModel state);
    void ClearScreenState(ScreenKind kind);  // when a screen closes

    // ---- Commands: UI enqueues intent; simulation consumes ----
    void EnqueueCommand(OrderMoveCommand cmd);
    void EnqueueCommand(OrderAttackCommand cmd);
    void EnqueueCommand(OrderOrbitCommand cmd);
    void EnqueueCommand(OrderBombardCommand cmd);
    void EnqueueCommand(OrderColonizeCommand cmd);
    void EnqueueCommand(OrderEscortCommand cmd);
    void EnqueueCommand(OrderTroopToShipCommand cmd);
    void EnqueueCommand(OrderScrapCommand cmd);
    void EnqueueCommand(OrderExploreCommand cmd);
    void EnqueueCommand(MarkForColonizationCommand cmd);
    // Or a single generic: void EnqueueCommand<T>(T cmd) where T : ICommand;

    // ---- Screen stack (optional: can live on a separate service) ----
    void PushScreen(ScreenKind kind, object param = null);
    void PopScreen();
    void GoToScreen(ScreenKind kind, object param = null);
    ScreenKind CurrentScreen { get; }
}

public enum ScreenKind
{
    None,
    Universe,
    Colony,
    Diplomacy,
    Budget,
    ShipList,
    PlanetList,
    Research,
    ShipDesign,
    MainMenu,
    // ...
}
```

### 4.2 Implementation sketch (who writes / who reads)

- **Writer:** A **SyncGameStateToObserverSystem** (or a single “UI bridge” step after ECS updates) runs when the game is not paused or when a given screen is open. It:
  - Reads ECS/legacy **Empire**, **Relationship**, **Ship**, **Planet** for the **current context** (e.g. open Diplomacy with Them = X).
  - Fills **DiplomacyViewModel** (or other ViewModel) and calls **SetDiplomacyState(...)**.
  - Updates selection: **SetSelection(shipId, planetId, fleetIds)** from the selection system.
- **Reader:** Each UIDocument’s presenter (e.g. **DiplomacyPresenter**) subscribes to or polls **IGameStateObserver** for **DiplomacyViewModel** and applies it to the UI (labels, progress bars, button enabled state). Button clicks call **EnqueueCommand(...)** with the appropriate command and current selection from the observer.

### 4.3 Flow summary

1. **ECS/simulation** updates; then **SyncGameStateToObserverSystem** (or equivalent) writes ViewModels and selection into **IGameStateObserver**.
2. **UI Toolkit** (via presenters) reads only from **IGameStateObserver** and updates visual elements; **OnClick** calls **EnqueueCommand** with no reference to **Ship** or **Empire**.
3. **ProcessPlayerCommandsSystem** (or similar) consumes enqueued commands, resolves entity IDs to **Ship**/Planet, and executes the corresponding **Order*** or **AddGoal** logic inside the simulation.

This yields a clear **ECS Data → IGameStateObserver → UI Toolkit** pipeline and **UI → Command → ECS** path, with no direct UI → **Ship.AI** or **Empire.data** coupling.

---

*Document generated for migration planning; implementation details (EntityId vs Entity, threading, and exact ViewModel fields) should be aligned with the final ECS and UI Toolkit setup.*
