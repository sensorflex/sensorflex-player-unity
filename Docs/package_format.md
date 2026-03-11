# ScanNet++ iPhone ZIP Format

Self-contained `.zip` archive encoding RGB, depth, pose, intrinsics, IMU,
and the aligned scanned mesh from a single ScanNet++ iPhone scene capture.

Source data: ScanNet++ dataset (iPhone capture via ARKit on iPhone 13 Pro).
Converter: `main.py` in this repo.

---

## Archive Structure

```
<scene_id>/
├── meta.json
├── scanned_mesh/
│   └── mesh_aligned_0.05.ply
└── frames/
    ├── 000000/
    │   ├── rgb.jpg
    │   ├── depth.bin
    │   ├── mask.png
    │   └── meta.json
    ├── 000001/
    │   └── ...
    └── <N-1>/
        └── ...
```

- `<scene_id>` is the ScanNet++ scene identifier (e.g. `02455b3d20`).
- Frame folders are zero-padded 6-digit integers: `000000` through `N-1`.
- Frame index is the authoritative key — all four files inside a folder
  describe the same instant in time.
- `scanned_mesh/mesh_aligned_0.05.ply` is optional. When present, it is in the
  exact same world coordinate frame as each frame's `pose`.
- `rgb.jpg` and `mask.png` use `ZIP_STORED` (already compressed internally).
- `depth.bin`, `meta.json`, and `mesh_aligned_0.05.ply` use `ZIP_DEFLATED`.

---

## scene meta.json

Located at `<scene_id>/meta.json`. Describes the entire scene and the
conventions used throughout the archive. All fields are machine-readable.

```json
{
  "format_version": "1.1",
  "scene_id": "02455b3d20",
  "n_frames": 10632,
  "fps": 60,

  "source": {
    "dataset": "ScanNet++",
    "device": "iPhone 13 Pro",
    "capture_framework": "ARKit"
  },

  "coordinate_system": {
    "handedness": "right",
    "up": "+Y",
    "forward": "-Z",
    "units": "meters"
  },

  "pose": {
    "convention": "camera_to_world",
    "scale": "metric",
    "layout": "row_major_4x4",
    "note": "aligned to packaged scanned mesh coordinate space when scanned_mesh is present"
  },
  "pose_raw": {
    "convention": "camera_to_world",
    "scale": "arbitrary",
    "layout": "row_major_4x4",
    "note": "ARKit original pose, arbitrary origin and scale"
  },

  "scanned_mesh": {
    "path": "scanned_mesh/mesh_aligned_0.05.ply",
    "format": "ply",
    "units": "meters",
    "coordinate_frame": "pose",
    "note": "offline scanned mesh vertices are in the same world coordinate frame as frame meta.json pose"
  },

  "camera": {
    "model": "perspective",
    "distortion_model": "none",
    "distortion_coefficients": [],
    "intrinsic_variation": "per_frame",
    "intrinsic_layout": "row_major_3x3"
  },

  "rgb": {
    "width": 1920,
    "height": 1440,
    "format": "jpeg",
    "anonymization": "magenta_pixels"
  },

  "depth": {
    "width": 256,
    "height": 192,
    "format": "raw_float32_le",
    "layout": "row_major",
    "units": "meters",
    "sensor": "lidar",
    "range_min": 0.1,
    "range_max": 5.0,
    "invalid_value": 0.0
  },

  "mask": {
    "width": 1920,
    "height": 1440,
    "format": "png",
    "description": "non-zero pixels indicate anonymized regions"
  },

  "imu": {
    "frame": "device_body",
    "fields": {
      "rotate_rate": {
        "units": "rad/s",
        "shape": [3],
        "axes": ["x", "y", "z"]
      },
      "acceleration": {
        "units": "m/s2",
        "shape": [3],
        "axes": ["x", "y", "z"],
        "note": "gravity removed"
      },
      "magnet": { "units": "uT", "shape": [3], "axes": ["x", "y", "z"] },
      "attitude": {
        "units": "rad",
        "shape": [3],
        "axes": ["roll", "pitch", "yaw"]
      },
      "gravity": {
        "units": "unit_vector",
        "shape": [3],
        "axes": ["x", "y", "z"]
      }
    }
  }
}
```

### Key fields for decoder implementation

**`coordinate_system.handedness`** — `"right"` or `"left"`. Use this to
decide whether to flip any axis when importing into an engine with a
different convention (e.g. Unity uses left-handed).

**`coordinate_system.up`** — which world axis is up. ARKit uses `+Y`.
Unity also uses `+Y`. Unreal uses `+Z`.

**`coordinate_system.forward`** — which camera-space axis points forward
into the scene. ARKit uses `-Z` (OpenGL convention). Unity uses `+Z`.

**`pose.scale`** — `"metric"` means translation values are in real-world meters,
aligned to the packaged laser-scan mesh when `scanned_mesh` is present. `pose_raw.scale`
is `"arbitrary"` — ARKit does not guarantee real-world units there.

**`pose.convention`** — `"camera_to_world"` means the matrix transforms a
point from camera space into world space. `"world_to_camera"` is the
inverse (view matrix).

**`scanned_mesh.coordinate_frame`** — `"pose"` means scanned mesh vertices
and the exported `pose` matrices share the same world frame, so no extra
alignment transform should be applied before rendering cameras against the
scanned mesh.

**`camera.intrinsic_variation`** — `"per_frame"` means the K matrix in
each frame's `meta.json` must be used for that frame; a single static K
is not valid for the whole scene.

---

## rgb.jpg

- **Format:** JPEG
- **Resolution:** 1920 × 1440 px
- **Color space:** sRGB
- Pixels in anonymized regions are painted **magenta (255, 0, 255)**.
  The corresponding `mask.png` identifies those pixels exactly.

---

## depth.bin

- **Format:** Raw binary, no header
- **Dtype:** IEEE 754 single-precision float, **little-endian**
- **Layout:** Row-major, `depth[row][col]`, top-left origin
- **Size:** `256 × 192 × 4 = 196,608 bytes` (fixed per frame)
- **Units:** Meters
- **Invalid pixels:** Value `0.0` — no LiDAR return

**Depth intrinsics:** Not stored separately. Approximate by scaling the
RGB K matrix from `meta.json`:

```
K_depth.fx = K_rgb.fx * (256 / 1920)
K_depth.fy = K_rgb.fy * (192 / 1440)
K_depth.cx = K_rgb.cx * (256 / 1920)
K_depth.cy = K_rgb.cy * (192 / 1440)
```

---

## mask.png

- **Format:** PNG (lossless)
- **Resolution:** 1920 × 1440 px (matches `rgb.jpg`)
- **Content:** Non-zero pixels mark anonymized regions that are painted
  magenta in `rgb.jpg`.

---

## scanned_mesh/mesh_aligned_0.05.ply

- **Format:** PLY mesh
- **Units:** Meters
- **Coordinate frame:** Same world frame as per-frame `pose`
- **Type:** Offline scanned mesh
- **Source:** `scans/mesh_aligned_0.05.ply` from the ScanNet++ scene

If this file is present, frame `pose` is guaranteed to be the aligned,
metric camera-to-world transform for this scanned mesh. `pose_raw` remains
the original ARKit trajectory and should not be mixed with the scanned mesh
without an external alignment step.

This naming distinguishes the static ScanNet++ reconstruction from any
future AR-framework mesh stream. A future real-time mesh sequence should be
stored under a separate top-level key and path rather than overloading
`scanned_mesh`.

---

## frame meta.json

Per-frame metadata. Located at `<scene_id>/frames/<NNNNNN>/meta.json`.

```json
{
  "timestamp": 178454.292513458,
  "pose": [
    [-0.8736, -0.1263, 0.47, 1.6947],
    [-0.4867, 0.2184, -0.8458, 4.4221],
    [0.0042, -0.9677, -0.2522, 1.6195],
    [0.0, 0.0, 0.0, 1.0]
  ],
  "pose_raw": [
    [-0.9135, -0.1117, 0.3912, 16.1122],
    [0.0091, -0.9669, -0.255, 1.359],
    [0.4067, -0.2294, 0.8843, -8.5341],
    [0.0, 0.0, 0.0, 1.0]
  ],
  "intrinsic": [
    [1425.3125, 0.0, 954.9786],
    [0.0, 1425.3125, 725.3613],
    [0.0, 0.0, 1.0]
  ],
  "imu": {
    "rotate_rate": [-0.0397, 0.0259, -0.0499],
    "acceleration": [0.0048, -0.0111, -0.0008],
    "magnet": [0.0, 0.0, 0.0],
    "attitude": [-1.3078, -0.0089, -0.0081],
    "gravity": [-0.9656, 0.0089, -0.2599]
  }
}
```

### Fields

| Field       | Type              | Description                                                               |
| ----------- | ----------------- | ------------------------------------------------------------------------- |
| `timestamp` | float64           | ARKit capture timestamp, seconds since device boot                        |
| `pose`      | float32\[4\]\[4\] | Camera-to-world, **metric**, aligned to the scanned mesh coordinate space |
| `pose_raw`  | float32\[4\]\[4\] | Camera-to-world, ARKit original, arbitrary origin and scale               |
| `intrinsic` | float32\[3\]\[3\] | RGB camera K matrix at this frame's focal length                          |
| `imu`       | object            | IMU measurement (see `imu.fields` in scene `meta.json`)                   |

`pose` is in the same coordinate frame as `scanned_mesh/mesh_aligned_0.05.ply` when
that file is present in the archive.
`pose_raw` is useful when only relative camera motion matters (e.g. NeRF, SLAM)
and you don't need alignment to the scanned mesh.

---

## Decoding (C# / Unity)

```csharp
// depth.bin
float[,] DecodeDepth(byte[] bytes) {
    var depth = new float[192, 256];
    // float32 little-endian matches x86/ARM Unity native byte order
    Buffer.BlockCopy(bytes, 0, depth, 0, bytes.Length);
    return depth;
}

// meta.json — use Newtonsoft.Json or System.Text.Json
var meta = JsonConvert.DeserializeObject<FrameMeta>(jsonString);

// Handedness conversion: ARKit right-handed (+Y up, -Z forward)
//                    ->  Unity left-handed  (+Y up, +Z forward)
Matrix4x4 ConvertPose(float[][] pose) {
    // Flip Z column and Z row to convert right-handed to left-handed
    var m = ToMatrix4x4(pose);
    m.m02 = -m.m02; m.m12 = -m.m12; m.m22 = -m.m22; m.m32 = -m.m32;
    m.m20 = -m.m20; m.m21 = -m.m21; m.m22 =  m.m22; m.m23 = -m.m23;
    return m;
}
```

---

## Source Dataset

ScanNet++ — [https://github.com/scannetpp/scannetpp](https://github.com/scannetpp/scannetpp)

ICCV 2023. iPhone data captured with ARKit on iPhone 13 Pro (LiDAR + RGB).
