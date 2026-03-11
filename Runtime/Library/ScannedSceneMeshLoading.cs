using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace SensorFlex.Player.Library
{
    internal sealed class ScannedSceneMeshLoadOperation
    {
        readonly Task<ScannedSceneMeshData> m_Task;
        readonly string m_SceneId;

        public bool IsCompleted => m_Task.IsCompleted;
        public string SceneId => m_SceneId;

        ScannedSceneMeshLoadOperation(Task<ScannedSceneMeshData> task, string sceneId)
        {
            m_Task = task;
            m_SceneId = sceneId;
        }

        public static ScannedSceneMeshLoadOperation Start(SensorFlexSettings settings)
        {
            if (settings == null || settings.frameSourceMode != SensorFlexSettings.FrameSourceMode.Zip)
                return null;

            string zipPath = settings.zipFilePath;
            if (!Path.IsPathRooted(zipPath))
                zipPath = Path.Combine(Application.streamingAssetsPath, zipPath);

            if (!File.Exists(zipPath))
            {
                Debug.LogError($"[SF] ZIP not found for scanned mesh load: {zipPath}");
                return null;
            }

            using var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
            var sceneMetaEntry = FindSceneMetaEntry(archive);
            if (sceneMetaEntry == null)
                return null;

            string json;
            using (var sr = new StreamReader(sceneMetaEntry.Open()))
                json = sr.ReadToEnd();

            var meta = JsonUtility.FromJson<ArchiveIOUtils.SceneMetaJson>(json);
            var meshMeta = ArchiveIOUtils.GetScannedMeshMeta(meta);
            if (meshMeta == null || string.IsNullOrEmpty(meshMeta.path))
                return null;

            string meshEntryPath = $"{meta.scene_id}/{meshMeta.path}";
            if (archive.GetEntry(meshEntryPath) == null)
            {
                Debug.LogError($"[SF] Scanned mesh entry missing: {meshEntryPath}");
                return null;
            }

            Matrix4x4 coordConv = meta.coordinate_system != null
                ? ArchiveIOUtils.ComputeConversionMatrix(meta.coordinate_system.handedness, meta.coordinate_system.forward)
                : Matrix4x4.identity;

            var task = Task.Run(() => LoadMeshData(zipPath, meshEntryPath, coordConv));
            return new ScannedSceneMeshLoadOperation(task, meta.scene_id);
        }

        public bool TryComplete(out Mesh mesh)
        {
            mesh = null;
            if (!m_Task.IsCompleted)
                return false;

            if (m_Task.IsFaulted)
            {
                Debug.LogError("[SF] Scanned mesh load failed: " + m_Task.Exception?.GetBaseException());
                return true;
            }

            var data = m_Task.Result;
            if (data == null)
                return true;

            mesh = BuildUnityMesh(data, m_SceneId);
            return true;
        }

        static ZipArchiveEntry FindSceneMetaEntry(ZipArchive archive)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/meta.json", StringComparison.Ordinal) &&
                    entry.FullName.Count(c => c == '/') == 1)
                    return entry;
            }

            return null;
        }

        static ScannedSceneMeshData LoadMeshData(string zipPath, string meshEntryPath, Matrix4x4 coordConvMatrix)
        {
            using var archive = new ZipArchive(File.OpenRead(zipPath), ZipArchiveMode.Read);
            var entry = archive.GetEntry(meshEntryPath);
            if (entry == null)
                throw new InvalidOperationException($"Mesh entry not found: {meshEntryPath}");

            var bytes = ArchiveIOUtils.ReadEntry(entry);
            return PlyMeshReader.Parse(bytes, coordConvMatrix);
        }

        static Mesh BuildUnityMesh(ScannedSceneMeshData data, string sceneId)
        {
            var mesh = new Mesh
            {
                name = $"SensorFlexScannedMesh-{sceneId}"
            };

            mesh.indexFormat = data.Vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = data.Vertices;
            mesh.triangles = data.Triangles;

            if (data.Normals != null && data.Normals.Length == data.Vertices.Length)
                mesh.normals = data.Normals;
            else
                mesh.RecalculateNormals();

            mesh.RecalculateBounds();
            return mesh;
        }
    }

    sealed class ScannedSceneMeshData
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public int[] Triangles;
    }

    static class PlyMeshReader
    {
        enum PlyFormat
        {
            Ascii,
            BinaryLittleEndian
        }

        sealed class Header
        {
            public PlyFormat Format;
            public int HeaderBytes;
            public int VertexCount;
            public int FaceCount;
            public readonly List<PlyProperty> VertexProperties = new();
            public PlyProperty FaceListProperty;
        }

        sealed class PlyProperty
        {
            public string Name;
            public string Type;
            public string CountType;
            public bool IsList;
        }

        public static ScannedSceneMeshData Parse(byte[] bytes, Matrix4x4 coordConvMatrix)
        {
            var header = ParseHeader(bytes);
            return header.Format == PlyFormat.Ascii
                ? ParseAscii(bytes, header, coordConvMatrix)
                : ParseBinaryLittleEndian(bytes, header, coordConvMatrix);
        }

        static Header ParseHeader(byte[] bytes)
        {
            int headerEnd = FindHeaderEnd(bytes);
            string headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var lines = headerText.Split('\n');
            var header = new Header { HeaderBytes = headerEnd };

            bool inVertexElement = false;
            bool inFaceElement = false;
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("format ", StringComparison.Ordinal))
                {
                    if (line.Contains("ascii"))
                        header.Format = PlyFormat.Ascii;
                    else if (line.Contains("binary_little_endian"))
                        header.Format = PlyFormat.BinaryLittleEndian;
                    else
                        throw new NotSupportedException($"Unsupported PLY format line: {line}");
                }
                else if (line.StartsWith("element ", StringComparison.Ordinal))
                {
                    inVertexElement = false;
                    inFaceElement = false;

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3) continue;

                    if (parts[1] == "vertex")
                    {
                        header.VertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                        inVertexElement = true;
                    }
                    else if (parts[1] == "face")
                    {
                        header.FaceCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                        inFaceElement = true;
                    }
                }
                else if (line.StartsWith("property ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (inVertexElement)
                    {
                        header.VertexProperties.Add(new PlyProperty
                        {
                            Name = parts[^1],
                            Type = parts[1],
                            IsList = false
                        });
                    }
                    else if (inFaceElement && parts.Length >= 5 && parts[1] == "list")
                    {
                        header.FaceListProperty = new PlyProperty
                        {
                            IsList = true,
                            CountType = parts[2],
                            Type = parts[3],
                            Name = parts[4]
                        };
                    }
                }
            }

            if (header.VertexCount <= 0)
                throw new InvalidOperationException("PLY mesh has no vertices.");
            if (header.FaceCount <= 0)
                throw new InvalidOperationException("PLY mesh has no faces.");
            if (header.FaceListProperty == null)
                throw new NotSupportedException("PLY face list property is missing.");

            return header;
        }

        static int FindHeaderEnd(byte[] bytes)
        {
            var marker = Encoding.ASCII.GetBytes("end_header\n");
            int idx = IndexOf(bytes, marker);
            if (idx >= 0)
                return idx + marker.Length;

            marker = Encoding.ASCII.GetBytes("end_header\r\n");
            idx = IndexOf(bytes, marker);
            if (idx >= 0)
                return idx + marker.Length;

            throw new InvalidOperationException("PLY header terminator not found.");
        }

        static ScannedSceneMeshData ParseAscii(byte[] bytes, Header header, Matrix4x4 coordConvMatrix)
        {
            using var reader = new StringReader(Encoding.ASCII.GetString(bytes, header.HeaderBytes, bytes.Length - header.HeaderBytes));
            var vertices = new Vector3[header.VertexCount];
            Vector3[] normals = TryAllocateNormals(header);

            for (int i = 0; i < header.VertexCount; i++)
            {
                var parts = ReadTokens(reader);
                vertices[i] = ReadVertex(parts, header.VertexProperties, ref normals, i);
            }

            var triangles = new List<int>(header.FaceCount * 3);
            for (int i = 0; i < header.FaceCount; i++)
            {
                var parts = ReadTokens(reader);
                int count = int.Parse(parts[0], CultureInfo.InvariantCulture);
                TriangulateFace(parts, 1, count, triangles);
            }

            ApplyCoordinateConversion(vertices, normals, triangles, coordConvMatrix);
            return new ScannedSceneMeshData { Vertices = vertices, Normals = normals, Triangles = triangles.ToArray() };
        }

        static ScannedSceneMeshData ParseBinaryLittleEndian(byte[] bytes, Header header, Matrix4x4 coordConvMatrix)
        {
            var vertices = new Vector3[header.VertexCount];
            Vector3[] normals = TryAllocateNormals(header);

            using var ms = new MemoryStream(bytes, header.HeaderBytes, bytes.Length - header.HeaderBytes, false);
            using var br = new BinaryReader(ms);

            for (int i = 0; i < header.VertexCount; i++)
                vertices[i] = ReadBinaryVertex(br, header.VertexProperties, ref normals, i);

            var triangles = new List<int>(header.FaceCount * 3);
            for (int i = 0; i < header.FaceCount; i++)
            {
                int count = (int)ReadScalar(br, header.FaceListProperty.CountType);
                var indices = new int[count];
                for (int j = 0; j < count; j++)
                    indices[j] = (int)ReadScalar(br, header.FaceListProperty.Type);

                TriangulateFace(indices, triangles);
            }

            ApplyCoordinateConversion(vertices, normals, triangles, coordConvMatrix);
            return new ScannedSceneMeshData { Vertices = vertices, Normals = normals, Triangles = triangles.ToArray() };
        }

        static string[] ReadTokens(StringReader reader)
        {
            string line;
            do
            {
                line = reader.ReadLine();
                if (line == null)
                    throw new EndOfStreamException("Unexpected end of ASCII PLY stream.");
                line = line.Trim();
            }
            while (line.Length == 0);

            return line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        static Vector3[] TryAllocateNormals(Header header)
        {
            bool hasNormals = false;
            foreach (var property in header.VertexProperties)
            {
                if (property.Name == "nx" || property.Name == "ny" || property.Name == "nz")
                {
                    hasNormals = true;
                    break;
                }
            }

            return hasNormals ? new Vector3[header.VertexCount] : null;
        }

        static Vector3 ReadVertex(string[] parts, List<PlyProperty> properties, ref Vector3[] normals, int vertexIndex)
        {
            float x = 0f, y = 0f, z = 0f;
            float nx = 0f, ny = 0f, nz = 0f;

            for (int i = 0; i < properties.Count && i < parts.Length; i++)
            {
                float value = float.Parse(parts[i], CultureInfo.InvariantCulture);
                switch (properties[i].Name)
                {
                    case "x": x = value; break;
                    case "y": y = value; break;
                    case "z": z = value; break;
                    case "nx": nx = value; break;
                    case "ny": ny = value; break;
                    case "nz": nz = value; break;
                }
            }

            if (normals != null)
                normals[vertexIndex] = new Vector3(nx, ny, nz);

            return new Vector3(x, y, z);
        }

        static Vector3 ReadBinaryVertex(BinaryReader br, List<PlyProperty> properties, ref Vector3[] normals, int vertexIndex)
        {
            float x = 0f, y = 0f, z = 0f;
            float nx = 0f, ny = 0f, nz = 0f;

            for (int i = 0; i < properties.Count; i++)
            {
                float value = (float)ReadScalar(br, properties[i].Type);
                switch (properties[i].Name)
                {
                    case "x": x = value; break;
                    case "y": y = value; break;
                    case "z": z = value; break;
                    case "nx": nx = value; break;
                    case "ny": ny = value; break;
                    case "nz": nz = value; break;
                }
            }

            if (normals != null)
                normals[vertexIndex] = new Vector3(nx, ny, nz);

            return new Vector3(x, y, z);
        }

        static double ReadScalar(BinaryReader br, string type)
        {
            return type switch
            {
                "char" or "int8" => br.ReadSByte(),
                "uchar" or "uint8" => br.ReadByte(),
                "short" or "int16" => br.ReadInt16(),
                "ushort" or "uint16" => br.ReadUInt16(),
                "int" or "int32" => br.ReadInt32(),
                "uint" or "uint32" => br.ReadUInt32(),
                "float" or "float32" => br.ReadSingle(),
                "double" or "float64" => br.ReadDouble(),
                _ => throw new NotSupportedException($"Unsupported PLY scalar type '{type}'.")
            };
        }

        static void TriangulateFace(string[] parts, int start, int count, List<int> triangles)
        {
            if (count < 3)
                return;

            int first = int.Parse(parts[start], CultureInfo.InvariantCulture);
            for (int i = 1; i < count - 1; i++)
            {
                triangles.Add(first);
                triangles.Add(int.Parse(parts[start + i], CultureInfo.InvariantCulture));
                triangles.Add(int.Parse(parts[start + i + 1], CultureInfo.InvariantCulture));
            }
        }

        static void TriangulateFace(int[] indices, List<int> triangles)
        {
            if (indices.Length < 3)
                return;

            int first = indices[0];
            for (int i = 1; i < indices.Length - 1; i++)
            {
                triangles.Add(first);
                triangles.Add(indices[i]);
                triangles.Add(indices[i + 1]);
            }
        }

        static void ApplyCoordinateConversion(Vector3[] vertices, Vector3[] normals, List<int> triangles, Matrix4x4 coordConvMatrix)
        {
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = coordConvMatrix.MultiplyPoint3x4(vertices[i]);

            if (normals != null)
            {
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = coordConvMatrix.MultiplyVector(normals[i]).normalized;
            }

            if (ChangesHandedness(coordConvMatrix))
            {
                for (int i = 0; i + 2 < triangles.Count; i += 3)
                {
                    (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
                }
            }
        }

        static bool ChangesHandedness(Matrix4x4 matrix)
        {
            var x = matrix.MultiplyVector(Vector3.right);
            var y = matrix.MultiplyVector(Vector3.up);
            var z = matrix.MultiplyVector(Vector3.forward);
            return Vector3.Dot(Vector3.Cross(x, y), z) < 0f;
        }

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return i;
            }

            return -1;
        }
    }
}
