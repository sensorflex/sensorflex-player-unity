# SensorFlex Unity Player

A Unity 6 XR plug-in that replays SensorFlex capture data through AR Foundation. Feed recorded camera frames, poses, intrinsics, depth, and a scanned scene mesh into any AR Foundation-based project without modifying application code.

---

## Requirements

| Requirement | Version |
|---|---|
| Unity | 6000.0 or later |
| AR Foundation | 6.x |
| Universal Render Pipeline | 17.x |
| NativeWebSocket | any (GitHub UPM) |

---

## Installation

Add the package to your project's `Packages/manifest.json` as a local or Git dependency:

```json
{
  "dependencies": {
    "com.sensorflex.player.unity": "file:../Packages/com.sensorflex.player.unity",
    "com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm"
  }
}
```

Or clone as a git submodule under `Packages/`:

```bash
git submodule add <repo-url> Packages/com.sensorflex.player.unity
git submodule update --init --recursive
```

---

## Project Setup

1. **Enable the XR loader**
   Open **Edit → Project Settings → XR Plug-in Management**, select your target platform, and enable **SensorFlex Player** in the loader list.

2. **Add scene components**
   In your scene, on the `XROrigin` GameObject (or a sibling):
   - Add **AR SensorFlex Session** (`XR/SensorFlex/AR SensorFlex Session`)
   - For scanned mesh display, add **AR SensorFlex Scene Mesh** (`XR/SensorFlex/AR SensorFlex Scene Mesh`) to any child object

3. **Configure the frame source** (see [Frame Sources](#frame-sources) below)

4. **Add AR Foundation managers** as usual — `ARCameraManager`, `ARCameraBackground`, `AROcclusionManager` — the package feeds them automatically through XR subsystem APIs.

---

## Frame Sources

Select the source on the **AR SensorFlex Session** component (`Frame Source Mode` field).

| Mode | Description | Setup |
|---|---|---|
| **FileSystem** | Loads PNG/JPG sequences from disk at startup | Place images in `Assets/StreamingAssets/<Image Folder>/` |
| **Sfz** | Streams frames from a `.sfz` archive; also loads the bundled scanned mesh | Set `Sfz File Path` to a `.sfz` relative to `StreamingAssets/` or an absolute path |
| **FileIo** | Same as Sfz but reads from an unzipped session directory | Set `File Io Path` to a directory containing `session.json` |
| **WebSocket** | Receives frames from a live server over WebSocket | Set `Web Socket Url` (default `ws://localhost:3000`) and run a compatible server |

### FileSystem quick start

```
Assets/
└── StreamingAssets/
    └── DiskCam/          ← default Image Folder
        ├── 000001.jpg
        ├── 000002.jpg
        └── ...
```

### SFZ quick start

Obtain or convert a dataset to the SensorFlex Session Format (see [`Docs/SensorFlexFormat.md`](Docs/SensorFlexFormat.md)), then set `Sfz File Path` to the `.sfz` archive path. The package reads frames, poses, intrinsics, depth, and the optional scanned mesh from a single file.

For an unzipped session directory use **FileIo** mode and set `File Io Path` to the folder containing `session.json`.

### WebSocket quick start

The server must accept a JSON `hello` message and respond with a `scene` message followed by binary frame packets. Set `Web Socket Url` and press Play.

---

## Scene Components

### AR SensorFlex Session

**Menu path:** `XR/SensorFlex/AR SensorFlex Session`

Primary integration component. Attach once to `XROrigin` or any scene object.

| Inspector Section | Key Fields |
|---|---|
| Frame Source | Source mode, SFZ path, FileIo path, image folder, WebSocket URL |
| Playback | Preload frame count, loop, target FPS |
| Depth (Occlusion) | Enable depth, depth folder (FileSystem only) |
| Session Alignment | Optional position/rotation/scale offset applied to `XROrigin` at startup |
| Replay Camera | Enable camera driving, optional transform override, position scale and offset |

### AR SensorFlex Scene Mesh

**Menu path:** `XR/SensorFlex/AR SensorFlex Scene Mesh`

Instantiates the scanned mesh from a SFZ archive or FileIo directory when `attachments.scene_mesh` is present in `session.json`. Attach to any child object under `XROrigin`.

| Field | Description |
|---|---|
| Material | Optional material override (defaults to `SensorFlex/VertexColor` shader) |
| Add Mesh Collider | Adds a `MeshCollider` to the instantiated mesh object |

Only active for **Sfz** and **FileIo** frame sources; does nothing for FileSystem or WebSocket.

---

## Output Paths

The package publishes data through two parallel paths:

**AR Foundation (Path 1)** — consumed via standard managers:
- Color texture → `ARCameraManager` / `ARCameraBackground`
- Per-frame intrinsics and projection matrix → `ARCameraManager`
- Environment depth → `AROcclusionManager` (FileSystem, Sfz, and FileIo modes)
- Session tracking state → `ARSession`

**Bridges (Path 2)** — consumed via package scene components:
- Camera pose → `PoseBridge` → `ARSensorFlexSession` drives `XROrigin` camera rig
- Scanned mesh → `ScannedSceneMeshBridge` → `ARSensorFlexSceneMesh` renders mesh in scene

Both bridges (`PoseBridge`, `ScannedSceneMeshBridge`) are public static APIs and can also be subscribed to directly by host application code.

---

## Known Limitations

- `PoseBridge` and `ScannedSceneMeshBridge` are global singletons; they are not isolated per-session.
- The WebSocket server protocol is custom — a compatible server is required.

---

## Further Reading

- [`Docs/Architecture.md`](Docs/Architecture.md) — internal component map, data flow, threading model
- [`Docs/SensorFlexFormat.md`](Docs/SensorFlexFormat.md) — SFZ session format reference (tracks, attachments, coordinate system)
