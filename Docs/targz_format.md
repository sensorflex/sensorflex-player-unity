# ScanNet++ iPhone tar.gz Format

Self-contained `.tar.gz` archive encoding RGB, depth, pose, intrinsics, and
IMU data from a single ScanNet++ iPhone scene capture.

Source data: ScanNet++ dataset (iPhone capture via ARKit on iPhone 13 Pro).
Converter: `main.py` in this repo.

---

## Archive Structure

```
<scene_id>/
├── meta.json
└── frames/
    ├── 000000/
    │   ├── rgb.jpg
    │   ├── depth.bin
    │   ├── mask.png
    │   └── meta.bin
    ├── 000001/
    │   └── ...
    └── <N-1>/
        └── ...
```

- `<scene_id>` is the ScanNet++ scene identifier (e.g. `02455b3d20`).
- Frame folders are zero-padded 6-digit integers: `000000` through `N-1`.
- Frame index is the authoritative key — all four files inside a folder
  describe the same instant in time.
- The archive uses gzip compression (`w:gz`). Individual files inside are
  not additionally compressed.

---

## meta.json

Scene-level metadata. Always located at `<scene_id>/meta.json`.

```json
{
  "scene_id": "02455b3d20",
  "n_frames": 10632,
  "fps": 60,
  "rgb_width": 1920,
  "rgb_height": 1440,
  "depth_width": 256,
  "depth_height": 192,
  "depth_dtype": "float32",
  "depth_units": "meters",
  "depth_invalid_value": 0.0,
  "meta_bin_layout": "pose[16xf32_le] intrinsic[9xf32_le] imu[15xf32_le]",
  "meta_bin_bytes_per_frame": 160,
  "imu_layout": "rotate_rate[3] acceleration[3] magnet[3] attitude[3] gravity[3]",
  "coordinate_system": "ARKit right-handed: +X right, +Y up, +Z toward viewer",
  "pose_description": "camera-to-world, ARKit raw (unitless scale)",
  "intrinsic_description": "RGB camera K matrix, per-frame (ARKit adjusts focal length)"
}
```

---

## rgb.jpg

- **Format:** JPEG
- **Resolution:** 1920 × 1440 px
- **Color space:** sRGB
- **Source lens:** iPhone 13 Pro back camera, 5.7mm f/1.5 (27mm equiv.)
- Pixels that were anonymized are painted **magenta (255, 0, 255)**.
  The corresponding `mask.png` identifies those pixels.

---

## depth.bin

- **Format:** Raw binary, no header
- **Dtype:** IEEE 754 single-precision float, **little-endian**
- **Layout:** Row-major, `depth[row][col]`, top-left origin
- **Size:** `256 × 192 × 4 = 196,608 bytes` (fixed)
- **Units:** Meters
- **Invalid pixels:** Value `0.0` means no LiDAR return

**Source sensor:** iPhone LiDAR (ARKit `ARDepthData`).

**Depth intrinsics:** No separate intrinsic matrix is stored for depth.
Derive it by scaling the RGB intrinsic from `meta.bin`:

```
K_depth[fx] = K_rgb[fx] * (256 / 1920)
K_depth[fy] = K_rgb[fy] * (192 / 1440)
K_depth[cx] = K_rgb[cx] * (256 / 1920)
K_depth[cy] = K_rgb[cy] * (192 / 1440)
```

---

## mask.png

- **Format:** PNG (lossless)
- **Resolution:** 1920 × 1440 px (matches `rgb.jpg` exactly)
- **Content:** Anonymization mask. Non-zero pixels indicate regions
  that contain sensitive content (faces, personal items).
- Mask pixel value `> 0` corresponds to a magenta pixel in `rgb.jpg`.

---

## meta.bin

Fixed-size binary struct, **160 bytes**, all values IEEE 754
single-precision float, **little-endian**.

```
Offset   Bytes   Field        Shape    Description
------   -----   -----        -----    -----------
  0       64     pose         [4][4]   Camera-to-world transform
 64       36     intrinsic    [3][3]   RGB camera K matrix
100       60     imu          [15]     IMU measurement
```

### pose — 4×4 matrix (64 bytes)

Row-major. Transforms a point in camera space to world space.

```
[ R(3x3) | t(3x1) ]
[ 0  0  0 |   s   ]    s ≈ 1.0
```

**Coordinate system:** ARKit right-handed world frame.

| Axis | Direction                     |
| ---- | ----------------------------- |
| +X   | Right                         |
| +Y   | Up                            |
| +Z   | Toward viewer (out of screen) |

**Scale:** Raw ARKit pose — scale is **not metric**. ARKit does not
guarantee real-world units. If metric scale is needed, `aligned_pose`
from the ScanNet++ source JSON provides a version aligned to the
laser-scanned mesh coordinate space (not stored in this archive).

### intrinsic — 3×3 matrix K (36 bytes)

Row-major.

```
[ fx   0  cx ]
[  0  fy  cy ]
[  0   0   1 ]
```

Units: pixels. Valid for RGB at 1920 × 1440.

**Per-frame variation:** ARKit adjusts focal length dynamically
(auto-focus). `fx`, `fy`, `cx`, `cy` are different for every frame —
do not assume a single static K for the whole scene.

### imu — 15 floats (60 bytes)

```
Index   Count   Field          Unit     Description
-----   -----   -----          ----     -----------
  0       3     rotate_rate    rad/s    Gyroscope [x, y, z]
  3       3     acceleration   m/s²     User acceleration, gravity removed [x, y, z]
  6       3     magnet         µT       Magnetometer [x, y, z]  (often zero)
  9       3     attitude       rad      Device attitude [roll, pitch, yaw]
 12       3     gravity        —        Gravity unit vector in device frame [x, y, z]
```

---

## Decoding (C# / Unity)

```csharp
// depth.bin
float[,] DecodeDepth(byte[] bytes) {
    var depth = new float[192, 256];
    Buffer.BlockCopy(bytes, 0, depth, 0, bytes.Length); // little-endian float32
    return depth;
}

// meta.bin
struct FrameMeta {
    public float[] pose;       // [16]  read as flat, reshape to 4x4
    public float[] intrinsic;  // [9]   read as flat, reshape to 3x3
    public float[] imu;        // [15]
}

FrameMeta DecodeMeta(byte[] bytes) {
    // All floats are little-endian — matches x86/ARM default on Unity platforms
    int o = 0;
    var m = new FrameMeta();
    m.pose      = new float[16]; Buffer.BlockCopy(bytes, o, m.pose,      0, 64);  o += 64;
    m.intrinsic = new float[9];  Buffer.BlockCopy(bytes, o, m.intrinsic, 0, 36);  o += 36;
    m.imu       = new float[15]; Buffer.BlockCopy(bytes, o, m.imu,       0, 60);
    return m;
}

// IMU named accessors
Vector3 RotateRate(float[] imu)    => new Vector3(imu[0],  imu[1],  imu[2]);
Vector3 Acceleration(float[] imu)  => new Vector3(imu[3],  imu[4],  imu[5]);
Vector3 Magnet(float[] imu)        => new Vector3(imu[6],  imu[7],  imu[8]);
Vector3 Attitude(float[] imu)      => new Vector3(imu[9],  imu[10], imu[11]);
Vector3 Gravity(float[] imu)       => new Vector3(imu[12], imu[13], imu[14]);
```

---

## Omitted Fields

The following fields are present in the ScanNet++ source
`pose_intrinsic_imu.json` but are not stored in this archive:

| Field          | Type          | Description                                         |
| -------------- | ------------- | --------------------------------------------------- |
| `timestamp`    | float64       | ARKit capture timestamp (seconds since device boot) |
| `aligned_pose` | float32[4][4] | Pose aligned to metric mesh coordinate space        |

---

## Source Dataset

ScanNet++ — [https://github.com/scannetpp/scannetpp](https://github.com/scannetpp/scannetpp)

ICCV 2023. iPhone data captured with ARKit on iPhone 13 Pro (LiDAR + RGB).
