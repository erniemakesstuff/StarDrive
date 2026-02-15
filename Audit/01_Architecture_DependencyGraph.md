# StarDrive BlackBox — Global Architecture Audit

**Engine:** MonoGame/XNA (Microsoft.Xna.Framework.Game)  
**Note:** This is **not Unity**. There are no `Awake()`, `Start()`, or `DontDestroyOnLoad`. The lifecycle uses XNA's `Initialize` → `LoadContent` → `Update`/`Draw`.

---

## 1. Entry Point Detection

### Application Start

| Phase | Location | Description |
|-------|----------|-------------|
| **Entry** | `Ship_Game\GameScreens\Program.cs` → `Main(string[] args)` | Process entry point |
| **Pre-Init** | `GlobalStats.LoadConfig()` | Must run before any `Log` calls; loads config from file |
| **Log** | `Log.Initialize()` | Centralized logging with Sentry |
| **CLI** | `ParseMainArgs(args)` | Handles `--mod`, `--export-textures`, etc. |
| **Game Instance** | `using StarDriveGame game = new()` | Single game instance |
| **Run** | `game.OnInitialize = RunInitializationTasks` then `game.Run()` | Optional CLI tasks, then XNA game loop |

### Boot Order (Chronological)

1. **Program.Main** — Unhandled exception handlers, culture, domain setup
2. **GlobalStats.LoadConfig** — Config, paths, mod info
3. **Log.Initialize** — Logging pipeline
4. **ParseMainArgs** — CLI arguments
5. **StarDriveGame constructor** — GC mode, directories, `Exiting` handler
6. **OnInitialize** — Optional CLI tasks (localizer, export, etc.); can exit early
7. **game.Run()** — XNA `Game.Run()`
8. **StarDriveGame.Initialize** — `Instance = this`, `ScreenManager = new()`, `ResourceManager.InitContentDir`, `ApplyGraphics`, `InitializeAudio`
9. **StarDriveGame.LoadContent** — `GameCursors.Initialize`, `ScreenManager.LoadContent`
10. **Default Screen** — If `NumScreens == 0`, `AddScreenAndLoadContent(new GameLoadingScreen(showSplash: true))`
11. **GameLoadingScreen** — Loads resources via `ResourceManager`, then transitions to **MainMenuScreen**
12. **MainMenuScreen** — New Game / Load Game / Options
13. **UniverseScreen** — Created from New Game or Load; starts **SimThread** in `LoadContent` → `InitializeUniverse` → `CreateUniverseSimThread`

### Scene / Screen Flow

- **No traditional Unity scenes.** Screens are stacked in `ScreenManager.GameScreens`.
- **Add:** `AddScreen()` (thread-safe, deferred) or `AddScreenAndLoadContent()` (immediate).
- **Replace:** `GoToScreen(screen, clear3DObjects)` clears stack and pushes new screen.
- **Exit:** Top screen calls `ExitScreen()` and is removed; game exits when stack is empty.

---

## 2. Singleton Analysis

### Singleton / Static Access Pattern

| Class | Pattern | Coupling | Role | God Class? |
|-------|---------|----------|------|------------|
| **StarDriveGame.Instance** | `public static StarDriveGame Instance` | **High** | Game root; referenced for exit, graphics, Steam | **Yes** — Holds state, exit logic, and graphics wiring |
| **ScreenManager.Instance** | `public static ScreenManager Instance { get; private set; }` | **High** | Central screen stack, graphics, input, lighting | **Yes** — State + view refs + logic |
| **ResourceManager** | Static class, many static fields | **Very High** | All game data: textures, modules, hulls, tech, etc. | **Yes** — ~2000+ lines, all content |
| **GlobalStats** | Static class | **High** | Config, mod info, graphics, gameplay flags | **Low** — Mostly config; stateless |
| **GameBase.Base** | `public static GameBase Base` | **High** | Content manager access | Part of God pattern |
| **GameBase.ScreenManager** | `public static ScreenManager ScreenManager` | **High** | Quick access from anywhere | Part of God pattern |
| **GameAudio** | Static class | **Medium** | Audio playback, SFX, music | **Low** — Stateless service |
| **Fonts** | Static class | **Low** | Cached font instances | **Low** — Stateless cache |
| **Log** | Static class | **Low** | Logging | **Low** — Stateless |
| **SteamManager** | Static class | **Low** | Steam achievements, stats | **Low** — Stateless |
| **Parallel** | Static class | **Low** | Thread pool | **Low** — Stateless |
| **InternalDamageModifier.Instance** | `public static InternalDamageModifier Instance` | **Low** | Single modifier instance | **Low** — Data only |

### Singleton Summary

| Type | Count | Notable Classes |
|------|-------|-----------------|
| **God Classes** | 3 | `StarDriveGame`, `ScreenManager`, `ResourceManager` |
| **Managers (mostly stateless)** | 4 | `GameAudio`, `Log`, `SteamManager`, `Parallel` |
| **Config / Caches** | 3 | `GlobalStats`, `Fonts`, `InternalDamageModifier` |

---

## 3. The Game Loop

### Architecture

- **Main thread:** XNA `Update(float deltaTime)` and `Draw()`.
- **Sim thread:** `UniverseScreen.SimThread` runs `UniverseSimulationLoop()`.
- **Sync:** Sim waits on `DrawCompletedEvt` at start of loop; `Draw()` calls `DrawCompletedEvt.Set()` at end.

### Loop Flow

```
Main Thread                          Sim Thread
─────────────                        ──────────
Update(deltaTime)
  └─ ScreenManager.Update(Elapsed)
       └─ Top screen Update()
            └─ UniverseScreen: HandleInput, etc.

Draw()
  └─ ScreenManager.Draw()
       └─ Top screen Draw()
            └─ UniverseScreen.Draw()
                 ├─ AdvanceSimulationTargetTime(elapsed.RealTime.Seconds)
                 └─ DrawCompletedEvt.Set()  ──►  SimThread wakes
                                                   ProcessSimulationTurns()
                                                     └─ SingleSimulationStep(FixedSimTime)
                                                          ├─ ProcessTurnEmpires()
                                                          ├─ UState.Objects.Update(timeStep)
                                                          ├─ UpdateMiscComponents()
                                                          └─ EndOfTurnUpdate()
```

### Time Model

| Concept | Implementation |
|---------|----------------|
| **Fixed timestep** | `FixedSimTime` — e.g. `1f / CurrentSimFPS` (default 60) |
| **Sim FPS** | `GlobalStats.SimulationFramesPerSecond` + `SimFPSModifier` |
| **Game speed** | `UState.GameSpeed` (0.25–10x); affects `fixedTimeStep` and max iterations |
| **Sim iterations per “frame”** | Up to `30 * GameSpeed` per Draw cycle |

### Game Loop Classification

| Criterion | Result |
|-----------|--------|
| **Simulation tick** | Distinct logical tick via `FixedSimTime` |
| **Frame coupling** | Decoupled — sim runs between Draw calls, not per frame |
| **Determinism** | Fixed timestep supports deterministic replays |
| **DOTS suitability** | **Reusable** — logic can be extracted into systems with `FixedSimTime` |

### Refactor Assessment

- **Reusable:** Fixed-timestep sim, `SingleSimulationStep(FixedSimTime)`, `UniverseObjectManager.Update(FixedSimTime)`.
- **Refactor needed:**  
  - Mixed UI + sim responsibilities in `UniverseScreen` (God class).  
  - `RunOnSimThread(Action)` / `PendingSimThreadActions` — cross-thread action queue; better as explicit commands.  
  - `AdvanceSimulationTargetTime(deltaTimeFromUI)` ties sim advancement to render timing; could be driven by logical time.

---

## 4. Persistent State

### Engine Note

This project uses MonoGame/XNA. **There is no `DontDestroyOnLoad`** — that is a Unity API.

### What Survives “Scene” Transitions

| System | Persistence | Mechanism |
|--------|-------------|-----------|
| **StarDriveGame** | Process lifetime | Root `Game` instance |
| **ScreenManager** | Process lifetime | Created in `StarDriveGame.Initialize` |
| **ResourceManager** | Process lifetime | Static data, loaded once |
| **GlobalStats** | Process lifetime | Static config |
| **GameAudio** | Process lifetime | Static, initialized once |
| **UniverseState** | While UniverseScreen is active | Owned by `UniverseScreen.UState` |
| **Screens** | Until popped | Stack in `ScreenManager.GameScreens` |

### Transitions

- **MainMenu → UniverseScreen:** `GoToScreen(universeScreen)` clears stack and pushes `UniverseScreen`.
- **UniverseScreen → ColonyScreen, CombatScreen, etc.:** Pushed on top; `UniverseScreen` stays in stack.
- **Back:** `ExitScreen()` pops current; previous screen resumes.
- **Save/Load:** New `UniverseScreen` created with `UniverseState` from save; old one disposed.

---

## 5. Dependency Graph (Simplified)

```
Program.Main
    └─ StarDriveGame
           ├─ GameBase (base)
           │      ├─ Game (XNA)
           │      ├─ Content (GameContentManager)
           │      └─ Graphics (GraphicsDeviceManager)
           ├─ ScreenManager (singleton)
           │      ├─ GameScreens[] (stack)
           │      ├─ input (InputState)
           │      ├─ LightSysManager (SunBurn)
           │      └─ SceneInter (SceneInterface)
           ├─ ResourceManager (static, init'd via InitContentDir)
           ├─ GlobalStats (static)
           └─ GameAudio (static)

UniverseScreen (when active)
    ├─ UniverseState (UState)
    │      ├─ Empires, Systems, Ships, Planets, etc.
    │      └─ Objects (UniverseObjectManager)
    ├─ SimThread → UniverseSimulationLoop
    └─ DrawCompletedEvt (sync with SimThread)
```

---

## 6. DOTS Extraction Recommendations

### High Value, Low Coupling

1. **FixedSimTime** — Already an explicit time type; suitable for ECS systems.
2. **UniverseObjectManager.Update(FixedSimTime)** — Ship/projectile/sensor updates; can be split into systems.
3. **SingleSimulationStep** — Clear boundary for a “tick” function.
4. **Empire.Update** — High-level empire logic; can become systems.
5. **SpatialManager / NativeSpatial** — Spatial queries; align with DOTS spatial structures.

### High Value, Higher Coupling

1. **ResourceManager** — Split into MeshCache, TextureCache, DataCache; expose data, not loading logic.
2. **UniverseState** — Core state container; map to DOTS World/entities/components.
3. **ScreenManager** — Separate presentation from game logic; UI as separate layer.

### God Classes to Decompose

1. **UniverseScreen** — Split into:
   - Simulation controller (tick driver)
   - Input handler
   - Render pipeline
   - UI overlays
2. **ResourceManager** — Split by responsibility (see above).
3. **StarDriveGame** — Reduce to bootstrapper + wiring; move logic into services.

---

*Report generated from static analysis. No runtime execution.*
