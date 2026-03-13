# SensorFlex Player Occlusion System Task

## Goal

Build the SensorFlex player occlusion path so it behaves like an ARKit-style
AR Foundation provider.

The target outcome is:

1. a Unity developer can switch XR provider to SensorFlex
2. keep using standard AR Foundation components
3. run their app without rewriting game logic
4. get visible environment-depth occlusion of virtual 3D content

This is not only a depth-texture task. It is a provider-compatibility task
across subsystem data, rendering behavior, and AR Foundation integration.

---

## Product Standard

The occlusion system should be considered done when a normal AR Foundation app
that uses:

- `ARSession`
- `ARCameraManager`
- `ARCameraBackground`
- `AROcclusionManager`
- ordinary scene geometry / app materials

can switch to the SensorFlex XR loader and still:

- show replayed camera imagery
- receive environment depth
- render virtual content in the expected pose space
- occlude virtual objects against replay depth
- preserve world alignment and world scale behavior

The success bar is "swap provider and run", not "custom demo scene works".

---

## Current State

The project already has:

- a custom `XROcclusionSubsystem` in `Runtime/Subsystem/Depth.cs`
- a custom camera subsystem
- a session alignment system
- `AROcclusionManager` support in the scene
- `ARShaderOcclusion` added to the scene camera
- file-system depth loading and metric `.bin` scaling support

The project does not yet fully guarantee ARKit-like drop-in behavior.

Remaining risk areas:

- render-path coverage for arbitrary virtual content
- truthfulness and completeness of occlusion frame metadata
- fidelity of the depth source format
- compatibility with non-demo host application shaders/materials

---

## Scope

This task is specifically about **environment depth occlusion** for AR
Foundation applications using the SensorFlex player package.

In scope:

- `XROcclusionSubsystem` behavior
- environment depth publication
- AR Foundation occlusion integration
- world alignment / world scale interaction with depth
- visible rendering of occlusion on virtual content

Out of scope for this task unless needed as dependencies:

- recorder package
- human segmentation
- non-depth AR features such as plane detection or anchors

---

## Core Requirement

The occlusion path must behave like a provider, not like a scene-specific
effect.

That means:

- AR Foundation managers should consume it through subsystem APIs
- host applications should not need SensorFlex-specific rendering scripts
- the system should work with standard AR Foundation setup patterns
- support flags and mode reporting should match real behavior

---

## Workstreams

### 1. Subsystem Contract Completion

The custom occlusion subsystem must satisfy AR Foundation's expected runtime
contract, not just expose a single texture.

Required behavior:

- support `requestedEnvironmentDepthMode`
- report `currentEnvironmentDepthMode` truthfully
- publish depth through `TryGetEnvironmentDepth(...)`
- publish current frame descriptors through `GetTextureDescriptors(...)`
- publish occlusion frame metadata through `TryGetFrame(...)`
- keep timestamps aligned with camera playback
- keep pose / FOV / near-far metadata coherent with the replay camera

Definition of done:

- `AROcclusionManager` receives stable depth frames from the provider
- `ARShaderOcclusion` receives enough metadata to build its globals
- subsystem reports unsupported features as unsupported

### 2. Depth Data Fidelity

Depth must behave like ARKit environment depth, not like a debug image.

Required behavior:

- metric depth values in meters
- depth aligned to the same replay camera frame as RGB
- consistent intrinsics / projection assumptions
- correct orientation and screen-space sampling convention
- correct handling of invalid depth
- consistent scaling under session alignment / world scale changes

Current decision:

- `.bin` raw float depth is the canonical path for correct occlusion
- normalized PNG/JPG depth may remain as a fallback, but should not be the
  reference implementation for ARKit-like behavior

Definition of done:

- metric depth is the primary tested path
- changing world alignment scale changes occlusion depth scale consistently
- depth compares correctly against rendered virtual geometry

### 3. Rendering Integration

Visible occlusion must apply to virtual content generically.

Important distinction:

- camera background shaders draw the replay image
- object/material shaders or pipeline passes determine whether virtual objects
  disappear behind environment depth

This task should avoid a solution that only fixes the reconstructed mesh.

Required outcome:

- arbitrary virtual 3D objects in the host application can be occluded
- the solution should not depend on each app writing SensorFlex-specific code

Preferred implementation direction:

- first verify how far `AROcclusionManager` + `ARShaderOcclusion` gets us
- if standard AR Foundation shader-global behavior is not enough, add a
  URP renderer-feature path for generic hard occlusion

Decision rule:

- if per-shader integration is needed for every material, the provider is not
  yet "drop-in" enough
- a render-pipeline-level solution is preferred if it is needed to cover
  general virtual content

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

## Implementation Priorities

### Priority 1

Make the occlusion subsystem contract complete and stable.

Tasks:

- verify `Depth.cs` frame metadata against AR Foundation expectations
- verify timestamp, pose, FOV, and clip-plane correctness
- remove any remaining assumptions that only work in the demo scene

### Priority 2

Lock the primary depth path to metric float data.

Tasks:

- prefer `.bin` float depth for correctness
- validate depth alignment against replay camera intrinsics
- keep scale changes synchronized with depth reload/update behavior

### Priority 3

Make visible occlusion generic.

Tasks:

- validate whether `ARShaderOcclusion` alone gives acceptable coverage
- if not, implement URP hard-occlusion integration at renderer level
- avoid limiting the effect to SensorFlex-specific materials

### Priority 4

Tighten provider support flags and compatibility behavior.

Tasks:

- document FileSystem-only limitations if still present
- keep ZIP/WebSocket support claims accurate
- add explicit warnings where behavior is degraded or partial

---

## Suggested Engineering Checklist

- [ ] Confirm the canonical environment-depth source format is metric float.
- [ ] Verify depth frame index stays locked to the replay camera frame index.
- [ ] Verify replay camera intrinsics used by occlusion match the rendered view.
- [ ] Verify world alignment scale affects depth consistently.
- [ ] Verify `AROcclusionManager` receives texture descriptors every frame.
- [ ] Verify `ARShaderOcclusion` receives usable occlusion-frame metadata.
- [ ] Test visible hard occlusion on a normal virtual mesh.
- [ ] Decide whether per-shader support is sufficient or whether URP renderer
      integration is required.
- [ ] If needed, implement a renderer-feature-based hard occlusion pass.
- [ ] Re-test with a host-app style scene that was not built specifically for
      SensorFlex.

---

## Known Non-Goals

These are explicitly not required for the first successful occlusion milestone:

- human stencil / human depth segmentation
- soft occlusion polish before hard occlusion is correct
- recorder-side capture work
- solving all AR Foundation subsystem parity beyond what host apps actually
  require for the first target scenarios

---

## Decision Notes

### Why this should be provider-first

The project goal is to let developers swap XR provider and run their
application directly. That only works if the SensorFlex player behaves like a
real AR Foundation provider, not a custom rendering demo.

### Why metric depth matters

ARKit-like occlusion is fundamentally a metric depth comparison against
rendered virtual geometry. Normalized image encodings are acceptable for
debugging, but they are not the reference path for provider-equivalent
occlusion behavior.

### Why generic render integration matters

A solution that only occludes the reconstructed mesh or only works with one
custom shader does not meet the provider-swap goal. The render path must be
generic enough for ordinary host-app virtual content.

---

## Deliverable

Deliver a SensorFlex occlusion system that is:

- AR Foundation compatible
- world-scale aware
- metric-depth correct
- visibly effective on general virtual 3D content
- usable by host applications through XR provider swap rather than custom
  scene-specific glue
