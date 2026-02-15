This roadmap is designed for an experienced software engineer. It prioritizes **Technical Risk Reduction** (proving DOTS/LLM works) followed by **Gameplay Loop Implementation** (the 4X elements).

We will follow a **"Vertical Slice"** strategy: implementing *one* full chain of interaction (UI -> Logic -> Rendering -> Save) for a single mechanic before widening the scope.

---

### **Phase 1: The "Iron Skeleton" (Tech Stack & Render Pipeline)**
**Goal:** Prove you can render 10,000 ships at 60 FPS and import legacy data.
**Timeline:** Weeks 1–4

**1.1. Project Setup & Packages**
*   **Unity Version:** 2022 LTS or 2023 (Stable).
*   **Packages:** Entities, Burst, Collections, Mathematics, Entities Graphics, UI Toolkit, Input System.
*   **Repo Structure:** `/Assets/Scripts/ECS`, `/Assets/Scripts/UI`, `/External/LegacyData`.

**1.2. The Data Importer (The Bridge)**
*   **Task:** Write a C# tool (Editor Script) to parse one legacy `.hull` file and one `.design` file.
*   **Output:** A Unity `ScriptableObject` (`ShipDesignSO`) containing the grid layout and stats.
*   **Baker:** Write an `Authoring` script that converts `ShipDesignSO` into an ECS `BlobAsset` (read-optimized memory).

**1.3. The "Moving Dots" Prototype**
*   **ECS:** Create a simple `MoveSystem` (using `SystemAPI.Time.DeltaTime` and `float3 Position`).
*   **Graphics:** Use `BatchRendererGroup` or `Entities Graphics` to render a generic mesh for the ship.
*   **Stress Test:** Spawn 10,000 entities. Ensure 60 FPS.
    *   *Success Criteria:* You see 10k cubes moving in a flow field without dropping frames.

---

### **Phase 2: The "Interactive Map" (Grid & Navigation)**
**Goal:** A playable galaxy map where ships obey movement rules.
**Timeline:** Weeks 5–8

**2.1. Galaxy Generation**
*   **Logic:** Port the legacy Star Generation logic (pure C#).
*   **Visuals:** Render Stars and Planets. Use GPU Instancing for stars.
*   **Coordinate System:** Implement the "Sector/Grid" coordinate system from the audit (pure math, no physics engines).

**2.2. The Camera & Input**
*   **Camera:** Implement an RTS Camera (Zoom/Pan) using standard MonoBehaviours (Camera logic doesn't need to be DOTS).
*   **Selection:** Implement a "Spatial Query" system.
    *   *Tech:* A `NativeQuadtree` job that takes mouse coordinates and returns Entity IDs.

**2.3. Pathfinding (The Flow Field)**
*   **Logic:** Implement the Grid traversal math.
*   **ECS:** A `PathfindingSystem` that reads `TargetPosition` and writes `Velocity`.
*   *Success Criteria:* You can select a group of 500 ships, click a planet, and they navigate around a "Nebula" (obstacle) to get there.

---

### **Phase 3: The "Empire Engine" (Economy & Data)**
**Goal:** The "Expand" and "Exploit" phases. Resources, construction, and turns.
**Timeline:** Weeks 9–12

**3.1. The Simulation Loop (Fixed Step)**
*   **Architecture:** Implement the `FixedStepSimulationSystemGroup`.
*   **Logic:** Port the `Empire.DoMoney()` and `Planet.UpdateIncomes()` logic from the XNA audit.
*   **Components:** `ResourceStockpile`, `PlanetEconomy`, `TaxRate`.

**3.2. UI Toolkit Integration**
*   **Architecture:** MVVM Pattern.
    *   *ECS:* `System` writes data to a `NativeHashMap` or `Singleton` component.
    *   *Bridge:* A MonoBehaviour queries ECS every frame and updates a standard C# View Model.
    *   *UI:* UI Toolkit XML binds to the View Model.
*   **Feature:** A "Colony Screen" that shows Population/Production growing in real-time.

**3.3. Construction Queue**
*   **Logic:** A `BuildSystem` that decrements production points and spawns a ship Entity when complete.
*   *Success Criteria:* You can click a planet, queue a "Scout," wait 5 seconds, and see the Scout appear.

---

### **Phase 4: The "Ghost in the Machine" (LLM & Diplomacy)**
**Goal:** The unique selling point. Deep politics and non-deterministic narratives.
**Timeline:** Weeks 13–16

**4.1. The Local LLM Bridge**
*   **Tech:** Integrate `Llama.cpp` (or similar) running in a background thread.
*   **Architecture:** Asynchronous Command Buffer.
    *   Game Main Thread -> `EnqueuePrompt(JSON Context)`
    *   Background Thread -> `Await Inference`
    *   Game Main Thread -> `ApplyResponse(JSON Result)`

**4.2. The History System**
*   **Data:** Create a `DynamicBuffer<HistoryEvent>` on Empire entities.
*   **Events:** Log "War Declared," "Trade Broken," "Planet Bombarded."
*   **Serialization:** Ensure this buffer can be dumped to a string for the LLM prompt.

**4.3. The Diplomatic Screen**
*   **UI:** A chat interface.
*   **Feature:** Talk to an AI rival. Ask "Why did you attack me?"
    *   *Logic:* The AI reads its `HistoryBuffer`, generates a rationale via LLM, and responds.
*   *Success Criteria:* You declare war. The AI insults you based on a specific event that happened 10 turns ago.

---

### **Phase 5: The "War Room" (Combat & Damage)**
**Goal:** The "Exterminate" phase.
**Timeline:** Weeks 17–20

**5.1. Spatial Combat**
*   **Math:** Port the `RayCircleIntersect` and `NarrowPhase` logic from XNA to Burst-compiled Jobs.
*   **System:** A `WeaponFireSystem` that checks range and spawns `Projectile` entities.

**5.2. Damage & Modules**
*   **Components:** `Health`, `Shield`, `Armor`.
*   **Logic:** Grid-based damage (ships have internal module grids).
    *   *Tech:* Use `BlobAssets` for the ship's internal layout to avoid memory overhead.

**5.3. VFX Graph**
*   **Visuals:** Use Unity VFX Graph for beams, explosions, and shields.
*   **Bridge:** An ECS System that spawns "VFX Trigger Events" which a MonoBehaviour reads to play the visual effect.

---

### **Phase 6: The Wrapper (Save/Load & Polish)**
**Goal:** Persistence and User Experience.
**Timeline:** Weeks 21–24

**6.1. Save System (The Hardest Part)**
*   **Format:** Custom Binary or JSON writer (using `System.Text.Json` source generation for speed).
*   **ECS:** You must iterate all Entities and serialize their Components.
*   **LLM State:** Serialize the "Narrative History" buffers to a sidecar JSON file.

**6.2. Audio**
*   **Tech:** FMOD or Unity Audio.
*   **Logic:** Hook sound events (Explosion, UI Click) to the ECS Event system.

**6.3. Steam Integration**
*   **Tech:** Steamworks.NET.
*   **Features:** Achievements, Cloud Save.

---

### **The Vertical Slice Milestone (End of Month 6)**
At this point, you have:
1.  **Start:** Main Menu -> New Game.
2.  **Play:**
    *   Generate a small galaxy.
    *   Move scouts (DOTS).
    *   Colonize one planet (Economy).
    *   Meet one AI.
    *   **Insult the AI via Chat (LLM) and cause a war.**
    *   Fight a battle (Combat System).
3.  **End:** Save the game. Quit. Load the game.

### **Risk Management**

| Risk | Mitigation |
| :--- | :--- |
| **LLM Inference Latency** | Use the "Turn-Based Processing" architecture. The AI "thinks" while the player is busy managing planets. Never block the main thread. |
| **DOTS Learning Curve** | Stick to `IJobEntity` (simplest API) for 90% of logic. Only use manual Chunks (`IJobChunk`) if absolutely necessary for performance. |
| **Scope Creep** | Do not implement "Ground Combat" or "Espionage" for the Vertical Slice. Stick to Space + Diplomacy. |
| **Asset Pipeline** | If extracting XNA assets fails, use colored cubes. Do not let art block code. |

### **Next Action**
Execute **Phase 1.2 (The Data Importer)**. Until you can read the legacy `ShipDesign` and `Hull` files into Unity, nothing else matters. This is your "Hello World."