using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    // ── Static ZIP / JSON decoding helpers ───────────────────────────────────────

    /// <summary>
    /// Static helpers for ZIP entry reading, JSON float extraction, matrix packing,
    /// and coordinate-system conversion. Used by <see cref="FrameLoader"/> and
    /// any other subsystem that reads from the SensorFlex archive format.
    /// </summary>
    internal static class ArchiveIOUtils
    {
        // ── Scene meta.json DTOs ─────────────────────────────────────────────────

        [Serializable] internal class CoordSystem   { public string handedness; public string forward; public string up; }
        [Serializable] internal class MeshMetaJson  { public string path; public string format; public string units; public string coordinate_frame; }
        [Serializable] internal class SourceMetaJson { public string dataset; public string device; public string capture_framework; }
        [Serializable] internal class SceneMetaJson
        {
            public string format_version;
            public string scene_id;
            public int n_frames;
            public int fps;
            public CoordSystem coordinate_system;
            public SourceMetaJson source;
            public MeshMetaJson scanned_mesh;
            public MeshMetaJson mesh;
        }

        // ── ZIP entry reading ────────────────────────────────────────────────────

        /// <summary>Reads the full decompressed content of a ZIP entry into a byte array.</summary>
        internal static byte[] ReadEntry(ZipArchiveEntry entry)
        {
            var buf   = new byte[(int)entry.Length];
            using var s = entry.Open();
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return buf;
        }

        // ── JSON helpers ─────────────────────────────────────────────────────────

        // Matches integers, decimals, and scientific-notation floats (including negatives).
        static readonly Regex s_NumRegex = new Regex(@"-?\d+\.?\d*(?:[eE][+-]?\d+)?");

        /// <summary>
        /// Extracts all float values from a named JSON array field.
        /// Works with nested arrays such as <c>"pose": [[a,b],[c,d]]</c>.
        /// </summary>
        internal static float[] ExtractFloatsFromField(string json, string field)
        {
            int keyPos = json.IndexOf($"\"{field}\"", StringComparison.Ordinal);
            if (keyPos < 0) return null;
            int start = json.IndexOf('[', keyPos);
            if (start < 0) return null;

            int depth = 0, end = start;
            for (int i = start; i < json.Length; i++)
            {
                if      (json[i] == '[') depth++;
                else if (json[i] == ']') { if (--depth == 0) { end = i; break; } }
            }

            var matches = s_NumRegex.Matches(json.Substring(start, end - start + 1));
            var result  = new float[matches.Count];
            for (int i = 0; i < matches.Count; i++)
                result[i] = float.Parse(matches[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }

        // ── Matrix helpers ───────────────────────────────────────────────────────

        /// <summary>Packs a flat 16-element row-major float array into a <see cref="Matrix4x4"/>.</summary>
        internal static Matrix4x4 FloatsToMatrix4x4(float[] f)
        {
            var m = new Matrix4x4();
            m.m00=f[0];  m.m01=f[1];  m.m02=f[2];  m.m03=f[3];
            m.m10=f[4];  m.m11=f[5];  m.m12=f[6];  m.m13=f[7];
            m.m20=f[8];  m.m21=f[9];  m.m22=f[10]; m.m23=f[11];
            m.m30=f[12]; m.m31=f[13]; m.m32=f[14]; m.m33=f[15];
            return m;
        }

        // ── Coordinate conversion ────────────────────────────────────────────────

        /// <summary>
        /// Builds the flip matrix C that converts a source coordinate system to
        /// Unity's left-handed (+Y up, +Z forward) convention.
        /// For right-handed -Z-forward sources (ARKit / OpenGL): C = diag(1,1,-1,1).
        /// </summary>
        internal static Matrix4x4 ComputeConversionMatrix(string handedness, string forward)
        {
            if (handedness != "right") return Matrix4x4.identity;
            if (string.IsNullOrEmpty(forward) || forward == "-Z")
                return new Matrix4x4(new Vector4(1,0,0,0), new Vector4(0,1,0,0),
                                     new Vector4(0,0,-1,0), new Vector4(0,0,0,1));
            return Matrix4x4.identity;
        }

        /// <summary>
        /// Converts a camera-to-world matrix from a source coordinate system to
        /// a Unity <see cref="Pose"/> using the symmetric formula M_unity = C * M_source * C.
        /// Archives that declare -Z-forward camera optics need the forward vector
        /// derived from the converted matrix to follow that optical-axis convention.
        /// </summary>
        internal static Pose ConvertToUnityPose(Matrix4x4 source, Matrix4x4 c, bool useNegativeZForwardOpticalAxis = false)
        {
            var m        = c * source * c;
            var position = new Vector3(m.m03, m.m13, m.m23);
            var forward  = useNegativeZForwardOpticalAxis
                ? new Vector3(-m.m02, -m.m12, -m.m22)
                : new Vector3(m.m02, m.m12, m.m22);
            var up       = useNegativeZForwardOpticalAxis
                ? new Vector3(-m.m01, -m.m11, -m.m21)
                : new Vector3(m.m01, m.m11, m.m21);
            if (forward == Vector3.zero || up == Vector3.zero)
                return new Pose(position, Quaternion.identity);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }

        /// <summary>
        /// Builds a Unity projection matrix from pinhole camera intrinsics
        /// (fx, fy, cx, cy) defined over an image of the given width and height.
        /// Intrinsic coordinates are assumed to use image-space origin at top-left.
        /// </summary>
        internal static Matrix4x4 ComputeProjectionMatrix(Vector4 intrinsics, int imageWidth, int imageHeight, float nearClipPlane, float farClipPlane)
        {
            float fx = intrinsics.x;
            float fy = intrinsics.y;
            float cx = intrinsics.z;
            float cy = intrinsics.w;

            if (fx <= 0f || fy <= 0f || imageWidth <= 0 || imageHeight <= 0)
                return Matrix4x4.Perspective(60f, (float)imageWidth / Mathf.Max(1, imageHeight), nearClipPlane, farClipPlane);

            float left = -cx * nearClipPlane / fx;
            float right = (imageWidth - cx) * nearClipPlane / fx;
            float top = cy * nearClipPlane / fy;
            float bottom = -(imageHeight - cy) * nearClipPlane / fy;

            return PerspectiveOffCenter(left, right, bottom, top, nearClipPlane, farClipPlane);
        }

        static Matrix4x4 PerspectiveOffCenter(float left, float right, float bottom, float top, float near, float far)
        {
            float x = 2f * near / (right - left);
            float y = 2f * near / (top - bottom);
            float a = (right + left) / (right - left);
            float b = (top + bottom) / (top - bottom);
            float c = -(far + near) / (far - near);
            float d = -(2f * far * near) / (far - near);
            float e = -1f;

            var m = new Matrix4x4();
            m[0, 0] = x;  m[0, 1] = 0f; m[0, 2] = a;  m[0, 3] = 0f;
            m[1, 0] = 0f; m[1, 1] = y;  m[1, 2] = b;  m[1, 3] = 0f;
            m[2, 0] = 0f; m[2, 1] = 0f; m[2, 2] = c;  m[2, 3] = d;
            m[3, 0] = 0f; m[3, 1] = 0f; m[3, 2] = e;  m[3, 3] = 0f;
            return m;
        }

        internal static MeshMetaJson GetScannedMeshMeta(SceneMetaJson meta)
        {
            return meta?.scanned_mesh ?? meta?.mesh;
        }
    }
}
