using UnityEngine;

namespace SensorFlex.Player
{
    internal static class PoseBridge
    {
        public static bool HasPose { get; private set; }
        public static Pose LatestPose { get; private set; }

        public static event System.Action<Pose> OnPoseUpdated;

        public static void SetUnityPose(Pose pose)
        {
            LatestPose = pose;
            HasPose = true;
            OnPoseUpdated?.Invoke(LatestPose);
        }

        public static void Clear()
        {
            HasPose = false;
            LatestPose = Pose.identity;
        }
    }
}
