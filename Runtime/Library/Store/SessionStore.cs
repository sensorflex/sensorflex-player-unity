// SessionStore.cs — internal shared session state for subsystems.
//
// Owns the FrameLoader lifecycle and exposes session and ring-buffer data
// through typed proxy properties so CameraSubsystem, OcclusionSubsystem,
// ControlBridge, and ScannedMeshLoader can read from it without ever
// referencing FrameLoader directly.
//
// CameraSubsystem drives the lifecycle: StartSession() on warmup,
// StopSessionAsync() on stop/restart, and per-frame writes to
// LatestTimestampNs / LatestIntrinsics / LatestTextureDimensions.
// All other consumers are read-only.

using System.Threading.Tasks;
using UnityEngine;

namespace SensorFlex.Player.Library
{
    internal static class SessionStore
    {
        // ── Private loader ────────────────────────────────────────────────────
        static FrameLoader s_Loader;

        // ── Per-frame data (written by CameraSubsystem each frame) ────────────
        internal static long       LatestTimestampNs       = 0;
        internal static Vector4    LatestIntrinsics        = new(935.3f, 935.3f, 960f, 720f);
        internal static Vector2Int LatestTextureDimensions = new(1920, 1440);

        // ── Session state ─────────────────────────────────────────────────────
        internal static bool           IsActive    => s_Loader != null;
        internal static bool           IsReady     => s_Loader?.IsReady ?? false;
        internal static SfzSessionData SessionData => s_Loader?.SessionData;

        // ── Ring buffer proxies (read-only for all consumers) ─────────────────
        internal static double    FrameInterval                  => s_Loader?.FrameInterval ?? (1.0 / 30);
        internal static Matrix4x4 CoordConvMatrix                => s_Loader?.CoordConvMatrix ?? Matrix4x4.identity;
        internal static bool      UseNegativeZForwardOpticalAxis => s_Loader?.UseNegativeZForwardOpticalAxis ?? false;
        internal static int       TotalFrames                    => s_Loader?.TotalFrames ?? 0;
        internal static int       BufSize                        => s_Loader?.BufSize ?? 0;
        internal static Texture2D[]  Frames        => s_Loader?.Frames;
        internal static byte[][]     DepthBins     => s_Loader?.DepthBins;
        internal static Matrix4x4[]  Poses         => s_Loader?.Poses;
        internal static Vector4[]    Intrinsics    => s_Loader?.Intrinsics;
        internal static bool[]       SlotReady     => s_Loader?.SlotReady;
        internal static int[]        SlotGlobalIdx => s_Loader?.SlotGlobalIdx;
        internal static int          LatestGlobalIndex  => s_Loader?.LatestGlobalIndex ?? -1;
        internal static int          PendingDecodeCount => s_Loader?.PendingDecodeCount ?? 0;

        internal static int PlayHead
        {
            get => s_Loader?.PlayHead ?? -1;
            set { if (s_Loader != null) s_Loader.PlayHead = value; }
        }

        // ── Lifecycle (called by CameraSubsystem) ─────────────────────────────

        internal static void StartSession(ARSensorFlexSession session, int maxFramesToLoad, int framesToWait)
        {
            var loader = new FrameLoader();
            loader.Start(session, maxFramesToLoad, framesToWait);
            s_Loader = loader;
        }

        internal static void Tick() => s_Loader?.Tick();

        internal static async Task StopSessionAsync()
        {
            var loader = s_Loader;
            s_Loader = null;
            ResetFrameData();

            if (loader != null)
            {
                await loader.StopAsync();
                loader.DestroyTextures();
            }
        }

        internal static byte[] TryConsumeAttachment(string name) => s_Loader?.TryConsumeAttachment(name);

        // Resets all state without stopping any active loader.
        // Use only when no session is running (e.g., on initial Start()).
        internal static void Clear()
        {
            s_Loader = null;
            ResetFrameData();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static void ResetFrameData()
        {
            LatestTimestampNs       = 0;
            LatestIntrinsics        = new Vector4(935.3f, 935.3f, 960f, 720f);
            LatestTextureDimensions = new Vector2Int(1920, 1440);
        }
    }
}
