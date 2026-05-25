# SensorFlex Player Architecture

This package implements a lightweight XR provider stack for replaying SensorFlex capture data inside Unity through AR Foundation interfaces.

## Goals

- Present recorded color, pose, intrinsics, optional mesh, and optional depth data as XR subsystems and package runtime components.
- Support multiple frame sources behind one runtime-facing loader contract.
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

- `Runtime/Subsystem/Camera.cs`
  Owns playback timing and exposes camera frames to AR Foundation. Creates `FrameLoader`, advances playback each frame, updates intrinsics and the projection matrix from recorded per-frame intrinsics, pushes poses into `PoseBridge`, and publishes the scanned mesh via `ScannedSceneMeshBridge`.

- `Runtime/Subsystem/Depth.cs`
  Exposes environment depth through `XROcclusionSubsystem`. Implemented for FileSystem depth images only; synchronized to camera frame progression via `OnFramesReady`. WebSocket mode is not supported.

- `Runtime/Subsystem/Session.cs`
  Provides a minimal `XRSessionSubsystem` implementation so the package can participate in the XR lifecycle.

- `Runtime/Bridges/PoseBridge.cs`
  Static event bridge carrying camera poses from `CameraSubsystem` to scene components. Fires `OnPoseUpdated` each frame; `ARSensorFlexSession` subscribes to drive the camera rig.

- `Runtime/Bridges/ScannedSceneMeshBridge.cs`
  Static event bridge carrying the loaded `Mesh` from `CameraSubsystem` to scene components. Fires `OnMeshReady` once; `ARSensorFlexSceneMesh` subscribes to instantiate the mesh.

- `Runtime/Library/FrameLoading.cs`
  Frame-loading orchestration and backend abstraction:
  - `FrameLoader`: subsystem-facing facade
  - `IFrameLoaderState`: shared data contract between facade and backends
  - `IFrameLoaderBackend`: backend contract
  - `FileSystemFrameLoaderBackend`
  - `ZipFrameLoaderBackend`
  - `WebSocketFrameLoaderBackend`

- `Runtime/Library/ArchiveIOUtils.cs`
  Stateless helpers for archive reads, JSON float extraction, matrix construction, pose conversion, and projection construction from recorded intrinsics.

- `Runtime/Library/ScannedSceneMeshLoading.cs`
  Scene-mesh loading path for ZIP archives. Parses the packaged PLY mesh on a background thread, converts coordinates into Unity space, preserves vertex colors, and builds the runtime `Mesh`.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` resolves the active `ARSensorFlexSession` for config.
4. For ZIP archives, the camera provider starts `ScannedSceneMeshLoadOperation` on a background thread and publishes the result via `ScannedSceneMeshBridge` once complete.
5. The camera provider creates `FrameLoader` and selects a backend based on `FrameSourceMode`.
6. The selected backend fills shared loader state with textures and, where available, pose/intrinsics/depth data.
7. The camera provider advances playback each Unity frame and publishes data through **two parallel paths**:

   **Path 1 — AR Foundation subsystem APIs:**
   - Color textures → `XRCameraSubsystem` → `ARCameraManager` / background rendering
   - Per-frame intrinsics → `TryGetIntrinsics`
   - Projection matrix derived from intrinsics → AR Foundation camera APIs
   - Frame timestamp and camera state → AR Foundation camera APIs
   - Environment depth → `XROcclusionSubsystem` (FileSystem mode only)
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
  │          │                  │  │  Subsystem  │  │ (FileSystem only)  │ │
  │          │  ┌─────────────┐ │  │ stub        │  │                    │ │
  │          │  │ FrameLoader │ │  └─────────────┘  └────────────────────┘ │
  │          │  │ ┌─────────┐ │ │                            │              │
  │          │  │ │FileSys  │ │ │                            │              │
  │          │  │ │ZIP      │ │ │                            │              │
  │          │  │ │WebSocket│ │ │                            │              │
  │          │  │ └────┬────┘ │ │                            │              │
  │          │  │      │      │ │                            │              │
  │          │  │ ArchiveIO   │ │                            │              │
  │          │  │ Utils       │ │                            │              │
  │          │  └─────────────┘ │                            │              │
  │          └────────┬─────────┘                            │              │
  │                   │                                      │              │
  └───────────────────┼──────────────────────────────────────┼──────────────┘
                      │ publishes via two paths               │
                      │                                       │
        ┌─────────────┴──────────────────┐                   │
        │                                │                   │
        ▼                                ▼                   ▼
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
  ║  XROcclusionSubsystem ◄──────╫──╫──(subscribes OnPoseUpdated)          ║
  ║  • depth texture (FileSystem)║  ║  drives XROrigin camera rig           ║
  ║                              ║  ║                                       ║
  ║  XRSessionSubsystem          ║  ║  ScannedSceneMeshBridge               ║
  ║  • session tracking state    ║  ║  ─────────────────────────────────   ║
  ║                              ║  ║  CameraSubsystem calls                ║
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
  The package publishes camera color, intrinsics, projection, session state, and optional environment depth through standard XR subsystem APIs. Host applications consume this through `ARCameraManager`, `ARCameraBackground`, `AROcclusionManager`, and other AR Foundation components without any package-specific code.

- **Path 2 — Bridges (scene components)**
  The package provides two scene components that consume bridge events:
  - `ARSensorFlexSession` — attach to `XROrigin` to drive the replay camera rig from `PoseBridge` pose events. Also applies session-level alignment and clip-plane overrides.
  - `ARSensorFlexSceneMesh` — attach to any child object to instantiate the scanned scene mesh from `ScannedSceneMeshBridge`. Supports material override and optional `MeshCollider`.

  For most host applications the intended integration is: one `ARSensorFlexSession` on `XROrigin`, one `ARSensorFlexSceneMesh` on a child object.

- **`PoseBridge` and `ScannedSceneMeshBridge` as direct APIs**
  Both bridges are public static APIs and can be subscribed to by arbitrary host code without using the package's scene components, for advanced or custom integration.

## Backend Responsibilities

### FileSystem

- Resolves `imageFolder` relative to `StreamingAssets`
- Loads and decodes all color images eagerly at startup
- Marks the loader ready once preload completes

### WebSocket

- Connects asynchronously to `webSocketUrl`
- Sends a `hello` handshake with buffer size and warm-up count
- Receives a `scene` JSON message for metadata (FPS, frame count, coordinate system)
- Receives binary frame packets identified by a 4-byte magic header (RGB + meta JSON + optional depth)
- Drains decoded frames to GPU in batches of 3 per frame via `DrainUploadQueue()`
- Sends `window` messages back to the server as `PlayHead` advances

### ZIP

- Opens the archive and reads the root `meta.json` for scene metadata
- Builds the coordinate conversion matrix from archive metadata
- Streams frames on a dedicated background thread into a ring buffer
- Back-pressure: the loader thread sleeps when the ring buffer is full
- Publishes RGB, raw depth bytes, pose matrices, and intrinsics per frame
- Loads the packaged scanned mesh on a background thread and publishes it via `ScannedSceneMeshBridge`
- Main thread drains GPU uploads in batches of 3 per frame via `DrainUploadQueue()`

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- ZIP archive frame reads happen on a dedicated background thread (`SF-ZipLoader`).
- ZIP and WebSocket texture creation and GPU upload happen on the main thread (via `DrainUploadQueue()`).
- WebSocket message dispatch is driven from the main thread through the camera subsystem update loop.
- ZIP mesh reads and PLY parsing happen on a background `Task` thread; the `Mesh` object is built on the main thread by `TryComplete()`.

## Current Design Constraints

- `OcclusionSubsystem` supports FileSystem depth images only. WebSocket depth is silently disabled.
- ZIP depth bytes are loaded into `FrameLoader.DepthBins` but are not yet surfaced through `XROcclusionSubsystem`.
- `PoseBridge` and `ScannedSceneMeshBridge` are package-global static APIs; they are not session-isolated.
- `FrameLoader` is the integration seam between subsystem code and IO backends. New sources should be added as `IFrameLoaderBackend` implementations rather than expanding the facade.
