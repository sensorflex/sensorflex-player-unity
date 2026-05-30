# SensorFlex Player Architecture

This package implements a lightweight XR provider stack for replaying SensorFlex capture data inside Unity through AR Foundation interfaces.

## Goals

- Present recorded color, pose, intrinsics, optional mesh, and optional depth data as XR subsystems and package runtime components.
- Support multiple frame sources behind one runtime-facing session contract.
- Minimize host-app integration work by keeping the primary public setup path inside the package.

## Main Components

- `Runtime/Loader.cs`
  Registers and starts the custom XR subsystems through Unity XR Management.

- `Runtime/Settings.cs`
  Defines `SensorFlexSettings`, the runtime configuration entry point. It selects the frame source mode, preload count, FPS, optional session alignment, and optional depth settings.

- `Runtime/ARSensorFlexSession.cs`
  Primary host-app integration component. Attach to `XROrigin` (or any scene object). Drives the replay camera rig by consuming `PoseBridge` pose events and applying position/rotation to the `XROrigin.CameraFloorOffsetObject`. Also applies session alignment transform and clip-plane overrides. Exposes `AutoPlay` — when false, playback starts paused after loading completes. Does **not** handle mesh instantiation — see `ARSensorFlexSceneMesh`.

- `Runtime/ARSensorFlexSceneMesh.cs`
  Scene mesh integration component. Attach to any child of `XROrigin`. Listens to `ScannedSceneMeshBridge.OnMeshReady` and instantiates the loaded PLY mesh as a `MeshFilter`/`MeshRenderer` under its own transform. Supports an optional material override and optional `MeshCollider`.

- `Runtime/ARSensorFlexReplayController.cs`
  Optional UI/scripting bridge that exposes play, pause, step-forward, restart, speed control, and depth visualisation to host applications through `ControlBridge`.

- `Runtime/Subsystem/Camera.cs`
  Owns playback timing and exposes camera frames to AR Foundation. Calls `SfzSessionStore.StartSession()` on warmup and `SfzSessionStore.Tick()` once per Unity frame, advances the ring-buffer playhead, updates intrinsics and projection matrix from per-frame data, and pushes poses into `PoseBridge`. No direct IO — all session data is read through `SfzSessionStore` proxy properties.

- `Runtime/Subsystem/Depth.cs`
  Exposes environment depth through `XROcclusionSubsystem`. Reads raw depth bytes and ring-buffer state from `SfzSessionStore` each frame. Synchronized to camera frame progression via `OnFramesReady`.

- `Runtime/Subsystem/Session.cs`
  Provides a minimal `XRSessionSubsystem` implementation so the package can participate in the XR lifecycle.

- `Runtime/Library/Bridges/PoseBridge.cs`
  Static event bridge carrying camera poses from `CameraSubsystem` to scene components.

- `Runtime/Library/Bridges/ScannedSceneMeshBridge.cs`
  Static event bridge carrying the loaded `Mesh` from `SfzSessionStore` to scene components. Fires `OnMeshReady` once per session; `ARSensorFlexSceneMesh` subscribes to instantiate the mesh.

- `Runtime/Library/Bridges/ControlBridge.cs`
  Static event bridge for playback control commands (play/pause/step/restart/speed). `ARSensorFlexReplayController` drives it; `CameraSubsystem` reacts. Reads playhead position and total frame count from `SfzSessionStore`.

- `Runtime/Library/Store/SfzSessionStore.cs`
  Single source of truth for SFZ/FileIo session lifecycle and data access. `CameraSubsystem` drives it through `StartSession()` / `Tick()` / `StopSessionAsync()`; all other subsystems read state through typed proxy properties. Contains **all** SFZ IO as private nested types — nothing in this file is visible outside it:
  - `SfzSessionJson` / DTOs — session.json deserialization types
  - `IBackendState` / `BackendState` — ring-buffer state contract and implementation
  - `ISessionBackend` — three-phase backend contract (`Open` / `TryGetSessionJson` / `StartLoading`)
  - `SessionLoadState` — lifecycle enum (`Idle` / `Waiting` / `LoadingAttachments` / `Loading` / `Ready`)
  - `SfzTrackInfo`, `SfzAttachmentInfo`, `SfzSessionData` — parsed session data model
  - `SfzFileBackend` — ZIP archive source; streams frames on a background thread (`SF-SFZ`)
  - `FileIoBackend` — loose-file source; identical streaming logic (`SF-FileIo`)
  - `ScannedMeshLoaderImpl` — consumes `scene_mesh` attachment bytes, drives PLY parse, publishes to `ScannedSceneMeshBridge`
  - `ScannedSceneMeshLoadOperation` / `ScannedSceneMeshData` — task-based PLY load operation
  - `PlyMeshReader` — ASCII and binary-little-endian PLY decoder

- `Runtime/Library/Utils/MathUtils.cs`
  Stateless XR camera math. `ConvertToUnityPose()` converts a camera-to-world matrix from a source coordinate system to a Unity `Pose` via the symmetric formula `M_unity = C·M·C`. `ComputeProjectionMatrix()` builds an off-centre Unity projection matrix from pinhole intrinsics (fx, fy, cx, cy). Used by `CameraSubsystem`.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` resolves the active `ARSensorFlexSession` for config.
4. Camera calls `SfzSessionStore.StartSession()`, which opens the appropriate private backend (`SfzFileBackend` or `FileIoBackend`).
5. Each Unity frame, Camera calls `SfzSessionStore.Tick()`:
   - **Waiting** — polls `TryGetSessionJson()` until session metadata arrives; on success, session data is parsed and state advances to **LoadingAttachments**.
   - **LoadingAttachments** — `ScannedMeshLoaderImpl` consumes `scene_mesh` attachment bytes and waits for the async PLY parse to complete. Sessions with no attachments pass through immediately. The loading overlay reads "Loading attachment: scene mesh". Once all attachments are resolved, `StartLoading()` is called on the backend and state advances to **Loading**.
   - **Loading** — calls `DrainMainThreadWork()` (GPU uploads); transitions to **Ready** once enough frames are buffered (`framesToWait` threshold).
   - **Ready** — continues draining; `CameraSubsystem` begins advancing the playhead.
6. The active backend fills the ring buffer with textures, pose matrices, intrinsics, and depth bytes on a background thread.
7. The camera provider advances the ring-buffer playhead and publishes data through **two parallel paths**:

   **Path 1 — AR Foundation subsystem APIs:**
   - Color textures → `XRCameraSubsystem` → `ARCameraManager` / background rendering
   - Per-frame intrinsics → `TryGetIntrinsics`
   - Projection matrix derived from intrinsics → AR Foundation camera APIs
   - Frame timestamp and camera state → AR Foundation camera APIs
   - Environment depth bytes → `XROcclusionSubsystem`
   - Session tracking state → `XRSessionSubsystem`

   **Path 2 — Bridge APIs:**
   - Camera pose → `PoseBridge.SetUnityPose()` → `ARSensorFlexSession` drives `XROrigin` camera rig
   - Scanned mesh → `ScannedSceneMeshBridge.SetMesh()` → `ARSensorFlexSceneMesh` renders mesh in scene

8. Host applications consume AR Foundation data through standard managers and rendering components (Path 1), and replay motion and mesh through the package's scene components (Path 2).

## Architecture Diagram

```
  ┌─────────────────────────────────────────────────────────────────────────┐
  │  PACKAGE RUNTIME                                                        │
  │                                                                         │
  │  ┌──────────────────┐    ┌─────────────────────────────────────┐        │
  │  │ SensorFlex       │    │ ARSensorFlexSession                  │        │
  │  │ Settings         │    │ (config · alignment · AutoPlay)      │        │
  │  └────────┬─────────┘    └─────────────────────────────────────┘        │
  │           │ read at startup                                              │
  │           ▼                                                              │
  │  ┌─────────────────┐                                                    │
  │  │  XR Management  ├──► Loader ──────────────────────────┐              │
  │  └─────────────────┘       │ creates                      │             │
  │                    ┌───────┴──────────┐                   │             │
  │                    ▼                  ▼                   ▼             │
  │          ┌──────────────────┐  ┌──────────┐  ┌───────────────────────┐ │
  │          │  CameraSubsystem │  │ Session  │  │  OcclusionSubsystem   │ │
  │          │  timing + AR     │  │Subsystem │  │                       │ │
  │          │  Foundation APIs │  │ (stub)   │  └──────────┬────────────┘ │
  │          └────────┬─────────┘  └──────────┘             │              │
  │                   │ StartSession/Tick/Stop               │ reads        │
  │                   │                           MathUtils ─┤ depth buf    │
  │                   └──────────────┬───────────────────────┘              │
  │                                  ▼                                      │
  │          ┌────────────────────────────────────────────────────────────┐ │
  │          │  SfzSessionStore                    (Library/Store/)       │ │
  │          │                                                             │ │
  │          │  Idle ──► Waiting ──► LoadingAttachments ──► Loading ──► Ready│
  │          │                                                             │ │
  │          │  ┌──────────────────────────────────────────────────────┐  │ │
  │          │  │  SfzFileBackend / FileIoBackend  (private nested)     │  │ │
  │          │  │  background thread · ring buffer · GPU upload         │  │ │
  │          │  └──────────────────────────────────────────────────────┘  │ │
  │          │                                                             │ │
  │          │  ┌──────────────────────────────────────────────────────┐  │ │
  │          │  │  ScannedMeshLoaderImpl + PlyMeshReader (private)      │  │ │
  │          │  │  consumes scene_mesh · async PLY parse · publishes    │  │ │
  │          │  └──────────────────────────────────────────────────────┘  │ │
  │          └────────────────────────────────────────────────────────────┘ │
  │                              │                                           │
  └──────────────────────────────┼───────────────────────────────────────────┘
                                 │ publishes via two paths
                                 │
        ┌────────────────────────┴───────────────┐
        │                                        │
        ▼                                        ▼
  ╔══════════════════════════════╗  ╔═══════════════════════════════════════╗
  ║  PATH 1: AR Foundation       ║  ║  PATH 2: Bridges                     ║
  ║  Subsystem APIs              ║  ║                                       ║
  ║                              ║  ║  PoseBridge                           ║
  ║  XRCameraSubsystem           ║  ║  ─────────────────────────────────   ║
  ║  • color texture             ║  ║  CameraSubsystem calls                ║
  ║  • intrinsics (fx fy cx cy)  ║  ║  SetUnityPose() each frame            ║
  ║  • projection matrix         ║  ║         │                             ║
  ║  • timestamp                 ║  ║         ▼                             ║
  ║                              ║  ║  ARSensorFlexSession                  ║
  ║  XROcclusionSubsystem        ║  ║  (subscribes OnPoseUpdated)           ║
  ║  • depth texture             ║  ║  drives XROrigin camera rig           ║
  ║                              ║  ║                                       ║
  ║  XRSessionSubsystem          ║  ║  ScannedSceneMeshBridge               ║
  ║  • session tracking state    ║  ║  ─────────────────────────────────   ║
  ║                              ║  ║  SfzSessionStore calls                ║
  ║         │                    ║  ║  SetMesh() once on load               ║
  ║         ▼                    ║  ║         │                             ║
  ║  AR Foundation managers      ║  ║         ▼                             ║
  ║  ARCameraManager             ║  ║  ARSensorFlexSceneMesh                ║
  ║  AROcclusionManager          ║  ║  (subscribes OnMeshReady)             ║
  ║  Standard AR app code        ║  ║  instantiates MeshFilter /            ║
  ╚══════════════════════════════╝  ║  MeshRenderer in scene                ║
                                    ╚═══════════════════════════════════════╝
```

## Host Application Integration

There are two outward-facing publication paths:

- **Path 1 — AR Foundation (primary)**
  The package publishes camera color, intrinsics, projection, session state, and environment depth through standard XR subsystem APIs. Host applications consume this through `ARCameraManager`, `ARCameraBackground`, `AROcclusionManager`, and other AR Foundation components without any package-specific code.

- **Path 2 — Bridges (scene components)**
  The package provides scene components that consume bridge events:
  - `ARSensorFlexSession` — attach to `XROrigin` to drive the replay camera rig from `PoseBridge` pose events. Also applies session-level alignment and clip-plane overrides. Set `AutoPlay = false` to hold at the first frame after loading.
  - `ARSensorFlexSceneMesh` — attach to any child object to instantiate the scanned scene mesh from `ScannedSceneMeshBridge`. Supports material override and optional `MeshCollider`.
  - `ARSensorFlexReplayController` — optional component that exposes `ControlBridge` to Unity UI or scripting.

  For most host applications the intended integration is: one `ARSensorFlexSession` on `XROrigin`, one `ARSensorFlexSceneMesh` on a child object.

- **`PoseBridge` and `ScannedSceneMeshBridge` as direct APIs**
  Both bridges are public static APIs and can be subscribed to by arbitrary host code without using the package's scene components, for advanced or custom integration.

## SfzSessionStore Internals

`SfzSessionStore` owns the full SFZ/FileIo session lifecycle. `CameraSubsystem` is the only writer; all other subsystems are read-only consumers of its proxy properties. All IO types are private nested classes — nothing is visible outside the file.

### SFZ (ZIP archive) — `SfzFileBackend`

- Validates the archive path relative to `StreamingAssets`
- Reads `session/session.json` from the archive on first poll
- Streams frames on a dedicated background thread (`SF-SFZ`); sleeps when the ring buffer is full
- `TryGetAttachmentBytes()` reads the attachment file from the archive using `s_SessionData` (available from parse time, before `StartLoading`)
- Keeps the archive open across the full loop pass (`BeginLoadPass`/`EndLoadPass`) so the central directory is parsed only once per iteration

### FileIo (loose files) — `FileIoBackend`

- Validates the session directory path relative to `StreamingAssets`
- Reads `session.json` from the directory on first poll
- Identical streaming logic to SFZ via `SfzBackendBase`
- `TryGetAttachmentBytes()` reads the attachment file directly from disk using `s_SessionData`

## Session State Machine

`SfzSessionStore.Tick()` drives this state machine once per Unity frame.

```
  Idle ──► Waiting ──► LoadingAttachments ──► Loading ──► Ready
              │                │                  │
      TryGetSessionJson    ScannedMesh-       DrainMain-
      polls each frame     LoaderImpl         ThreadWork
                           Tick() each        each frame
                           frame
```

- **Waiting** — `TryGetSessionJson()` is polled each frame; on success, session metadata is parsed and state advances to **LoadingAttachments**.
- **LoadingAttachments** — `ScannedMeshLoaderImpl.Tick()` runs each frame. It consumes the `scene_mesh` attachment bytes from the backend and waits for the async PLY parse to finish. `IsComplete` is `true` immediately when no `scene_mesh` attachment exists. Once complete, `StartLoading()` is called on the backend and state advances to **Loading**.
- **Loading** — GPU uploads drain each frame via `DrainMainThreadWork()`; transitions to **Ready** when `BackendState.IsReady` is true (the `framesToWait` preload threshold is met).
- **Ready** — continues draining; `CameraSubsystem` advances the playhead each frame.

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- SFZ and FileIo frame reads happen on dedicated background threads (`SF-SFZ`, `SF-FileIo`).
- Texture creation and GPU upload (`LoadImage` / `Apply`) happen on the main thread via `DrainMainThreadWork()`, batched at 3 uploads per frame.
- PLY mesh parsing runs on a background `Task` thread; the `Mesh` object is built on the main thread by `TryComplete()`.

## Current Design Constraints

- `PoseBridge` and `ScannedSceneMeshBridge` are package-global static APIs; they are not session-isolated.
- `SfzSessionStore` is also a static (package-global) store. A future `LiveSessionStore` will implement the same public surface for live WebSocket streaming.
- New data types beyond color/depth/pose/intrinsics should be modelled as `SfzTrackInfo` entries or `SfzAttachmentInfo` entries in `SfzSessionData`; `ScannedMeshLoaderImpl` inside `SfzSessionStore` is the extension point for new attachment handlers — add a new branch in its `Tick()`.
- The private nested `SfzSessionJson` class is the authoritative definition of what `session.json` may contain.
