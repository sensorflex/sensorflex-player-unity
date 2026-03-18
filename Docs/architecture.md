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
  Primary public host-app integration component. It is intended to be attached to `XROrigin` and combines:
  - replay camera rig driving from `PoseBridge`
  - scanned mesh instantiation and rendering
  - camera clip-plane overrides
  - session alignment application on the active `XROrigin`

- `Runtime/Subsystem/Camera.cs`
  Owns playback timing and exposes camera frames to AR Foundation. It creates `FrameLoader`, advances playback, updates intrinsics, computes the replay projection matrix from recorded per-frame intrinsics, and pushes poses into `PoseBridge`.

- `Runtime/Subsystem/Depth.cs`
  Exposes environment depth through `XROcclusionSubsystem`. Today this is implemented for file-system depth images and synchronized to camera frame progression via `OnFramesReady`.

- `Runtime/Subsystem/Session.cs`
  Provides a minimal `XRSessionSubsystem` implementation so the package can participate in the XR lifecycle.

- `Runtime/Bridges/PoseBridge.cs`
  Acts as a public static pose handoff for host app code that wants the current camera pose without going through AR Foundation pose APIs.

- `Runtime/Bridges/ScannedSceneMeshBridge.cs`
  Carries the loaded scene mesh from the package runtime to public scene components such as `ARSensorFlexSession`.

- `Runtime/Library/FrameLoading.cs`
  Contains the frame-loading orchestration and backend abstraction:
  - `FrameLoader`: framework-facing facade used by the camera subsystem
  - `IFrameLoaderState`: shared data contract between the facade and backends
  - `IFrameLoaderBackend`: backend contract
  - `FileSystemFrameLoaderBackend`
  - `WebSocketFrameLoaderBackend`
  - `ZipFrameLoaderBackend`

- `Runtime/Library/ArchiveIOUtils.cs`
  Stateless helpers for archive reads, JSON float extraction, matrix construction, pose conversion, and projection construction from recorded intrinsics.

- `Runtime/Library/ScannedSceneMeshLoading.cs`
  Scene-mesh loading path for ZIP archives. It parses the packaged PLY mesh, converts coordinates into Unity space, preserves vertex colors, and builds the runtime `Mesh`.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the custom camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` loads `SensorFlexSettings`.
4. The camera provider begins scene-mesh loading for ZIP archives and publishes the result through `ScannedSceneMeshBridge`.
5. The camera provider creates `FrameLoader`.
6. `FrameLoader` selects a backend based on `frameSourceMode`.
7. The selected backend fills shared loader state with textures and, where available, pose/intrinsics/depth data.
8. The camera provider advances playback on Unity's main thread and publishes:
   - color textures via `XRCameraSubsystem`
   - intrinsics via `TryGetIntrinsics`
   - projection matrices derived from recorded per-frame intrinsics via AR Foundation camera APIs
   - camera timing and frame state via AR Foundation camera APIs
   - pose via the public `PoseBridge` API
9. `OcclusionSubsystem` optionally publishes depth textures in lock-step with camera playback through `XROcclusionSubsystem`.
10. `ARSensorFlexSession` consumes `PoseBridge` and `ScannedSceneMeshBridge` to drive the replay camera rig and instantiate the packaged scanned mesh under `XROrigin`.
11. Host applications primarily consume data through AR Foundation managers and the `ARSensorFlexSession` component; `PoseBridge` remains a narrower direct API for advanced use.

## ASCII Diagram

```text
+--------------------------------------------------------------------------+
| Internal Runtime                                                         |
|                                                                          |
|  +----------------------+                                                |
|  | SensorFlexSettings   |                                                |
|  | source/fps/alignment |                                                |
|  +----------+-----------+                                                |
|             |                                                            |
|             v                                                            |
|  +-------------------+        +------------------+                       |
|  | Unity XR Manager  | -----> | Loader           |                       |
|  | / AR Foundation   |        | XRLoaderHelper   |                       |
|  +---------+---------+        +---+----------+---+                       |
|            |                      |          |                           |
|            v                      v          v                           |
|   +--------+---------+   +--------+----+  +---+----------------+         |
|   | CameraSubsystem  |   | Session      |  | OcclusionSubsystem |        |
|   | playback owner   |   | Subsystem    |  | depth provider     |        |
|   +----+--------+----+   +-------------+  +---+----------------+         |
|        |        |                               ^                        |
|        |        | PoseBridge.SetUnityPose()     | OnFramesReady          |
|        |        v                               |                        |
|        |   +----+-----------+                   |                        |
|        |   | PoseBridge     |                   |                        |
|        |   | public pose API|                   |                        |
|        |   +----------------+                   |                        |
|        |                                        |                        |
|        |   +--------------------+               |                        |
|        +-> | ScannedSceneMesh   |               |                        |
|            | Bridge             |               |                        |
|            +--------------------+               |                        |
|                                                                          |
|   +----+--------------------------------------+                          |
|   | FrameLoader facade                        |                          |
|   | exposes Frames / Poses / Intrinsics /     |                          |
|   | Depth to runtime subsystems               |                          |
|   +-------------------+-----------------------+                          |
|                       |                                                  |
|                       v                                                  |
|          +------------+------------------------------+                   |
|          | shared loader state + backend interface   |                   |
|          +------------+------------------------------+                   |
|                       |                                                  |
|       +---------------+-------------------------------+                  |
|       |               |                               |                  |
|       v               v                               v                  |
|   +---+----------+ +--+----------------+ +------------+-----------+      |
|   | FileSystem   | | WebSocket         | | ZIP                    |      |
|   | preload imgs | | network preload   | | threaded stream/ring   |      |
|   +---+----------+ +--+----------------+ +------------+-----------+      |
|       |               |                               |                  |
|       +---------------+---------------+---------------+                  |
|                                       |                                  |
|                                       v                                  |
|                          +------------+------------+                     |
|                          | ArchiveIOUtils          |                     |
|                          | zip/json/pose/proj      |                     |
|                          +-------------------------+                     |
|                                                                          |
+--------------------------------------------------------------------------+

  Primary host integration                        Secondary shortcut API

  +----------------------------------+        +----------------------+
  | AR Foundation managers/components|        | Host app / game code |
  | ARCameraManager, background,     |        | reading PoseBridge   |
  | occlusion, XR consumers          |        +----------+-----------+
  +----------------+-----------------+                   ^
                   ^                                     |
                   |                                     |
     +-------------+--------------+                      |
     |                            |                      |
     | camera color / intrinsics  | latest Unity pose   |
     | projection / session       | via PoseBridge      |
     | environment depth          |                      |
     |                            |                      |
  +--+---------------+     +------+-------+              |
  | CameraSubsystem  |     | PoseBridge   |--------------+
  +--------+---------+     | public API   |
           |               +--------------+
           |
           v
  +--------+----------------------+
  | ARSensorFlexSession           |
  | public host integration       |
  | - drives replay camera rig    |
  | - instantiates scanned mesh   |
  | - applies clip-plane override |
  | - applies XROrigin alignment  |
  +--------+----------------------+
           ^
           |
  +--------+----------------------+
  | ScannedSceneMeshBridge        |
  +-------------------------------+

  `CameraSubsystem`, `OcclusionSubsystem`, and `SessionSubsystem`
  publish into the AR Foundation-facing path, while `ARSensorFlexSession`
  is the main package component for host-scene setup.
```

## Host Application Integration

There are two outward-facing publication paths:

- Primary path: AR Foundation
  The package publishes camera color, intrinsics, projection, session state, and optional environment depth through XR subsystem APIs. Host applications should generally consume this data through standard AR Foundation managers and rendering components.

- Primary setup component: `ARSensorFlexSession`
  For most host applications, the intended integration point is attaching `ARSensorFlexSession` to `XROrigin`. That component drives replay camera motion, instantiates the packaged scanned mesh, and applies package-level alignment and clip-plane behavior so app code does not need project-local helper scripts.

- Secondary path: `PoseBridge`
  `PoseBridge` is a direct static API for the latest Unity-space pose. It exists as a convenience escape hatch, but it is narrower and more coupled than the AR Foundation path.

## Backend Responsibilities

### FileSystem

- Resolves `imageFolder`
- Loads and decodes all color images eagerly
- Marks the loader ready once preload completes

### WebSocket

- Connects to `webSocketUrl`
- Requests a bounded set of frames
- Decodes incoming image payloads into textures
- Uses `DispatchWebSocket()` from the camera update loop

### ZIP

- Opens the archive and reads scene-level `meta.json`
- Builds the coordinate conversion matrix from archive metadata
- Streams frames on a background thread into a ring buffer
- Publishes RGB, raw depth bytes, pose matrices, and intrinsics
- Loads the packaged scanned mesh and publishes it through `ScannedSceneMeshBridge`
- Uses `DrainUploadQueue()` on the main thread for texture upload

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- ZIP archive frame reads happen on a dedicated background thread.
- ZIP texture creation and upload still happen on the main thread.
- WebSocket message dispatch is driven from the main thread through the camera subsystem.
- ZIP mesh file reads and PLY parsing happen off the main thread before the mesh is built on the Unity side.

## Current Design Constraints

- `OcclusionSubsystem` currently supports file-system depth textures only.
- ZIP depth is already loaded into `FrameLoader.DepthBins`, but not yet surfaced through `XROcclusionSubsystem`.
- `PoseBridge` is a public API surface, but its backing state is package-global and not session-isolated.
- `ARSensorFlexSession` is the intended public integration component, but some lower-level public surfaces still exist for advanced use and backwards compatibility.
- `FrameLoader` is the integration seam between framework code and IO backends. New sources should be added as new `IFrameLoaderBackend` implementations rather than expanding the facade.
