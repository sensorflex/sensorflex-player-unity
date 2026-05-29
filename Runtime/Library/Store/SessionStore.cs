// SessionStore.cs — internal shared session state for subsystems.
//
// A single place where session-scoped data lives so that CameraSubsystem,
// OcclusionSubsystem, ControlBridge, and ScannedMeshLoader can all read from
// it without knowing about each other.
//
// CameraSubsystem writes: Set(loader) on session start, Clear() on stop,
// and per-frame assignments to LatestTimestampNs / LatestIntrinsics /
// LatestTextureDimensions.  All other consumers are read-only.

using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal static class SessionStore
    {
        // ── Session ───────────────────────────────────────────────────────────

        /// <summary>Active FrameLoader; non-null while a session is running.</summary>
        internal static FrameLoader FrameLoader { get; private set; }

        // ── Per-frame data (written by CameraSubsystem each frame) ────────────

        internal static long       LatestTimestampNs      { get; internal set; }
        internal static Vector4    LatestIntrinsics       { get; internal set; } = new(935.3f, 935.3f, 960f, 720f);
        internal static Vector2Int LatestTextureDimensions { get; internal set; } = new(1920, 1440);

        // ── Lifecycle (called by CameraSubsystem) ─────────────────────────────

        internal static void Set(FrameLoader loader)
        {
            FrameLoader = loader;
        }

        internal static void Clear()
        {
            FrameLoader = null;
            LatestTimestampNs = 0;
            LatestIntrinsics = new Vector4(935.3f, 935.3f, 960f, 720f);
            LatestTextureDimensions = new Vector2Int(1920, 1440);
        }
    }
}
