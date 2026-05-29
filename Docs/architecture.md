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
  Primary host-app integration component. Attach to `XROrigin` (or any scene object). Drives the replay camera rig by consuming `PoseBridge` pose events and applying position/rotation to the `XROrigin.CameraFloorOffsetObject`. Also applies session alignment transform and clip-plane overrides. Does **not** handle mesh instantiation — see `ARSensorFlexSceneMesh`.

- `Runtime/ARSensorFlexSceneMesh.cs`
  Scene mesh integration component. Attach to any child of `XROrigin`. Listens to `ScannedSceneMeshBridge.OnMeshReady` and instantiates the loaded PLY mesh as a `MeshFilter`/`MeshRenderer` under its own transform. Supports an optional material override and optional `MeshCollider`.

- `Runtime/ARSensorFlexReplayController.cs`
  Optional UI/scripting bridge that exposes play, pause, step-forward, restart, speed control, and depth visualisation to host applications through `ControlBridge`.

- `Runtime/Subsystem/Camera.cs`
  Owns playback timing and exposes camera frames to AR Foundation. Calls `SfzSessionStore.StartSession()` on warmup and `SfzSessionStore.Tick()` once per Unity frame, advances the ring-buffer playhead, updates intrinsics and projection matrix from per-frame data, and pushes poses into `PoseBridge`. No direct IO imports — all session data is read through `SfzSessionStore` proxy properties.

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
  Single source of truth for SFZ/FileIo session lifecycle and data access. `CameraSubsystem` drives it through `StartSession()` / `Tick()` / `StopSessionAsync()`; all other subsystems read state through typed proxy properties. Contains private nested types for all SFZ IO:
  - `SfzFileBackend` — ZIP archive source; streams frames on a background thread (`SF-SFZ`)
  - `FileIoBackend` — loose-file source; identical streaming logic (`SF-FileIo`)
  - `ScannedMeshLoaderImpl` — polls for `scene_mesh` attachment, kicks off PLY parse, publishes to `ScannedSceneMeshBridge`

- `Runtime/Library/IO/FrameLoader.cs`
  Shared session contracts. Defines `IBackendState` / `BackendState` (ring-buffer state), `ISessionBackend` (three-phase backend contract used by `LiveWebSocketBackend`), `SessionLoadState` enum, and the session data model (`SfzSessionData`, `SfzTrackInfo`, `SfzAttachmentInfo`).

- `Runtime/Library/IO/LiveWebSocketBackend.cs`
  `ISessionBackend` implementation for live WebSocket streaming. Connects asynchronously with auto-reconnect, receives `session.json` as a text message, `SFAT` binary packets for attachments, and `SFWP` binary frame packets. Will be promoted to a full `LiveSessionStore` in a future iteration.

- `Runtime/Library/IO/ScannedMeshLoader.cs`
  Background PLY parser. `ScannedSceneMeshLoadOperation.StartFromPlyBytes()` is called by `SfzSessionStore`; it parses ASCII and binary-little-endian PLY on a background `Task` and builds the `Mesh` on the main thread via `TryComplete()`.

- `Runtime/Library/IO/SfzUtils.cs`
  Stateless helpers: ZIP entry reads, JSON float extraction, matrix construction, pose conversion (`SfzPoseToMatrix4x4`, `ConvertToUnityPose`), intrinsics extraction, and projection matrix construction.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` resolves the active `ARSensorFlexSession` for config.
4. Camera calls `SfzSessionStore.StartSession()`, which opens the appropriate private backend (`SfzFileBackend` or `FileIoBackend`).
5. Each Unity frame, Camera calls `SfzSessionStore.Tick()`:
   - **Waiting** — polls `TryGetSessionJson()` until session metadata arrives, then calls `StartLoading()` on the backend.
   - **Loading** — calls `DrainMainThreadWork()` (GPU uploads); transitions to **Ready** once enough frames are buffered. In parallel, `ScannedMeshLoaderImpl` polls `TryConsumeAttachment("scene_mesh")` and starts the PLY parse when bytes are available.
   - **Ready** — continues draining; `ScannedMeshLoaderImpl` continues polling until the parse completes.
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
  │  ┌──────────────────┐    ┌──────────────────────────────┐               │
  │  │ SensorFlex       │    │ ARSensorFlexSession           │               │
  │  │ Settings         │    │ (config + alignment source)   │               │
  │  └────────┬─────────┘    └──────────────────────────────┘               │
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
  │                   │                                      │ depth buf    │
  │                   └──────────────┬───────────────────────┘              │
  │                                  ▼                                      │
  │          ┌────────────────────────────────────────────────────────────┐ │
  │          │  SfzSessionStore                    (Library/Store/)       │ │
  │          │                                                             │ │
  │          │  Idle ──► Waiting ──► Loading ──► Ready                    │ │
  │          │                                                             │ │
  │          │  ┌──────────────────────────────────────────────────────┐  │ │
  │          │  │  SfzFileBackend / FileIoBackend  (private nested)     │  │ │
  │          │  │  background thread · ring buffer · GPU upload   ◄─────┼──┼─── SfzUtils
  │          │  └──────────────────────────────────────────────────────┘  │ │
  │          │                                                             │ │
  │          │  ┌──────────────────────────────────────────────────────┐  │ │
  │          │  │  ScannedMeshLoaderImpl              (private nested)  │  │ │
  │          │  │  polls scene_mesh · drives PLY parse                  │  │ │
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
  - `ARSensorFlexSession` — attach to `XROrigin` to drive the replay camera rig from `PoseBridge` pose events. Also applies session-level alignment and clip-plane overrides.
  - `ARSensorFlexSceneMesh` — attach to any child object to instantiate the scanned scene mesh from `ScannedSceneMeshBridge`. Supports material override and optional `MeshCollider`.
  - `ARSensorFlexReplayController` — optional component that exposes `ControlBridge` to Unity UI or scripting.

  For most host applications the intended integration is: one `ARSensorFlexSession` on `XROrigin`, one `ARSensorFlexSceneMesh` on a child object.

- **`PoseBridge` and `ScannedSceneMeshBridge` as direct APIs**
  Both bridges are public static APIs and can be subscribed to by arbitrary host code without using the package's scene components, for advanced or custom integration.

## SfzSessionStore Internals

`SfzSessionStore` owns the full SFZ/FileIo session lifecycle. `CameraSubsystem` is the only writer; all other subsystems are read-only consumers of its proxy properties.

### SFZ (ZIP archive) — `SfzFileBackend`

- Validates the archive path relative to `StreamingAssets`
- Reads `session/session.json` from the archive on first poll
- Streams frames on a dedicated background thread (`SF-SFZ`); sleeps when the ring buffer is full
- `TryGetAttachmentBytes()` opens the archive once to read the attachment file
- Keeps the archive open across the full loop pass (`BeginLoadPass`/`EndLoadPass`) so the central directory is parsed only once per iteration

### FileIo (loose files) — `FileIoBackend`

- Validates the session directory path relative to `StreamingAssets`
- Reads `session.json` from the directory on first poll
- Identical streaming logic to SFZ via `SfzBackendBase`
- `TryGetAttachmentBytes()` reads the attachment file directly from disk

### Live (WebSocket) — `LiveWebSocketBackend`

- Separate file (`LiveWebSocketBackend.cs`), implements `ISessionBackend`; will become `LiveSessionStore` in a future iteration
- Connects asynchronously to `webSocketUrl` with auto-reconnect (up to 5 attempts)
- Sends a `hello` handshake after connect; server replies with session JSON
- Receives `SFAT` binary packets (attachments) and `SFWP` binary frame packets
- Frame drain is gated: `DrainMainThreadWork()` holds until all expected attachments have been consumed

## Session State Machine

`SfzSessionStore.Tick()` drives this state machine once per Unity frame. `ScannedMeshLoaderImpl` runs inside the same `Tick()` call and is responsible for attachment polling and mesh publishing independently of the frame state machine.

```
  Idle ──► Waiting ──► Loading ──► Ready
             │            │
     TryGetSessionJson  DrainMainThreadWork
     polls each frame   polls each frame
```

- **Waiting** — `TryGetSessionJson()` is polled; on success, session metadata is parsed and `StartLoading()` is called on the backend.
- **Loading** — GPU uploads drain each frame; transitions to **Ready** when `BackendState.IsReady` is true (enough frames buffered). Meanwhile, `ScannedMeshLoaderImpl` watches for `scene_mesh` bytes and starts the PLY parse.
- **Ready** — continues draining; `ScannedMeshLoaderImpl` continues polling until parse completes.

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- SFZ and FileIo frame reads happen on dedicated background threads (`SF-SFZ`, `SF-FileIo`).
- Texture creation and GPU upload (`LoadImage` / `Apply`) happen on the main thread via `DrainMainThreadWork()`, batched at 3 uploads per frame.
- WebSocket message dispatch is driven from the main thread through `Dispatch()` called by `Tick()`.
- PLY mesh parsing runs on a background `Task` thread; the `Mesh` object is built on the main thread by `TryComplete()`.

## Current Design Constraints

- `PoseBridge` and `ScannedSceneMeshBridge` are package-global static APIs; they are not session-isolated.
- `SfzSessionStore` is also a static (package-global) store. A future `LiveSessionStore` will share the same public surface and the two will be selected at session startup.
- New data types beyond color/depth/pose/intrinsics should be modelled as `SfzTrackInfo` entries or `SfzAttachmentInfo` entries in `SfzSessionData`; `ScannedMeshLoaderImpl` inside `SfzSessionStore` is the extension point for new attachment handlers — add a new branch in its `Tick()`.
- The `SfzUtils.SfzSessionJson` JSON schema is the authoritative definition of what `session.json` may contain.
