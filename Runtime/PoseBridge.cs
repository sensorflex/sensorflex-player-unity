using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Lightweight static bridge that carries camera pose data from the SensorFlex
    /// plugin to the host application without requiring AR Foundation pose APIs.
    ///
    /// Coordinate systems
    /// ------------------
    /// Source (ScanNet++ MP4 Track 2): ARKit camera-to-world, right-handed.
    ///   +X right  |  +Y up  |  +Z toward viewer (out of screen)
    ///
    /// Exposed via this bridge: Unity world space, left-handed.
    ///   +X right  |  +Y up  |  +Z forward (into screen)
    ///
    /// The raw ARKit matrix is also exposed as <see cref="RawMatrix"/> if you
    /// need to do your own conversion.
    /// </summary>
    public static class PoseBridge
    {
        /// <summary>True once the first pose has been pushed.</summary>
        public static bool HasPose { get; private set; }

        /// <summary>Latest camera pose in Unity world space.</summary>
        public static Pose LatestPose { get; private set; }

        /// <summary>Raw camera-to-world matrix in ARKit right-handed space.</summary>
        public static Matrix4x4 RawMatrix { get; private set; }

        /// <summary>
        /// Fires on the main thread each time a new pose is available.
        /// Argument is the pose in Unity world space.
        /// </summary>
        public static event System.Action<Pose> OnPoseUpdated;

        /// <summary>
        /// Push a new camera-to-world pose from ARKit space.
        /// Called internally by the SensorFlex plugin (e.g. the MP4 metadata decoder).
        /// Can also be called by host-app code if you obtain poses from another source.
        /// </summary>
        public static void SetARKitPose(Matrix4x4 arkitCameraToWorld)
        {
            RawMatrix = arkitCameraToWorld;
            LatestPose = ARKitToUnityPose(arkitCameraToWorld);
            HasPose = true;
            OnPoseUpdated?.Invoke(LatestPose);
        }

        /// <summary>
        /// Push a pose that is already in Unity world space (no conversion applied).
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
            RawMatrix = Matrix4x4.identity;
        }

        // ── Coordinate conversion ────────────────────────────────────────────────
        // ARKit world:  right-handed, +Z out of screen (toward viewer)
        // Unity world:  left-handed,  +Z into screen   (forward)
        //
        // To convert a world-space point:  Unity.xyz = (ARKit.x, ARKit.y, -ARKit.z)
        //
        // For the rotation we reconstruct forward/up from the matrix columns:
        //   ARKit camera "into screen" = M * (0, 0, -1)  →  column 2 negated
        //   Converting that world-space vector to Unity space flips its Z once more.
        //   ARKit camera "up"          = M * (0,  1,  0) →  column 1
        //   Converting to Unity space flips its Z.
        static Pose ARKitToUnityPose(Matrix4x4 m)
        {
            var position = new Vector3(m.m03, m.m13, -m.m23);

            // Camera "forward" in ARKit world space = -column2; flip Z for Unity space
            var forward = new Vector3(-m.m02, -m.m12, m.m22);
            // Camera "up" in ARKit world space = column1; flip Z for Unity space
            var up = new Vector3(m.m01, m.m11, -m.m21);

            if (forward == Vector3.zero || up == Vector3.zero)
                return new Pose(position, Quaternion.identity);

            return new Pose(position, Quaternion.LookRotation(forward, up));
        }
    }
}
