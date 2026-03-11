# SensorFlex Player Architecture

This package implements a lightweight XR provider stack for replaying SensorFlex capture data inside Unity through AR Foundation interfaces.

## Goals

- Present recorded color, pose, and optional depth data as XR subsystems.
- Support multiple frame sources behind one runtime-facing loader contract.
- Keep Unity-facing subsystem code separate from backend-specific IO code.

## Main Components

- `Runtime/Loader.cs`
  Registers and starts the custom XR subsystems through Unity XR Management.

- `Runtime/Settings.cs`
  Defines `SensorFlexSettings`, the runtime configuration entry point. It selects the frame source mode, preload count, FPS, loop behavior, and optional depth settings.

- `Runtime/Subsystem/Camera.cs`
  Owns playback timing and exposes camera frames to AR Foundation. It creates `FrameLoader`, advances playback, updates intrinsics, and pushes poses into `PoseBridge`.

- `Runtime/Subsystem/Depth.cs`
  Exposes environment depth through `XROcclusionSubsystem`. Today this is implemented for file-system depth images and synchronized to camera frame progression via `OnFramesReady`.

- `Runtime/Subsystem/Session.cs`
  Provides a minimal `XRSessionSubsystem` implementation so the package can participate in the XR lifecycle.

- `Runtime/PoseBridge.cs`
  Acts as a simple static pose handoff for host app code that wants the current camera pose without going through AR Foundation pose APIs.

- `Runtime/Library/FrameLoading.cs`
  Contains the frame-loading orchestration and backend abstraction:
  - `FrameLoader`: framework-facing facade used by the camera subsystem
  - `IFrameLoaderState`: shared data contract between the facade and backends
  - `IFrameLoaderBackend`: backend contract
  - `FileSystemFrameLoaderBackend`
  - `WebSocketFrameLoaderBackend`
  - `ZipFrameLoaderBackend`

- `Runtime/Library/ArchiveIOUtils.cs`
  Stateless helpers for archive reads, JSON float extraction, matrix construction, and coordinate-system conversion.

## High-Level Data Flow

1. Unity XR Management initializes `Loader`.
2. `Loader` creates and starts the custom camera, session, and occlusion subsystems.
3. `CameraSubsystem.CameraDataProvider` loads `SensorFlexSettings` and creates `FrameLoader`.
4. `FrameLoader` selects a backend based on `frameSourceMode`.
5. The selected backend fills shared loader state with textures and, where available, pose/intrinsics/depth data.
6. The camera provider advances playback on Unity's main thread and publishes:
   - color textures via `XRCameraSubsystem`
   - intrinsics via `TryGetIntrinsics`
   - pose via `PoseBridge`
7. `OcclusionSubsystem` optionally publishes depth textures in lock-step with camera playback.

## ASCII Diagram

```text
                           +----------------------+
                           | SensorFlexSettings   |
                           | source/fps/buffering |
                           +----------+-----------+
                                      |
                                      v
+-------------------+        +--------+---------+
| Unity XR Manager  | -----> | Loader            |
| / AR Foundation   |        | XRLoaderHelper    |
+---------+---------+        +---+-----------+---+
          |                      |           |
          |                      |           |
          v                      v           v
 +--------+---------+   +--------+----+  +---+----------------+
 | CameraSubsystem  |   | Session      |  | OcclusionSubsystem |
 | playback owner   |   | Subsystem    |  | depth provider     |
 +--------+---------+   +-------------+  +---+----------------+
          |                                   ^
          | OnFramesReady                     |
          v                                   |
 +--------+-----------------------------------+---+
 | FrameLoader facade                             |
 | exposes Frames / Poses / Intrinsics / Depth    |
 +-------------------+----------------------------+
                     |
                     v
        +------------+------------------------------+
        | shared loader state + backend interface   |
        +------------+------------------------------+
                     |
     +---------------+-------------------------------+
     |               |                               |
     v               v                               v
 +---+----------+ +--+----------------+ +------------+-----------+
 | FileSystem   | | WebSocket         | | ZIP                    |
 | preload imgs | | network preload   | | threaded stream/ring   |
 +---+----------+ +--+----------------+ +------------+-----------+
     |               |                               |
     +---------------+---------------+---------------+
                                     |
                                     v
                        +------------+------------+
                        | ArchiveIOUtils          |
                        | zip/json/matrix helpers |
                        +------------+------------+
                                     |
                                     v
                              +------+------+
                              | PoseBridge  |
                              | latest pose |
                              +-------------+
```

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
- Uses `DrainUploadQueue()` on the main thread for texture upload

## Runtime Threading Model

- Unity-facing subsystem methods run on the main thread.
- ZIP archive frame reads happen on a dedicated background thread.
- ZIP texture creation and upload still happen on the main thread.
- WebSocket message dispatch is driven from the main thread through the camera subsystem.

## Current Design Constraints

- `OcclusionSubsystem` currently supports file-system depth textures only.
- ZIP depth is already loaded into `FrameLoader.DepthBins`, but not yet surfaced through `XROcclusionSubsystem`.
- `PoseBridge` is package-global state; it is simple, but not session-isolated.
- `FrameLoader` is the integration seam between framework code and IO backends. New sources should be added as new `IFrameLoaderBackend` implementations rather than expanding the facade.
