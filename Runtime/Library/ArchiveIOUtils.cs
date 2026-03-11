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
        [Serializable] internal class SceneMetaJson { public string scene_id; public int n_frames; public int fps; public CoordSystem coordinate_system; }

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
        /// </summary>
        internal static Pose ConvertToUnityPose(Matrix4x4 source, Matrix4x4 c)
        {
            var m        = c * source * c;
            var position = new Vector3(m.m03, m.m13, m.m23);
            var forward  = new Vector3(m.m02, m.m12, m.m22); // col-2 = camera +Z in Unity
            var up       = new Vector3(m.m01, m.m11, m.m21);
            if (forward == Vector3.zero || up == Vector3.zero)
                return new Pose(position, Quaternion.identity);
            return new Pose(position, Quaternion.LookRotation(forward, up));
        }
    }
}
