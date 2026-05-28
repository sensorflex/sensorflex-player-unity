// SessionLoader.cs — backend-agnostic session.json parsing and track/attachment loading.
//
// ISessionDataProvider is implemented by the I/O backends (SFZ archive, FileIo directory).
// SessionLoader drives all loading: it parses session.json, populates SfzSessionData
// generic track/attachment maps, and provides typed helpers for loading frame records
// and attachment bytes — the backend only supplies raw bytes for a given path.
//
// Consumers:
//   SfzBackendBase      — implements ISessionDataProvider; calls TryLoad + TryLoadFrame
//   LiveWebSocketBackend — calls TryParse + ApplyToState (no file I/O needed)
//   ScannedMeshLoader   — creates a provider and calls TryLoad + LoadAttachmentBytes

using System;
using System.Collections.Generic;
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

    /// <summary>Generic metadata for one session track (frames, imu, …).</summary>
    internal sealed class SfzTrackInfo
    {
        public string Name           { get; }
        public double SampleInterval { get; }   // seconds between samples (1/fps, 1/rate_hz, …)
        public int    RecordCount    { get; }   // 0 when unknown (live / no pre-parsed array)

        internal SfzTrackInfo(string name, double sampleInterval, int recordCount)
        {
            Name           = name;
            SampleInterval = sampleInterval;
            RecordCount    = recordCount;
        }
    }

    /// <summary>Generic metadata for one session attachment (scene_mesh, …).</summary>
    internal sealed class SfzAttachmentInfo
    {
        public string Name   { get; }
        public string File   { get; }   // relative path within the session root
        public string Format { get; }   // e.g. "ply", or null if unspecified

        internal SfzAttachmentInfo(string name, string file, string format)
        {
            Name   = name;
            File   = file;
            Format = format;
        }
    }

    internal sealed class SfzSessionData
    {
        public string SessionId { get; }

        /// <summary>All tracks keyed by name (e.g. "frames", "imu").</summary>
        public IReadOnlyDictionary<string, SfzTrackInfo> Tracks { get; }

        /// <summary>All attachments keyed by name (e.g. "scene_mesh").</summary>
        public IReadOnlyDictionary<string, SfzAttachmentInfo> Attachments { get; }

        // Cached frame record array for TryLoadFrame — only accessed by SessionLoader.
        internal SfzUtils.SfzFrameRecordJson[] FrameRecords { get; }

        internal SfzSessionData(
            string sessionId,
            IReadOnlyDictionary<string, SfzTrackInfo>      tracks,
            IReadOnlyDictionary<string, SfzAttachmentInfo> attachments,
            SfzUtils.SfzFrameRecordJson[]                  frameRecords)
        {
            SessionId   = sessionId ?? "session";
            Tracks      = tracks      ?? new Dictionary<string, SfzTrackInfo>();
            Attachments = attachments ?? new Dictionary<string, SfzAttachmentInfo>();
            FrameRecords = frameRecords;
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

            var tracks = new Dictionary<string, SfzTrackInfo>();
            if (raw.tracks?.frames != null)
            {
                int fps   = raw.tracks.frames.metadata?.fps ?? 30;
                int count = raw.tracks.frames.data?.Length ?? 0;
                tracks["frames"] = new SfzTrackInfo("frames", 1.0 / Math.Max(1, fps), count);
            }
            if (raw.tracks?.imu != null)
            {
                float hz  = raw.tracks.imu.metadata?.sample_rate_hz ?? 100f;
                int count = raw.tracks.imu.data?.Length ?? 0;
                tracks["imu"] = new SfzTrackInfo("imu", 1.0 / Math.Max(1f, hz), count);
            }

            var attachments = new Dictionary<string, SfzAttachmentInfo>();
            if (raw.attachments?.scene_mesh != null)
                attachments["scene_mesh"] = new SfzAttachmentInfo(
                    "scene_mesh",
                    raw.attachments.scene_mesh.file,
                    raw.attachments.scene_mesh.format);

            data = new SfzSessionData(
                raw.session_id,
                tracks,
                attachments,
                raw.tracks?.frames?.data);

            return true;
        }

        /// <summary>
        /// Writes <see cref="SfzSessionData"/> fields into an <see cref="IFrameLoaderState"/>.
        /// Does not call AllocateRingBuffer — the caller is responsible for that.
        /// </summary>
        public static void ApplyToState(SfzSessionData data, IFrameLoaderState state)
        {
            bool hasFrames = data.Tracks.TryGetValue("frames", out var framesTrack);
            state.TotalFrames   = hasFrames && framesTrack.RecordCount > 0
                ? framesTrack.RecordCount
                : int.MaxValue;
            state.FrameInterval = hasFrames ? framesTrack.SampleInterval : 1.0 / 30;
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
        /// Reads the bytes for a named attachment via <paramref name="provider"/>.
        /// Returns null if the attachment is not present in the session or the read fails.
        /// </summary>
        public static byte[] LoadAttachmentBytes(
            SfzSessionData data, string attachmentName, ISessionDataProvider provider)
        {
            if (!data.Attachments.TryGetValue(attachmentName, out var att))
                return null;

            if (string.IsNullOrEmpty(att.File))
                return null;

            return provider.ReadFile(att.File);
        }
    }
}
