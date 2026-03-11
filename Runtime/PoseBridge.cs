using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Lightweight static bridge that carries camera pose data from the SensorFlex
    /// plugin to the host application without requiring AR Foundation pose APIs.
    ///
    /// Poses delivered here are already in Unity world space (left-handed, +Y up, +Z forward).
    /// Coordinate conversion from the source format is performed by the plugin before
    /// calling <see cref="SetUnityPose"/>; the conversion parameters are read from the
    /// archive's <c>meta.json</c> at runtime rather than being hardcoded.
    /// </summary>
    public static class PoseBridge
    {
        /// <summary>True once the first pose has been pushed.</summary>
        public static bool HasPose { get; private set; }

        /// <summary>Latest camera pose in Unity world space.</summary>
        public static Pose LatestPose { get; private set; }

        /// <summary>
        /// Fires on the main thread each time a new pose is available.
        /// Argument is the pose in Unity world space.
        /// </summary>
        public static event System.Action<Pose> OnPoseUpdated;

        /// <summary>
        /// Push a pose that is already in Unity world space.
        /// Called internally by the SensorFlex plugin each frame.
        /// </summary>
        public static void SetUnityPose(Pose pose)
        {
            LatestPose = pose;
            HasPose = true;
            OnPoseUpdated?.Invoke(LatestPose);
        }

        /// <summary>Reset state (e.g. on subsystem stop).</summary>
        public static void Clear()
        {
            HasPose = false;
            LatestPose = Pose.identity;
        }
    }
}
