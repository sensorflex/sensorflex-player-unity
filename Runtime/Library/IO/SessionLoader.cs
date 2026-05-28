// SessionLoader.cs — backend-agnostic session.json parsing and track/attachment loading.
//
// ISessionDataProvider is implemented by the I/O backends (SFZ archive, FileIo directory).
// SessionLoader drives all loading: it parses session.json, iterates frame records, and
// reads attachment bytes — the backend only supplies raw bytes for a given path.
//
// Consumers:
//   SfzBackendBase      — implements ISessionDataProvider; calls TryLoad + TryLoadFrame
//   LiveWebSocketBackend — calls TryParse + ApplyToState (no file I/O needed)
//   ScannedMeshLoader   — creates a provider and calls TryLoad + LoadSceneMeshBytes

using System;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    /// <summary>
    /// Abstracts the I/O source for a session so SessionLoader can drive loading
    /// without knowing whether data comes from a ZIP archive or a directory.
    /// </summary>
    internal interface ISessionDataProvider
    {
        /// <summary>Returns the raw session.json text, or false on failure.</summary>
        bool TryReadJson(out string json);

        /// <summary>
        /// Returns the bytes for a path relative to the session root (e.g. "rgb/000000.jpg"),
        /// or null if the file is missing or unreadable.
        /// </summary>
        byte[] ReadFile(string relativePath);
    }

    internal sealed class SfzSessionData
    {
        public string SessionId { get; }
        public double FrameInterval { get; }
        public SfzUtils.SfzFrameRecordJson[] FrameRecords { get; }  // null for live (no pre-known array)
        public int TotalFrames { get; }       // FrameRecords.Length, or int.MaxValue for live
        public string SceneMeshFile { get; }  // null when no scene_mesh attachment

        internal SfzSessionData(
            string sessionId,
            double frameInterval,
            SfzUtils.SfzFrameRecordJson[] frameRecords,
            string sceneMeshFile)
        {
            SessionId     = sessionId ?? "session";
            FrameInterval = frameInterval;
            FrameRecords  = frameRecords;
            TotalFrames   = frameRecords?.Length ?? int.MaxValue;
            SceneMeshFile = sceneMeshFile;
        }
    }

    internal static class SessionLoader
    {
        /// <summary>
        /// Reads and parses session.json through <paramref name="provider"/>.
        /// Returns false if the provider fails or the JSON is not a valid session.json.
        /// </summary>
        public static bool TryLoad(ISessionDataProvider provider, out SfzSessionData data)
        {
            data = null;
            if (!provider.TryReadJson(out var json))
                return false;

            if (!TryParse(json, out data))
            {
                Debug.LogError("[SF] SessionLoader: failed to parse session.json.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses a session.json string into an <see cref="SfzSessionData"/>.
        /// Returns false if the string is not a valid session.json (missing version field).
        /// </summary>
        public static bool TryParse(string json, out SfzSessionData data)
        {
            data = null;
            if (string.IsNullOrEmpty(json))
                return false;

            var raw = JsonUtility.FromJson<SfzUtils.SfzSessionJson>(json);
            if (raw == null || string.IsNullOrEmpty(raw.version))
                return false;

            int fps = raw.tracks?.frames?.metadata?.fps ?? 30;

            data = new SfzSessionData(
                sessionId:     raw.session_id,
                frameInterval: 1.0 / Math.Max(1, fps),
                frameRecords:  raw.tracks?.frames?.data,
                sceneMeshFile: raw.attachments?.scene_mesh?.file);

            return true;
        }

        /// <summary>
        /// Writes <see cref="SfzSessionData"/> fields into an <see cref="IFrameLoaderState"/>.
        /// Does not call AllocateRingBuffer — the caller is responsible for that.
        /// </summary>
        public static void ApplyToState(SfzSessionData data, IFrameLoaderState state)
        {
            state.TotalFrames                    = data.TotalFrames;
            state.FrameInterval                  = data.FrameInterval;
            state.CoordConvMatrix                = Matrix4x4.identity;
            state.UseNegativeZForwardOpticalAxis = false;
        }

        /// <summary>
        /// Loads the rgb and depth bytes for a single frame record via <paramref name="provider"/>.
        /// Returns false if the record has no rgb file or the read fails.
        /// <paramref name="depth"/> is null when the record has no depth channel.
        /// </summary>
        public static bool TryLoadFrame(
            SfzSessionData data, int recordIndex, ISessionDataProvider provider,
            out byte[] rgb, out byte[] depth)
        {
            rgb = depth = null;
            var record = data.FrameRecords[recordIndex];

            if (string.IsNullOrEmpty(record.rgb?.file))
                return false;

            rgb = provider.ReadFile(record.rgb.file);
            if (rgb == null)
                return false;

            depth = !string.IsNullOrEmpty(record.depth?.file)
                ? provider.ReadFile(record.depth.file)
                : null;

            return true;
        }

        /// <summary>
        /// Reads the scene mesh attachment bytes via <paramref name="provider"/>.
        /// Returns null if the session has no scene_mesh attachment or the read fails.
        /// </summary>
        public static byte[] LoadSceneMeshBytes(SfzSessionData data, ISessionDataProvider provider)
        {
            if (string.IsNullOrEmpty(data.SceneMeshFile))
                return null;

            return provider.ReadFile(data.SceneMeshFile);
        }
    }
}
