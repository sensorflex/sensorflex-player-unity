using System;
using UnityEngine;
using SensorFlex.Player.Subsystem;

namespace SensorFlex.Player.Library
{
    internal enum LiveConnectionState { Disconnected, Connecting, Live }

    internal static class ControlBridge
    {
        static bool  s_IsPlaying                = true;
        static float s_PlaybackSpeed            = 1f;
        static bool  s_DepthVisualizationEnabled;
        static LiveConnectionState s_ConnectionState = LiveConnectionState.Disconnected;

        public static bool IsPlaying
        {
            get => s_IsPlaying;
            private set
            {
                if (s_IsPlaying == value) return;
                s_IsPlaying = value;
                OnPlayStateChanged?.Invoke(s_IsPlaying);
            }
        }

        public static float PlaybackSpeed
        {
            get => s_PlaybackSpeed;
            private set
            {
                float clamped = Mathf.Clamp(value, 0.05f, 8f);
                if (Mathf.Approximately(s_PlaybackSpeed, clamped)) return;
                s_PlaybackSpeed = clamped;
                OnSpeedChanged?.Invoke(s_PlaybackSpeed);
            }
        }

        public static int CurrentFrame
        {
            get
            {
                var loader = CameraSubsystem.CameraDataProvider.ActiveSession;
                if (loader == null) return 0;
                int ph    = Mathf.Max(0, loader.PlayHead);
                int total = loader.TotalFrames;
                if (total > 0 && total < int.MaxValue && ph >= total)
                    ph %= total;
                return ph;
            }
        }

        public static int TotalFrames
        {
            get
            {
                var loader = CameraSubsystem.CameraDataProvider.ActiveSession;
                if (loader == null) return 0;
                int t = loader.TotalFrames;
                return t == int.MaxValue ? 0 : t;
            }
        }

        public static LiveConnectionState ConnectionState
        {
            get => s_ConnectionState;
            private set
            {
                if (s_ConnectionState == value) return;
                s_ConnectionState = value;
                OnConnectionStateChanged?.Invoke(s_ConnectionState);
            }
        }

        public static bool DepthVisualizationEnabled
        {
            get => s_DepthVisualizationEnabled;
            private set
            {
                if (s_DepthVisualizationEnabled == value) return;
                s_DepthVisualizationEnabled = value;
                OnDepthVisualizationChanged?.Invoke(s_DepthVisualizationEnabled);
            }
        }

        public static event Action<LiveConnectionState> OnConnectionStateChanged;
        public static event Action<bool> OnPlayStateChanged;
        public static event Action<bool> OnDepthVisualizationChanged;
        public static event Action<float> OnSpeedChanged;
        public static event Action OnStepForward;
        public static event Action OnRestart;

        public static void Play()  => IsPlaying = true;
        public static void Pause() => IsPlaying = false;
        public static void TogglePlay() => IsPlaying = !s_IsPlaying;
        public static void SetSpeed(float speed) => PlaybackSpeed = speed;
        public static void ToggleDepthVisualization() => DepthVisualizationEnabled = !s_DepthVisualizationEnabled;

        public static void StepForward()
        {
            if (s_IsPlaying) return;
            OnStepForward?.Invoke();
        }

        public static void Restart() => OnRestart?.Invoke();

        internal static void SetConnectionState(LiveConnectionState state) => ConnectionState = state;

        public static void Clear()
        {
            s_IsPlaying                = true;
            s_PlaybackSpeed            = 1f;
            s_DepthVisualizationEnabled = false;
            ConnectionState            = LiveConnectionState.Disconnected;
        }
    }
}
