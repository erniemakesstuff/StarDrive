# Gap Analysis: Non-Code Assets & Engine-Specific Implementations

**Migration Target:** Unity 2023+ (DOTS/URP)  
**Source:** StarDrive XNA/MonoGame  
**Scope:** Visual/audio assets, shaders, fonts, 3D models, and hardware interfaces.

---

## Executive Summary

| Asset Category | Status | Migration Complexity |
|----------------|--------|----------------------|
| **Textures** | Mixed (source + XNB) | Low–Medium |
| **3D Models (Asteroids/Planets)** | Source available (.fbx, .obj) | Low |
| **3D Models (Ships)** | **Decompilation required** (.xnb only) | High |
| **Shaders** | Mixed (some .fx source, many XNB) | Medium–High |
| **Audio** | Source available (YAML + .m4a/.mp3/.wav) | Low |
| **Fonts** | **Decompilation or recreation required** (.xnb SpriteFont) | Medium |

**Key finding:** The game does **not** use XACT. Audio is driven by `AudioConfig.yaml` with raw `.m4a`/`.mp3`/`.wav` files and NAudio playback—simplifying migration.

---

## 1. Raw vs. Compiled Asset Audit

### 1.1 Content Directory Scan Results

| Path | Source Formats Found | Compiled (.xnb) |
|------|----------------------|-----------------|
| `Content/Model/Asteroids/` | 9× `.fbx` (asteroid1–9.fbx) | No |
| `Content/Model/SpaceObjects/` | 4× `.obj` + `.mtl` (planet_sphere, planet_rings, planet_glow_ring, planet_glow_fresnel) | No |
| `Content/Model/Ships/` | **None** (path referenced but no source files in repo) | Yes (ship models loaded as XNA `Model`) |
| `Content/Effects/` | `Simple.fx`, `Simple.fxh`, `Clouds.fx` | BloomExtract, BloomCombine, GaussianBlur, desaturate, BeamFX, etc. |
| `Content/3DParticles/` | `ParticleEffect.fx` | — |
| `Content/Fonts/` | **None** (no `.spritefont` or raw font sources) | Yes (Arial*, Pirulen*, Verdana*, etc.) |
| `Content/Textures/` | `.dds`, `.png` (referenced in YAML/XML) | Many textures built as `.xnb` |
| `Content/Audio/` | `AudioConfig.yaml` + `.m4a`/`.mp3`/`.wav` paths | No XACT |

### 1.2 Asset Loading Logic (GameContentManager + RawContentLoader)

- **Raw path:** `RawContentLoader.IsSupportedMesh()` → only `.fbx` and `.obj`; `TextureImporter` → `.png` and `.dds` via `ImageUtils.LoadPng` / `Texture.FromFile`.
- **XNB path:** `AssetName` defaults to `.xnb` when no extension; `Content.Load<T>()` used for `Model`, `SkinnedModel`, `Effect`, `SpriteFont`, `Texture2D`.

**Conclusion:**  
- **Source available:** Asteroids (.fbx), space objects (.obj), some effects (.fx), audio files (.m4a/.mp3/.wav), some textures (.dds/.png).  
- **Decompilation required:** Ship models, fonts, most effects (Bloom*, BeamFX, desaturate, etc.), and textures present only as `.xnb` in the built game.

### 1.3 XNB Decompiler / Extractor Options

| Tool | Purpose | Notes |
|------|---------|-------|
| [Xnb.Exporter](https://github.com/modz2014/xnb.exporter) | Extract textures/models from XNB | Open-source, asset viewing/export |
| [XNB_Kit](https://github.com/HyperCrusher/XNB_Kit) | Unpack/repack XNB | C#, Stardew Valley–oriented |
| [XnbCli](https://github.com/nthung-2k5/XnbCli) | XNB handling, MonoGame shims | C#, texture extraction |
| [xnb_decomp (fesh0r)](https://github.com/fesh0r/xnb_decomp) | Decompressor | C#, MIT |
| MonoGame MGCB | Build content | `dotnet tool install -g dotnet-mgcb`; compile, not decompile |

**Recommendation:** Use **Xnb.Exporter** or **XNB_Kit** to batch-extract textures and models from the shipped `Content/*.xnb` files. For fonts, extraction yields bitmap textures; for Unity, plan migration to TextMeshPro SDF.

---

## 2. 3D Model & Animation Pipeline

### 2.1 Model Loaders

| Loader | Source | Path | Format |
|--------|--------|------|--------|
| `MeshImporter.ImportStaticMesh()` | SDNative/Nano | `Content/Model/…/*.fbx`, `*.obj` | FBX, OBJ |
| `GameContentManager.LoadStaticMesh()` | XNA ContentManager | `Model/Ships/…/ship08` (no ext → .xnb) | XNA Model (XNB) |
| `LoadSkinnedModel()` | XNA ContentManager | Animated hulls (e.g. Ralyeh) | XNA SkinnedModel (XNB) |

`StaticMesh.cs` supports:
- XNA `Model` / `ModelMesh`
- `SkinnedModel` with `Skeleton`, `AnimationClips` (XNAnimation)
- Raw `MeshData` + `Effect` from SDMesh

### 2.2 Skeleton & Skinned Animation

- **Skinned animation:** Used for `Animated=true` hulls (e.g. Ralyeh); `XNAnimation` drives `SkinnedBoneTransforms`.
- **Source format:** Ship/skinned models are loaded as XNA `Model`/`SkinnedModel` from `.xnb`; no `.fbx` ship sources in the audited Content tree.
- **Unity requirement:** `.fbx` with skin weights; no native XNB import.

### 2.3 Model Pipeline Migration

| Asset Type | Current Format | Target | Action |
|------------|----------------|--------|--------|
| Asteroids | `.fbx` | Unity FBX | Direct import; may need material reassignment |
| Planet meshes | `.obj` + `.mtl` | Unity | Import OBJ or convert to FBX (Blender/Assimp) |
| Ships | XNB (XNA Model) | FBX | Extract via XNB tool → convert to FBX (manual or script) |
| Skinned ships | XNB (SkinnedModel) | FBX with bones | Same; verify skin weights and bone hierarchy |

**If only XNB ship models exist:**
1. Use XNB extractor to get geometry + materials.
2. Export to an intermediate format (OBJ/COLLADA) if the tool supports it.
3. Import into Blender and re-export as FBX with correct skeleton (for skinned models).
4. Optional: C# converter script that parses XNB Model format and writes FBX via Assimp or similar.

---

## 3. Shader Complexity Analysis

### 3.1 HLSL (.fx / .fxh) Inventory

| File | Location | Complexity | Notes |
|------|----------|------------|-------|
| `Simple.fx` / `Simple.fxh` | Content/Effects | **Simple** | Texture + tint, `ViewProjection`; easy Shader Graph |
| `Clouds.fx` | Content/Effects | **Low** | Dual-noise sampling, position-based UV; Shader Graph |
| `ParticleEffect.fx` | Content/3DParticles | **Medium** | Velocity, size/color fade, rotation; standard particle HLSL |
| `BloomExtract` | Loaded as Effect | **Medium** | Post-process; recreate as URP Volume/Pass |
| `BloomCombine` | Loaded as Effect | **Medium** | Post-process |
| `GaussianBlur` | Loaded as Effect | **Medium** | Blur kernel; URP Fullscreen Pass |
| `desaturate` | Loaded as Effect | **Simple** | Fullscreen desaturation |
| `BeamFX` | Loaded as Effect | **Unknown** | Beam weapon; likely additive/blend; source not in repo |
| `PlanetHalo`, `PlanetShader`, `SunLayerCombine`, etc. | XNB | **Medium–High** | Custom effects; need decompilation or reimplementation |

### 3.2 Effect Usage in Code

- `BloomComponent`: BloomExtract, BloomCombine, GaussianBlur (post-processing)
- `Beam.BeamEffect`: beam weapons
- `YouWinScreen` / `YouLoseScreen`: desaturate for vignette
- `MeshImporter`: SynapseGaming `LightingEffect` for 3D; separate from .fx above

### 3.3 Shader Migration Plan

| Shader | Approach |
|--------|----------|
| Simple, Clouds | Shader Graph (Unlit + texture + color) |
| ParticleEffect | Shader Graph (Particle Unlit) or URP Built-in Particle |
| BloomExtract/Combine, GaussianBlur, desaturate | URP Renderer Feature or Volume Override; custom Fullscreen Pass |
| BeamFX | Inspect via XNB decompilation; likely simple additive beam → Shader Graph |
| Planet/Sun effects | Decompile or rewrite in HLSL for URP Shader Graph / custom shader |

**Gap:** Planet, sun, and other custom effects lack `.fx` source in the repo. Decompile from XNB or reimplement from behavior.

---

## 4. Audio Middleware Strategy

### 4.1 Architecture (No XACT)

- **Config:** `Content/Audio/AudioConfig.yaml` — categories (Music, RacialMusic, PlanetAmbient, Warp, Weapons, etc.) with `SoundEffect` entries.
- **Format:** `Sound: "Music/AmbientMusic.0.m4a"` or `Sounds: ["…", "…"]`; supported: `.m4a`, `.mp3`, `.aac`, `.wav`, `.aif`.
- **Playback:** NAudio (`NAudioPlaybackEngine`, `AudioConfig`, `AudioCategory`) — no XACT, WaveBank, or Cue.

### 4.2 Weapon / Cue Mapping

Weapon XML uses `FireCueName`, `DieCue`, `InFlightCue` (e.g. `sd_weapon_laser_large_alt_04`, `Explo1`). These map to `SoundEffect.Id` in `AudioConfig.yaml`, not XACT cues.

### 4.3 Playback Model

- **Fire-and-forget:** `GameAudio.PlaySfx(id)` / `PlaySfxAsync(id)` — simple `PlayOneShot`-style.
- **Music:** Categories with `FadeOutTime`, `MaxConcurrentSounds`; crossfade between tracks (e.g. AmbientMusic, CombatMusic).
- **3D positional:** `SetListenerPos`, `AudioEmitter` for spatial SFX.

**Conclusion:** No FMOD/Wwise required for parity. Unity `AudioSource` + `AudioClip` + a small category/mixer layer is sufficient. Optionally use Unity Audio Mixer for Music vs Effects.

---

## 5. Text & Font Rendering

### 5.1 Current Pipeline

- **Loader:** `Font.cs` → `content.Load<SpriteFont>("Fonts/" + fontPath)` (e.g. `Fonts/Arial12Bold`, `Fonts/Pirulen12a`).
- **Usage:** `SpriteBatch.DrawString(Fonts.Arial12Bold, text, pos, color)` — no runtime font generation.
- **Fonts:** Arial10/11Bold/12/12Bold/14Bold/20Bold/8Bold, Consolas18, Laserian14, Pirulen12/16/20, Tahoma10/11/Bold9, Verdana10/12/12Bold/14Bold, Visitor10.

### 5.2 Source Assets

- **No `.spritefont` files** found in `Content/Fonts/`.
- Fonts are loaded as compiled XNB; source `.spritefont` XML (if any) is not in the audited tree.

### 5.3 Migration to TextMeshPro

| Step | Action |
|------|--------|
| 1 | Extract font texture + glyph data from XNB (if possible) or identify source TTF (Arial, Verdana, Pirulen, etc.). |
| 2 | Use TextMeshPro SDF: generate `.asset` from TTF with same sizes (10, 12, 14, 20, etc.). |
| 3 | Replace `SpriteBatch.DrawString` with `TextMeshProUGUI` or `TextMeshPro` and match `LineSpacing` / `SpaceWidth` via `FontAsset` settings. |
| 4 | For custom (Pirulen, Laserian, Visitor): obtain TTF or recreate SDF from extracted bitmap if no TTF. |

**Risk:** Pirulen, Laserian, Visitor may be commercial/niche fonts; verify licensing and availability.

---

## 6. Tooling Requirements

| Tool | Purpose |
|------|---------|
| **Xnb.Exporter** or **XNB_Kit** | Extract textures, models, effects from `.xnb` |
| **MonoGame MGCB** | Rebuild content if `.spritefont` / `.fx` sources exist; not for decompilation |
| **Blender** (or similar) | Convert OBJ → FBX; fix ship meshes from XNB export |
| **Assimp** (optional) | Programmatic mesh format conversion |
| **FFmpeg** (optional) | Transcode `.m4a` → `.wav` if Unity prefers uncompressed |
| **TextMeshPro** | Font migration (SDF) |
| **Unity URP Shader Graph** | Simple/Clouds/particle shaders |
| **Unity URP Custom Renderer Feature** | Bloom, blur, desaturate |

---

## 7. Asset Status Summary

| Category | Status | Notes |
|----------|--------|-------|
| **Textures** | Mixed | `.dds`/`.png` in YAML; many also as XNB; extract where needed |
| **3D – Asteroids** | Source available | 9× `.fbx` |
| **3D – Space objects** | Source available | 4× `.obj` + `.mtl` |
| **3D – Ships** | Decompilation required | XNB only; no `.fbx`/`.obj` in repo |
| **Effects (Simple, Clouds, Particle)** | Source available | `.fx` in Content |
| **Effects (Bloom, Beam, etc.)** | Decompilation required | XNB only |
| **Audio** | Source available | YAML + `.m4a`/`.mp3`/`.wav` |
| **Fonts** | Decompilation or recreation | XNB SpriteFont; no `.spritefont` source |

---

## 8. Recommended Migration Order

1. **Audio** — Port YAML config and wire `AudioClip` by Id; simplest.
2. **Textures** — Prefer existing `.dds`/`.png`; extract from XNB where missing.
3. **Asteroids & space objects** — Import `.fbx`/`.obj`; assign materials.
4. **Simple shaders** — Recreate Simple, Clouds, ParticleEffect in Shader Graph.
5. **Post-processing** — Implement Bloom, blur, desaturate as URP passes.
6. **Fonts** — Generate TextMeshPro SDF from TTFs; fallback to bitmap extraction.
7. **Ship models** — Run XNB extraction; establish OBJ/FBX pipeline for ships.
8. **Complex effects** — Decompile or reimplement Planet/Sun/Beam from behavior.

---

*Report generated from static analysis of the StarDrive (XNA/MonoGame) codebase. Paths and formats verified against `game/Content`, `Ship_Game`, `SDNative`, and `Deploy/Release`.*
