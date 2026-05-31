# SensorFlex Session Format

Version 1.0. Self-contained `.sfz` archive (ZIP-compatible) encoding an AR session for
recording and replay via ARFoundation.

**Coordinate system invariant:** all poses and vectors are in Unity world space —
left-handed, +Y up, +Z forward, meters. This is a format-level constant; it is not
stored per-session.

---

## Archive Structure

### Single-file

```
session/
├── session.json          ← all metadata and track data arrays
├── rgb/
│   ├── 000000.jpg
│   └── ...
├── depth/                (present only when depth channel exists)
│   ├── 000000.bin
│   └── ...
└── scene_mesh.ply        (present only when attachments.scene_mesh exists)
```

### Multi-part

When an archive exceeds a target size it is split into numbered part files:

```
<scene_id>-00000-of-00003.sfz   session.json + first content chunk(s)
<scene_id>-00001-of-00003.sfz   next content chunk(s)
<scene_id>-00002-of-00003.sfz   last content chunk(s)
```

- The naming suffix is `-DDDDD-of-DDDDD` (zero-padded, five digits each).
- A reader that receives **any** part can derive all part filenames from the suffix alone.
- `session.json` is always in part 0. All other parts contain only binary assets.
- Content is packed greedily in this order: `session.json` → attachment data → frame data.
  If the first part has remaining capacity after `session.json`, attachment chunks and then
  frame assets are added until the size limit is reached before a new part is started.
- A `parts` manifest in `session.json` describes every part's contents (see below).
- Readers must verify all parts are present before beginning to load.

**Compression:**
- `.jpg` files: `ZIP_STORED` (already compressed)
- everything else: `ZIP_DEFLATED`

File paths inside `session.json` are relative to `session/`.

---

## session.json

Single file containing all session metadata and track data arrays. Always lives in part 0
of a multi-part archive.

```json
{
  "version": "1.0",
  "session_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "start_time_utc": "2026-05-25T10:30:00.000Z",
  "device": {
    "model": "iPhone 13 Pro",
    "os": "iOS 16.0",
    "ar_framework": "ARKit"
  },
  "attachments": {
    "scene_mesh": { "file": "scene_mesh.ply", "format": "ply" }
  },
  "parts": [
    {
      "file": "<scene_id>-00000-of-00002.sfz",
      "contents": [
        { "type": "session" },
        { "type": "attachment", "attachment_key": "scene_mesh",
          "chunk_index": 0, "total_chunks": 2 },
        { "type": "frames", "frame_range": [0, 499] }
      ]
    },
    {
      "file": "<scene_id>-00001-of-00002.sfz",
      "contents": [
        { "type": "attachment", "attachment_key": "scene_mesh",
          "chunk_index": 1, "total_chunks": 2 },
        { "type": "frames", "frame_range": [499, 1250] }
      ]
    }
  ],
  "tracks": {
    "frames": {
      "metadata": {
        "fps": 60,
        "channels": {
          "rgb":   { "width": 1920, "height": 1440, "format": "jpeg" },
          "depth": { "width": 256, "height": 192, "format": "raw_float32_le",
                     "units": "meters", "sensor": "lidar", "invalid_value": 0.0 }
        }
      },
      "data": [
        {
          "timestamp_ns": 178454292513458,
          "camera": {
            "pose": {
              "position": [1.6947, 4.4221, -1.6195],
              "rotation": [0.12, 0.34, 0.56, 0.75]
            },
            "intrinsics": { "fx": 1425.3125, "fy": 1425.3125, "cx": 954.9786, "cy": 725.3613 }
          },
          "light_estimation": { "ambient_intensity": 1000.0, "color_temperature": 6500.0 },
          "rgb":   { "file": "rgb/000000.jpg" },
          "depth": { "file": "depth/000000.bin" }
        },
        {
          "timestamp_ns": 178454309180125,
          "camera": { "..." : "..." },
          "rgb": { "file": "rgb/000001.jpg" },
          "depth": { "file": "depth/000001.bin" }
        }
      ]
    },
    "imu": {
      "metadata": {
        "sample_rate_hz": 100
      },
      "data": [
        {
          "timestamp_ns": 178454275000000,
          "acceleration":  [0.01, -9.79,  0.03],
          "rotation_rate": [-0.04, 0.03, -0.05],
          "gravity":       [-0.97, 0.01, -0.26]
        },
        {
          "timestamp_ns": 178454285000000,
          "acceleration":  [0.02, -9.80,  0.01],
          "rotation_rate": [-0.03, 0.02, -0.04],
          "gravity":       [-0.97, 0.01, -0.26]
        }
      ]
    }
  }
}
```

---

## Track: frames

Camera-rate data aligned to the ARFoundation rendering loop.

### tracks.frames fields

| Field | Description |
|---|---|
| `metadata.fps` | Nominal capture rate |
| `metadata.channels` | Descriptor for each binary channel present (see below) |
| `data` | Ordered array of frame records, one per camera frame |

`metadata.channels` contains an entry for each optional per-frame binary channel. `rgb`
is always present. `depth` and any future binary channels are optional.
`light_estimation` is inline in each data record and does not appear in `channels`.

### Frame record fields

| Field | Type | ARFoundation source | Description |
|---|---|---|---|
| `timestamp_ns` | int64 | `ARCameraFrame.timestampNs` | Nanoseconds since device boot |
| `camera.pose.position` | float32[3] | `ARCamera.transform.position` | Camera position in Unity world space, meters |
| `camera.pose.rotation` | float32[4] | `ARCamera.transform.rotation` | Quaternion [x, y, z, w] in Unity world space |
| `camera.intrinsics.fx` / `fy` | float32 | `XRCameraIntrinsics.focalLength` | Focal lengths in pixels |
| `camera.intrinsics.cx` / `cy` | float32 | `XRCameraIntrinsics.principalPoint` | Principal point in pixels |
| `light_estimation.ambient_intensity` | float32 | `ARLightEstimationData.averageIntensityInLumens` | Lumens; omitted if unavailable |
| `light_estimation.color_temperature` | float32 | `ARLightEstimationData.averageColorTemperature` | Kelvin; omitted if unavailable |
| `rgb` | file ref | `XRCameraImage` | `{ "file": "rgb/<NNNNNN>.jpg" }` |
| `depth` | file ref | `XRCpuImage` (occlusion) | `{ "file": "depth/<NNNNNN>.bin" }`; absent if depth channel not present |

**`timestamp_ns`:** nanoseconds since device boot, matching `ARCameraFrame.timestampNs`.
Relative playback time: `(timestamp_ns[i] - timestamp_ns[0]) / 1e9` seconds.

### rgb files

- **Format:** JPEG, sRGB
- **Resolution:** `channels.rgb.width` × `channels.rgb.height`
- **Naming:** zero-padded 6-digit frame index — `rgb/000000.jpg`

### depth files

- **Format:** Raw binary, no header; IEEE 754 float32, little-endian, row-major
- **Size:** `depth_width × depth_height × 4` bytes (fixed)
- **Invalid:** `0.0` — no depth return
- **Naming:** zero-padded 6-digit frame index — `depth/000000.bin`

**Depth intrinsics** are not stored separately. Derive from `camera.intrinsics` in the
frame record and the depth resolution in `metadata.channels.depth`:

```
fx_d = camera.intrinsics.fx * (depth_width  / rgb_width)
fy_d = camera.intrinsics.fy * (depth_height / rgb_height)
cx_d = camera.intrinsics.cx * (depth_width  / rgb_width)
cy_d = camera.intrinsics.cy * (depth_height / rgb_height)
```

---

## Track: imu

Higher-frequency sensor data from the Unity Input System, independent of frame timing.
Typical rate: ~100 Hz. Present only when `"imu"` key exists in `tracks`.

`sample_rate_hz` is nominal — use per-sample `timestamp_ns` for actual timing.

### IMU sample fields

| Field | Type | Unity Input System source | Description |
|---|---|---|---|
| `timestamp_ns` | int64 | `InputDevice.lastUpdateTime` (converted) | Nanoseconds since device boot |
| `acceleration` | float32[3] | `Accelerometer.current.value` | Raw acceleration including gravity, m/s² |
| `rotation_rate` | float32[3] | `Gyroscope.current.value` | Angular velocity, rad/s |
| `gravity` | float32[3] | `GravitySensor.current.value` | Gravity direction, unit vector |

---

## Attachments

Static assets that accompany the session but are not time-series data. `attachments` is an
optional top-level object; omit it entirely when no static assets are present.

All file references in `attachments` are relative to `session/`, the same root as track file
references. Static asset files sit flat in the session root — no subdirectory is used for
single files.

### attachments.scene_mesh

An offline-reconstructed static mesh of the captured environment.

```json
"attachments": {
  "scene_mesh": {
    "file": "scene_mesh.ply",
    "format": "ply"
  }
}
```

| Field | Type | Description |
|---|---|---|
| `file` | string | Path relative to `session/`. Conventionally `scene_mesh.ply`. |
| `format` | string | Format hint. Currently only `"ply"` is supported. |

**Format:** PLY (ASCII or binary-little-endian). Standard vertex attributes: `x y z` (required);
`nx ny nz` normals and `red green blue` per-vertex colors (optional).

**Coordinate frame:** Unity world space — the same frame as `tracks.frames` poses.
No conversion is required when rendering the mesh against replayed camera data.

---

## Multi-Part Archives

### parts manifest

`session.json` includes a top-level `parts` array when the archive is split. It is omitted
entirely in single-file archives. Each element describes one part file and its contents.

```json
"parts": [
  {
    "file": "<scene_id>-00000-of-00003.sfz",
    "contents": [
      { "type": "session" },
      { "type": "attachment", "attachment_key": "scene_mesh",
        "chunk_index": 0, "total_chunks": 2 },
      { "type": "frames", "frame_range": [0, 499] }
    ]
  },
  {
    "file": "<scene_id>-00001-of-00003.sfz",
    "contents": [
      { "type": "attachment", "attachment_key": "scene_mesh",
        "chunk_index": 1, "total_chunks": 2 },
      { "type": "frames", "frame_range": [499, 999] }
    ]
  },
  {
    "file": "<scene_id>-00002-of-00003.sfz",
    "contents": [
      { "type": "frames", "frame_range": [999, 1250] }
    ]
  }
]
```

### Content item fields

| `type` | Additional fields | Description |
|---|---|---|
| `"session"` | — | This part file contains `session/session.json` |
| `"frames"` | `frame_range: [start, end)` | Frame assets `rgb/NNNNNN.jpg` and `depth/NNNNNN.bin` for indices `[start, end)` |
| `"attachment"` | `attachment_key`, `chunk_index`, `total_chunks` | Raw byte slice of the named attachment file; concatenate all chunks in `chunk_index` order to recover the original file |

`frame_range` end is **exclusive** (Python-slice convention).

### Reader algorithm

1. Detect the `-DDDDD-of-DDDDD.sfz` suffix in the given filename.
2. Derive all `total` part paths; verify every file exists before proceeding.
3. Open part 0, read `session.json`.
4. If `parts` is present, build two indices from it:
   - **Frame index → part**: for each `"frames"` item, map `[start, end)` to a part file.
   - **Attachment chunks → part**: for each `"attachment"` item, record `(chunk_index, part_file)`.
5. To read `rgb/000500.jpg` or `depth/000500.bin`: parse the frame index (500), look up
   the interval, open the correct part.
6. To read an attachment: collect all chunks sorted by `chunk_index`, read each chunk's
   `session/<attachment.file>` entry from its part, concatenate the raw bytes.

### Size estimation (writer side)

The reference converter (`scannetpp_sfz.py`) estimates per-frame size as:

```
frame_size ≈ on_disk_rgb_bytes + DEPTH_H × DEPTH_W × 4   (uncompressed depth upper bound)
```

This is conservative — actual part files are typically smaller than the configured limit
because depth compresses well under `ZIP_DEFLATED`.

---

## Coordinate System

All poses and vectors are in **Unity world space: left-handed, +Y up, +Z forward,
meters.** This is a format-level invariant — not stored in any metadata file.

A decoder reads positions and quaternions directly into `Transform.position` and
`Transform.rotation` without conversion.

**Converting from ARKit (right-handed, +Y up, −Z forward):**

ARKit provides camera-to-world matrices (row-major 4×4). Flip the Z axis by negating
the Z row and Z column (the diagonal element m22 double-negates to unchanged):

```csharp
Matrix4x4 ConvertPose(float[][] m) {
    var r = ToMatrix4x4(m);
    r.m02 = -r.m02; r.m12 = -r.m12; r.m22 = -r.m22; r.m32 = -r.m32; // Z column
    r.m20 = -r.m20; r.m21 = -r.m21; r.m23 = -r.m23;                  // Z row
    return r;
}

var pos = new Vector3(converted.m03, converted.m13, converted.m23);
var rot = converted.rotation;
```

---

## Decoding (C# / Unity)

```csharp
// depth file
float[,] DecodeDepth(byte[] bytes, int w, int h) {
    var depth = new float[h, w];
    Buffer.BlockCopy(bytes, 0, depth, 0, bytes.Length);
    return depth;
}

// frame record
var frame = JsonConvert.DeserializeObject<FrameRecord>(frameJson);

transform.SetPositionAndRotation(
    new Vector3(frame.camera.pose.position[0], frame.camera.pose.position[1], frame.camera.pose.position[2]),
    new Quaternion(frame.camera.pose.rotation[0], frame.camera.pose.rotation[1],
                   frame.camera.pose.rotation[2], frame.camera.pose.rotation[3])
);
```

---

## External Dataset Converters

Converters from external datasets produce SensorFlex archives by:

1. Transforming poses into Unity left-handed space.
2. Writing `session.json` with correct device metadata and all track data.
3. Including only tracks and channels the source data contains.

| Dataset | Converter |
|---|---|
| ScanNet++ (iPhone / ARKit) | `data_processing/scannetpp_sfz.py` |
