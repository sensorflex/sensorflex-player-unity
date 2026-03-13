# SensorFlex Player Occlusion System Task

## Purpose

This document is a handoff for continuing the SensorFlex player occlusion work.

The intended product outcome is:

1. a Unity developer swaps XR provider from ARKit/ARCore to SensorFlex
2. keeps using standard AR Foundation components
3. runs their application directly
4. gets visible environment-depth occlusion of virtual 3D content

The key point is that this is **provider behavior**, not a scene-specific
visual effect. The system should behave as if SensorFlex were a real AR
Foundation provider in the same way ARKit feels to a host application.

---

## Repositories And Ownership

There are two relevant git repos in the current workspace:

- testbed repo:
  `/Users/yzigm/Desktop/sensorflex-unity-testbed`
- player package repo:
  `/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity`

The player package is a nested git repo / submodule-style dependency. Be aware
that occlusion work may require commits in both places:

- package repo for subsystem / runtime / docs changes
- testbed repo for scene wiring and submodule pointer updates

Recent occlusion-related scene wiring exists in:

- [SceneLoading.unity](/Users/yzigm/Desktop/sensorflex-unity-testbed/Assets/Scenes/SceneLoading.unity)

Recent provider/runtime changes exist in:

- [Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)
- [Camera.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Camera.cs)
- [ARSensorFlexSession.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/ARSensorFlexSession.cs)

---

## Target Product Standard

The occlusion system should be considered done when a normal AR Foundation app
that uses:

- `ARSession`
- `ARCameraManager`
- `ARCameraBackground`
- `AROcclusionManager`
- ordinary virtual scene geometry / app materials

can switch to the SensorFlex XR loader and still:

- show replayed camera imagery
- receive environment depth
- render virtual content in the expected pose space
- occlude virtual objects against replay depth
- preserve world alignment and world scale behavior

The success bar is:

- "swap provider and run"

and not:

- "a custom SensorFlex demo object is manually made to disappear"

---

## Important Existing Docs

Start with these files before changing behavior:

- [Architecture.md](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Docs/Architecture.md)
- [ZipFormat.md](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Docs/ZipFormat.md)

The ZIP format doc is especially important because it defines the intended
depth source format and units:

- `depth.bin`
- `raw_float32_le`
- meters
- `256 x 192`

That is the canonical depth path for provider-quality occlusion.

---

## Current Runtime Structure

### Core Files

- [Loader.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Loader.cs)
  Creates the camera/session/occlusion subsystems.

- [Camera.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Camera.cs)
  Owns replay timing, publishes camera textures, projection, intrinsics, and
  replay pose.

- [Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)
  Implements the custom `XROcclusionSubsystem`.

- [ARSensorFlexSession.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/ARSensorFlexSession.cs)
  Holds source configuration, depth enablement, session alignment, replay
  camera driving, and the effective world/depth scale.

- [PoseBridge.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Bridges/PoseBridge.cs)
  Stores the latest replay pose in Unity world space.

- [FrameLoading.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/FrameLoading.cs)
  Loads frames from FileSystem / ZIP / WebSocket backends.

### Scene-Level Integration

The current test scene camera has:

- `ARCameraBackground`
- `ARCameraManager`
- `AROcclusionManager`
- `ARShaderOcclusion`

This was added in the testbed scene so the provider path can be exercised with
standard AR Foundation components.

---

## Current State Of The Occlusion Work

The following work has already been done.

### 1. Occlusion subsystem exists and is registered

[Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)
registers `SensorFlex-Occlusion` as a custom `XROcclusionSubsystem`.

### 2. Depth descriptors are now exposed through AR Foundation paths

The provider currently implements:

- `TryGetEnvironmentDepth(...)`
- `GetTextureDescriptors(...)`
- `TryGetFrame(...)`

This was necessary because `AROcclusionManager` does not only use the simple
single-texture getter; it also expects descriptor/frame paths.

### 3. Camera state is exposed to support occlusion-frame metadata

[Camera.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Camera.cs)
now publishes:

- latest timestamp
- latest intrinsics
- latest texture dimensions

The depth subsystem uses those values to build `XROcclusionFrame` metadata.

### 4. World scale is now applied to metric depth

[ARSensorFlexSession.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/ARSensorFlexSession.cs)
now exposes `EffectiveDepthWorldScale`.

That value currently combines:

- `SessionAlignment.uniformScale`
- replay `m_PositionScale`

[Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)
uses that scale for `.bin` metric depth and reloads depth if the scale changes
while the subsystem is running.

### 5. File-system depth currently supports both fallback and canonical formats

Current file-system support in [Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs):

- `.bin` raw float depth
- `.png` / `.jpg` / `.jpeg` normalized fallback depth

Important:

- `.bin` is the only path that currently scales correctly in metric world space
- normalized image depth is kept only as a fallback and is not a provider-grade
  representation

### 6. Scene camera now includes AR Foundation occlusion components

The testbed scene has been wired so the camera can exercise:

- `AROcclusionManager`
- `ARShaderOcclusion`

This is useful for validating provider behavior, but it is not yet proof that
host applications will get generic visible occlusion for arbitrary shaders.

---

## What Is Still Missing

The main remaining problem is **visible generic occlusion of virtual content**.

The provider side is partially in place, but the rendering story is not yet
finished enough to claim ARKit-like drop-in behavior.

Current likely gaps:

- generic render-path coverage for ordinary virtual objects
- confidence that `ARShaderOcclusion` alone is sufficient in this URP setup
- a fallback URP renderer-feature solution if per-shader support is not enough
- validation outside the custom test scene

In short:

- depth is being published
- AR Foundation occlusion components are present
- the remaining question is whether ordinary 3D app content actually disappears
  behind environment depth without app-specific shader rewrites

---

## Scope

This task is specifically about **environment depth occlusion** for AR
Foundation applications using the SensorFlex player package.

In scope:

- `XROcclusionSubsystem` behavior
- environment depth publication
- AR Foundation occlusion integration
- world alignment / world scale interaction with depth
- visible rendering of occlusion on ordinary virtual content

Out of scope unless needed as dependencies:

- recorder package
- human segmentation
- non-depth AR systems such as planes, anchors, raycasts

---

## Core Requirement

The occlusion path must behave like a provider, not like a package demo.

That means:

- AR Foundation managers should consume it through subsystem APIs
- host apps should not need SensorFlex-specific gameplay scripts
- support flags and mode reporting should match real behavior
- the path should be usable by an app team that only swaps XR provider / loader

---

## Known Constraints And Caveats

These are current truths of the repo and should be treated as intentional until
changed.

### 1. FileSystem mode is the only depth path currently implemented in `Depth.cs`

`Depth.cs` currently binds depth only for file-system playback.

Implication:

- ZIP depth exists elsewhere in the codebase, but is not yet routed through the
  occlusion subsystem
- WebSocket depth is not yet supported

### 2. ZIP already contains the right type of depth

[ZipFormat.md](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Docs/ZipFormat.md)
defines metric float `depth.bin`, and [FrameLoading.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/FrameLoading.cs)
already carries depth bytes in `DepthBins`.

Implication:

- the correct long-term provider path should probably converge on ZIP/metric
  depth rather than normalized images

### 3. The camera background shader is not the main generic occlusion solution

[SensorFlexCameraBackground.shader](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Assets/SensorFlexCameraBackground.shader)
draws the replayed RGB image.

It is not the main place to make arbitrary virtual objects disappear.

Generic visible occlusion likely belongs in:

- AR Foundation shader-global behavior if sufficient, or
- a URP renderer feature / full-screen pass if per-material edits are required

### 4. Reconstructed mesh-only solutions are not acceptable

It is not enough to make the packaged scanned mesh use a special shader.

The product goal is:

- host application content gets occluded

not:

- only SensorFlex-authored geometry gets occluded

---

## Workstreams

### 1. Subsystem Contract Completion

The custom occlusion subsystem must satisfy AR Foundation's runtime contract,
not just expose a single texture.

Required behavior:

- support `requestedEnvironmentDepthMode`
- report `currentEnvironmentDepthMode` truthfully
- publish depth through `TryGetEnvironmentDepth(...)`
- publish current frame descriptors through `GetTextureDescriptors(...)`
- publish occlusion frame metadata through `TryGetFrame(...)`
- keep timestamps aligned with camera playback
- keep pose / FOV / near-far metadata coherent with the replay camera

Relevant files:

- [Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)
- [Camera.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Camera.cs)
- [ARSensorFlexSession.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/ARSensorFlexSession.cs)

Definition of done:

- `AROcclusionManager` receives stable depth frames from the provider
- `ARShaderOcclusion` receives enough metadata to build usable globals
- subsystem reports unsupported features as unsupported

### 2. Depth Data Fidelity

Depth must behave like ARKit environment depth, not like a debug texture.

Required behavior:

- metric depth values in meters
- depth aligned to the same replay camera frame as RGB
- correct intrinsics / projection assumptions
- correct orientation and sampling convention
- correct handling of invalid depth values
- consistent scaling under session alignment / world scale changes

Current policy:

- `.bin` raw float depth is the canonical path
- normalized PNG/JPG depth may remain as a fallback, but should not be treated
  as the reference implementation for provider-quality occlusion

Relevant files:

- [ZipFormat.md](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Docs/ZipFormat.md)
- [FrameLoading.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Library/FrameLoading.cs)
- [Depth.cs](/Users/yzigm/Desktop/sensorflex-unity-testbed/Packages/com.sensorflex.player.unity/Runtime/Subsystem/Depth.cs)

Definition of done:

- metric depth is the primary tested path
- changing world alignment scale changes occlusion depth scale consistently
- depth compares correctly against rendered virtual geometry

### 3. Rendering Integration

Visible occlusion must apply to virtual content generically.

Important distinction:

- camera background shaders draw the replay image
- object shaders or render-pipeline passes determine whether virtual objects
  disappear behind environment depth

Required outcome:

- arbitrary virtual 3D objects in the host app can be occluded
- the solution should not depend on each app writing SensorFlex-specific code

Preferred implementation direction:

- first validate how far `AROcclusionManager` + `ARShaderOcclusion` gets us in
  a representative URP host scene
- if that is not enough, add a URP renderer-feature path for generic hard
  occlusion

Decision rule:

- if every material/shader in the host app must opt in manually, the provider
  is not yet "drop-in" enough
- a render-pipeline-level solution is preferred if needed to cover general
  virtual content

Definition of done:

- a normal rendered test object is visibly occluded by environment depth
- the effect works without modifying that object's gameplay code

### 4. Provider Truthfulness

The provider should only claim support for what it actually implements.

Must verify:

- environment depth support flag
- confidence image support flag
- temporal smoothing support flag
- raw vs smoothed depth behavior
- source-mode limitations such as FileSystem / ZIP / WebSocket

Definition of done:

- descriptor support delegates match real implementation
- unsupported requests degrade predictably
- log output is clear when features are unavailable

### 5. Host-App Compatibility

The final system should be tested as if it were used by an external app team.

Validation scenarios:

- host app with `ARCameraBackground` and `AROcclusionManager`
- ordinary virtual mesh using standard project material/shader
- session alignment enabled with non-unit world scale
- replay source with metric float depth
- provider swap without custom scene-side hacks

Definition of done:

- developer can enable SensorFlex loader and run the app directly
- visible occlusion works in a representative host-app scene

---

## Immediate Next Tasks

If continuing from the current repo state, the next work should happen in this
order.

### Task 1. Validate whether generic visible occlusion already works

Do not assume the recent provider changes are sufficient.

Test:

- a normal virtual cube or mesh in front of the replay camera
- standard URP material if possible
- replay with metric depth enabled
- `AROcclusionManager` and `ARShaderOcclusion` enabled

Questions to answer:

- does the object get occluded at all
- does it only work for some shaders/materials
- is the occlusion spatially aligned
- does world scale break it

### Task 2. If not generic enough, implement a URP-wide hard-occlusion path

This is the most likely missing piece for true provider-like behavior.

Implementation direction:

- add a URP renderer feature / full-screen pass
- compare rendered virtual depth against environment depth
- start with hard occlusion only
- keep the logic provider-agnostic from the host app's point of view

### Task 3. Promote ZIP depth to the main occlusion input path

The repo already has the right data shape in ZIP playback.

Likely work:

- surface `DepthBins` through the occlusion subsystem
- produce GPU textures from ZIP metric depth
- avoid treating normalized image depth as the main long-term solution

### Task 4. Re-check support flags and docs after behavior stabilizes

Once rendering and source support are settled:

- tighten descriptor support claims
- update `Architecture.md` if needed
- keep this task doc synchronized with actual implementation

---

## Suggested Engineering Checklist

- [ ] Confirm the canonical environment-depth source format is metric float.
- [ ] Verify depth frame index stays locked to the replay camera frame index.
- [ ] Verify replay camera intrinsics used by occlusion match the rendered view.
- [ ] Verify world alignment scale affects depth consistently.
- [ ] Verify `AROcclusionManager` receives texture descriptors every frame.
- [ ] Verify `ARShaderOcclusion` receives usable occlusion-frame metadata.
- [ ] Test visible hard occlusion on a normal virtual mesh.
- [ ] Decide whether `ARShaderOcclusion` is sufficient or whether URP renderer
      integration is required.
- [ ] If needed, implement a renderer-feature-based hard occlusion pass.
- [ ] Re-test with a host-app style scene that was not built specifically for
      SensorFlex.
- [ ] Route ZIP metric depth through the occlusion subsystem.
- [ ] Update support flags and docs after the implementation direction is final.

---

## Validation Notes

When testing, explicitly distinguish between these failure modes:

- no depth available at all
- depth available but not consumed by AR Foundation
- AR Foundation consuming depth but no visible object occlusion
- visible occlusion present but spatially misaligned
- visible occlusion aligned but broken under world scale

Those failures imply different owners:

- provider contract issue
- scene/component wiring issue
- render-pipeline integration issue
- data alignment issue
- world-scale handling issue

---

## Non-Goals For The First Successful Milestone

These are not required before the first meaningful handoff completion:

- human stencil / human depth segmentation
- soft occlusion polish before hard occlusion is correct
- recorder-side capture work
- solving all AR Foundation subsystem parity beyond what host apps need for the
  first target scenarios

---

## Final Deliverable

Deliver a SensorFlex occlusion system that is:

- AR Foundation compatible
- world-scale aware
- metric-depth correct
- visibly effective on general virtual 3D content
- usable by host applications through XR provider swap rather than custom
  scene-specific glue
