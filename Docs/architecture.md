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
  Owns playback timing and exposes camera frames to AR Foundation. Creates a `SessionLoader`, calls `Tick()` once per frame to drive the session state machine, advances the ring-buffer playhead, updates intrinsics and projection matrix from per-frame data, and pushes poses into `PoseBridge`.

- `Runtime/Subsystem/Depth.cs`
  Exposes environment depth through `XROcclusionSubsystem`. Reads raw depth bytes from the `SessionLoader` ring buffer each frame (all three source modes supported). Synchronized to camera frame progression via `OnFramesReady`.

- `Runtime/Subsystem/Session.cs`
  Provides a minimal `XRSessionSubsystem` implementation so the package can participate in the XR lifecycle.

- `Runtime/Library/Bridges/PoseBridge.cs`
  Static event bridge carrying camera poses from `CameraSubsystem` to scene components.

- `Runtime/Library/Bridges/ScannedSceneMeshBridge.cs`
  Static event bridge carrying the loaded `Mesh` from `SessionLoader` to scene components. Fires `OnMeshReady` once per session; `ARSensorFlexSceneMesh` subscribes to instantiate the mesh.

- `Runtime/Library/Bridges/ControlBridge.cs`
  Static event bridge for playback control commands (play/pause/step/restart/speed). `ARSensorFlexReplayController` drives it; `CameraSubsystem` reacts.

- `Runtime/Library/IO/SessionLoader.cs`
  Session lifecycle coordinator. Owned by `CameraSubsystem`. Drives a four-state machine (`Idle → Waiting → Loading → Ready`) through `Tick()` called once per Unity frame. Defines the `ISessionBackend` three-phase contract, the generic session data model (`SfzSessionData`, `SfzTrackInfo`, `SfzAttachmentInfo`), and the `ProcessAttachments()` orchestration that starts PLY mesh loads for all source modes.

- `Runtime/Library/IO/FrameLoader.cs`
  Defines `IFrameLoaderState` and `FrameLoaderState` — the ring-buffer state owned by each backend. Backends allocate a `FrameLoaderState` in `StartLoading()` and expose it via `ISessionBackend.State`.

- `Runtime/Library/IO/SfzBackends.cs`
  `ISessionBackend` implementations for ZIP-archive (`SfzFrameLoaderBackend`) and loose-file (`FileIoFrameLoaderBackend`) sessions. Both extend `SfzBackendBase` which streams frames on a dedicated background thread into the ring buffer, throttled by `PlayHead`.

- `Runtime/Library/IO/LiveWebSocketBackend.cs`
  `ISessionBackend` implementation for live WebSocket streaming. Connects asynchronously with auto-reconnect, receives `session.json` as a text message, `SFAT` binary packets for attachments, and `SFWP` binary frame packets. Holds frame drain until all expected attachments have been consumed by `SessionLoader`.

- `Runtime/Library/IO/ScannedMeshLoader.cs`
  Background PLY parse operation. `ScannedSceneMeshLoadOperation.StartFromPlyBytes()` is called by `SessionLoader.ProcessAttachments()` for all source modes. Parses ASCII and binary-little-endian PLY on a background `Task`; `TryComplete()` uploads the `Mesh` on the main thread.

- `Runtime/Library/IO/SfzUtils.cs`
  Stateless helpers: ZIP entry reads, JSON float extraction, matrix construction, pose conversion (`SfzPoseToMatrix4x4`, `ConvertToUnityPose`), intrinsics extraction, and projection matrix construction.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` resolves the active `ARSensorFlexSession` for config.
4. Camera creates a `SessionLoader` and calls `Start()`, which opens the appropriate `ISessionBackend`.
5. Each Unity frame, `Tick()` drives the state machine:
   - **Waiting** — polls `TryGetSessionJson()` until session metadata arrives, then calls `StartLoading()`.
   - **Loading** — calls `DrainMainThreadWork()` (GPU uploads) and `ProcessAttachments()` (starts PLY mesh parse when bytes are available); transitions to **Ready** once enough frames are buffered.
   - **Ready** — continues draining and processing attachments.
6. The selected backend fills the ring buffer with textures, pose matrices, intrinsics, and depth bytes. All three source modes (SFZ, FileIo, Live) follow the same contract.
7. The camera provider advances the ring-buffer playhead and publishes data through **two parallel paths**:

   **Path 1 — AR Foundation subsystem APIs:**
   - Color textures → `XRCameraSubsystem` → `ARCameraManager` / background rendering
   - Per-frame intrinsics → `TryGetIntrinsics`
   - Projection matrix derived from intrinsics → AR Foundation camera APIs
   - Frame timestamp and camera state → AR Foundation camera APIs
   - Environment depth bytes → `XROcclusionSubsystem` (all source modes)
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
  │          ┌──────────────────┐  ┌─────────────┐  ┌────────────────────┐ │
  │          │  CameraSubsystem │  │   Session   │  │ OcclusionSubsystem │ │
  │          │                  │  │  Subsystem  │  │                    │ │
  │          │  ┌─────────────┐ │  │  (stub)     │  └──────────┬─────────┘ │
  │          │  │SessionLoader│ │  └─────────────┘             │ reads     │
  │          │  │ state:      │ │                               │ ring buf  │
  │          │  │  Waiting    │ │                               │           │
  │          │  │  Loading    ├─┼───────────────────────────────┘           │
  │          │  │  Ready      │ │                                           │
  │          │  │             │ │                                           │
  │          │  │ ISessionBackend (per source mode)                        │
  │          │  │ ┌──────────┐│ │                                           │
  │          │  │ │SFZ / ZIP ││ │                                           │
  │          │  │ │FileIo    ││ │  ◄── SfzUtils (helpers)                   │
  │          │  │ │Live / WS ││ │                                           │
  │          │  │ └──┬───────┘│ │                                           │
  │          │  │    │ring buf│ │                                           │
  │          │  │  FrameLoader│ │                                           │
  │          │  │  State      │ │                                           │
  │          │  │             │ │                                           │
  │          │  │ Attachments:│ │                                           │
  │          │  │ ScannedMesh │ │                                           │
  │          │  │ Loader      │ │                                           │
  │          │  └─────────────┘ │                                           │
  │          └────────┬─────────┘                                           │
  │                   │                                                     │
  └───────────────────┼─────────────────────────────────────────────────────┘
                      │ publishes via two paths
                      │
        ┌─────────────┴──────────────────┐
        │                                │
        ▼                                ▼
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
  ║    (all source modes)        ║  ║                                       ║
  ║                              ║  ║  ScannedSceneMeshBridge               ║
  ║  XRSessionSubsystem          ║  ║  ─────────────────────────────────   ║
  ║  • session tracking state    ║  ║  SessionLoader calls                  ║
  ║                              ║  ║  SetMesh() once on load               ║
  ║         │                    ║  ║         │                             ║
  ║         ▼                    ║  ║         ▼                             ║
  ║  AR Foundation managers      ║  ║  ARSensorFlexSceneMesh                ║
  ║  ARCameraManager             ║  ║  (subscribes OnMeshReady)             ║
  ║  AROcclusionManager          ║  ║  instantiates MeshFilter /            ║
  ║  Standard AR app code        ║  ║  MeshRenderer in scene                ║
  ╚══════════════════════════════╝  ╚═══════════════════════════════════════╝
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

## Backend Responsibilities

All three source modes implement the same `ISessionBackend` three-phase contract:

1. **`Open(session)`** — validate and open the data source.
2. **`TryGetSessionJson(out json)`** — polled each `Tick()` until session metadata is available. File backends return immediately; the Live backend returns `false` until the server sends JSON.
3. **`StartLoading(data, bufSize, framesToWait)`** — allocate the ring buffer and begin streaming.

### SFZ (ZIP archive)

- Validates the archive path relative to `StreamingAssets`
- Reads `session/session.json` from the archive on first poll
- Streams frames on a dedicated background thread (`SF-SFZ`); sleeps when the ring buffer is full
- `TryGetAttachmentBytes()` opens the archive once to read the attachment file
- Supports loop playback across multiple passes (`BeginLoadPass`/`EndLoadPass` keep the archive open for the full pass)

### FileIo (loose files)

- Validates the session directory path relative to `StreamingAssets`
- Reads `session.json` from the directory on first poll
- Identical streaming logic to SFZ via `SfzBackendBase`
- `TryGetAttachmentBytes()` reads the attachment file directly from disk

### Live (WebSocket)

- Connects asynchronously to `webSocketUrl` with auto-reconnect (up to 5 attempts)
- Sends a `hello` handshake after connect; server replies with session JSON
- Receives `SFAT` binary packets (attachments) and `SFWP` binary frame packets
- Frame drain is gated: `DrainMainThreadWork()` holds until all expected attachments have been consumed by `SessionLoader.ProcessAttachments()`
- Publishes RGB, optional depth, pose, and intrinsics per frame from packet metadata JSON

## Session State Machine

`SessionLoader.Tick()` drives this state machine once per Unity frame:

```
  Idle ──► Waiting ──► Loading ──► Ready
             │            │
     TryGetSessionJson  DrainMainThreadWork
     polls each frame   ProcessAttachments
                        polls each frame
```

- **Waiting** — `TryGetSessionJson()` is polled; on success, session metadata is parsed and `StartLoading()` is called.
- **Loading** — GPU uploads drain each frame; attachments are processed as bytes arrive; transitions to **Ready** when `IFrameLoaderState.IsReady` is true (enough frames buffered).
- **Ready** — continues draining and processing attachments for the rest of the session.

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- SFZ and FileIo frame reads happen on dedicated background threads (`SF-SFZ`, `SF-FileIo`).
- Texture creation and GPU upload (`LoadImage` / `Apply`) happen on the main thread via `DrainMainThreadWork()`, batched at 3 uploads per frame.
- WebSocket message dispatch is driven from the main thread through `Dispatch()` called by `Tick()`.
- PLY mesh parsing runs on a background `Task` thread; the `Mesh` object is built on the main thread by `TryComplete()`.

## Current Design Constraints

- `PoseBridge` and `ScannedSceneMeshBridge` are package-global static APIs; they are not session-isolated.
- New data types beyond color/depth/pose/intrinsics should be modelled as `SfzTrackInfo` entries or `SfzAttachmentInfo` entries in `SfzSessionData`; `ProcessAttachments()` in `SessionLoader` is the extension point for new attachment handlers.
- The `SfzUtils.SfzSessionJson` JSON schema is the authoritative definition of what `session.json` may contain.
