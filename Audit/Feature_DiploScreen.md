# Audit: Diplomacy Screen — Background Video / Rendering

**Note:** This codebase uses **XNA/MonoGame**, not Unity. There are no Unity Prefabs, Unity VideoPlayer, RawImage, or RenderTexture. The audit below describes the actual implementation.

---

## 1. Video Rendering

### 1.1 Component Used for Playback

| Unity term (for reference) | Actual implementation |
|----------------------------|------------------------|
| VideoPlayer | **Microsoft.Xna.Framework.Media.VideoPlayer** |
| RawImage / RenderTexture | **Texture2D** — the current video frame is obtained via `VideoPlayer.GetTexture()` and drawn with `SpriteBatch.Draw()`. No separate render texture; the frame is updated each draw. |

**Playback wrapper:** `Ship_Game/GameScreens/ScreenMediaPlayer.cs`

- Holds **`Video Video`** and **`VideoPlayer Player`** (XNA).
- **`Texture2D Frame`** — last valid frame from `Player.GetTexture()` for drawing when video is playing/paused.
- **`Rect`** — display rectangle (set by the Diplomacy screen to `Portrait`).
- Draw: `ScreenMediaPlayer.Draw(SpriteBatch batch, ...)` draws `Frame` into `Rect` (or a given rectangle). If no frame yet, nothing is drawn.

So the “exact component” for playback is **XNA’s `VideoPlayer`** plus a **Texture2D** used as the current frame. There is no Unity `VideoPlayer` or `RawImage`.

### 1.2 How the Video File Is Loaded

- **Not Addressables, not Resources folder, not StreamingAssets.**
- **XNA Content pipeline:** videos are loaded via **`GameContentManager.Load<Video>(path)`** (content root relative).
- **Entry point:** `ResourceManager.LoadVideo(GameContentManager content, string videoPath)`:

```csharp
// Ship_Game/Data/ResourceManager.cs (excerpt)
public static Video LoadVideo(GameContentManager content, string videoPath)
{
    string path = "Video/" + videoPath;   // e.g. "Video/Human"
    // Mod override: Content/../{ModPath}Video/{videoPath}.xnb
    ...
    var video = content.Load<Video>(path);
    if (video != null)
        return video;
    Log.Error($"LoadVideo failed: {path}");
    return content.Load<Video>("Video/Loading 2");  // fallback
}
```

- **Path rule:** `"Video/" + videoPath` → typically **`Video/{RaceName}`** (e.g. `Video/Human`), built as `.xnb` from the content pipeline (often from `.wmv` source). Mods can override with an extra path prefix.

---

## 2. Character / Faction Association

### 2.1 How the Game Knows Which Video to Play for “Faction A”

There is **no** `LeaderProfile` or `FactionDef` type. The link is:

**Empire → EmpireData → Traits (RacialTrait) → VideoPath**

- **Empire** has **`data`** (`EmpireData`).
- **EmpireData** has **`Traits`** (race data; deserialized from `Content/Races/*.xml`).
- **RacialTrait** (in `Ship_Game/Data/RacialTrait.cs`) has **`VideoPath`** (string).

**Example — Human race** (`game/Content/Races/Human.xml`):

```xml
<Traits>
  <Name>The United Federation</Name>
  <Singular>Human</Singular>
  <Plural>Humans</Plural>
  <VideoPath>Human</VideoPath>
  <ShipType>Terran</ShipType>
  ...
</Traits>
```

So for “Faction A” the game uses **that empire’s race** (e.g. Human, Vulfen, Ralyeh). The **race’s `VideoPath`** in `Races/{Race}.xml` is the asset name under `Video/` (e.g. `Video/Human`).

### 2.2 File Path Structure for Leader / Race Assets

| Asset type | Path pattern | Source of name |
|------------|--------------|----------------|
| **Diplomacy video** | `Video/{VideoPath}` (e.g. `Video/Human`) | `EmpireData.Traits.VideoPath` (from `Races/*.xml`) |
| **Static portrait (fallback)** | `Portraits/{PortraitName}` (e.g. `Portraits/Human`) | `EmpireData.PortraitName` (from same race XML) |

So leader/faction “media” is keyed by **race**: one video per race (`VideoPath`), one portrait per race (`PortraitName`). No per-leader or per-faction ID beyond the race definition.

---

## 3. Animation / Blending — Pure Video, No 3D Models

- **No 3D models** for the diplomacy background. No Animator, no RenderTextures for 3D scenes.
- **Pure video:** the background is a single **looping video** per race. Each frame:
  - `VideoPlayer.GetTexture()` gives the current frame as **Texture2D**.
  - That texture is stored in `ScreenMediaPlayer.Frame` and drawn into the `Portrait` rectangle.

**Loop vs intro:**

- **Loop:** Default is **looping**. `ScreenMediaPlayer` is constructed with `looping: true`; `PlayVideo(empire.data.Traits.VideoPath)` does not pass `looping: false`, so diplomacy uses the default **looping** behavior.
- **Intro:** There is **no** separate intro vs loop in the diplomacy screen. One video plays and loops for the whole time the screen is open.

**War tint:** When at war, the drawn frame is tinted (blue/green reduced) so the same video looks “angry”:

```csharp
if (WarDeclared || UsAndThem.AtWar)
{
    color.B = 100;
    color.G = 100;
}
RacialVideo.Draw(batch, color);
```

---

## 4. How the UI “Switches” the Active Leader Video

The diplomacy screen is **one screen per conversation** (player vs one empire). The “other” empire is **`Them`**. The video is chosen once when the screen is shown and is always **that empire’s** race video. There is no in-screen switching between multiple leaders; you open a new screen per faction.

**Snippet — where the active leader video is set (DiplomacyScreen):**

```csharp
// DiplomacyScreen.cs

ScreenMediaPlayer RacialVideo;   // field
readonly Empire Them;            // the empire we're talking to
readonly Empire Us;              // player

// Update() — ensure player exists and start playback for Them
public override void Update(float fixedDeltaTime)
{
    RacialVideo ??= new(TransientContent);

    if (!RacialVideo.PlaybackFailed)
        RacialVideo.PlayVideoAndMusic(Them, WarDeclared);   // Them = faction for this screen

    RacialVideo.Rect = Portrait;
    RacialVideo.Update(this);
    ...
}
```

**Snippet — PlayVideoAndMusic (ScreenMediaPlayer) — uses faction’s race video path:**

```csharp
// ScreenMediaPlayer.cs
public void PlayVideoAndMusic(Empire empire, bool warMusic)
{
    if (IsPlaying || IsDisposed)
        return;

    PlayVideo(empire.data.Traits.VideoPath);   // ← Faction → Race → VideoPath

    if (empire.data.MusicCue != null && Player.State != MediaState.Playing)
    {
        ExtraMusic = GameAudio.PlayMusic(warMusic ? "CombatMusic" : empire.data.MusicCue);
        GameAudio.SwitchToRacialMusic();
    }
}
```

**Snippet — fallback when video is unavailable (DiplomacyScreen.DrawBackground):**

```csharp
void DrawBackground(SpriteBatch batch)
{
    if (RacialVideo.Size != Vector2.Zero)
    {
        if (RacialVideo.ReadyToPlay)
        {
            Color color = Color.White;
            if (WarDeclared || UsAndThem.AtWar) { color.B = 100; color.G = 100; }
            RacialVideo.Draw(batch, color);
        }
    }
    else
    {
        batch.Draw(Them.data.PortraitTex, Portrait, Color.White);   // static portrait fallback
    }
    ...
}
```

So: **“Switching”** = opening a **new** DiplomacyScreen with a different **`Them`**. That screen’s `RacialVideo` then plays **`Them.data.Traits.VideoPath`** (one video per race, looping, with optional war tint and music).

---

## 5. Summary Table

| Question | Answer |
|----------|--------|
| **Exact component for playback** | **XNA `VideoPlayer`** + **`Texture2D`** frame from `GetTexture()`. Wrapper: **`ScreenMediaPlayer`**. No Unity VideoPlayer/RawImage. |
| **How video is loaded** | **Content pipeline:** `content.Load<Video>("Video/" + videoPath)`. Not Addressables/Resources/StreamingAssets. |
| **File path structure** | **Video:** `Video/{VideoPath}` (e.g. `Video/Human`). **Portrait (fallback):** `Portraits/{PortraitName}`. Both names come from **race XML** (`Races/*.xml` → `Traits.VideoPath`, `PortraitName`). |
| **Faction → video link** | **Empire → EmpireData.Traits (RacialTrait).VideoPath**. No LeaderProfile/FactionDef; it’s race-based. |
| **Pure video vs 3D** | **Pure video.** No 3D models, no Animator, no RenderTextures for 3D. |
| **Loop vs intro** | **Loop only** for diplomacy; no separate intro clip. Optional **war tint** (color modifier) on the same video. |
