using System;
using UnityEngine;
using SensorFlex.Player.Subsystem;

namespace SensorFlex.Player
{
    /// <summary>
    /// Static bridge for controlling SensorFlex replay playback state.
    ///
    /// Play/pause, speed, and step-forward commands flow through here.
    /// The camera subsystem reads <see cref="IsPlaying"/> and <see cref="PlaybackSpeed"/>
    /// each frame; <see cref="OnStepForward"/> triggers a single-frame advance when paused.
    ///
    /// <see cref="CurrentFrame"/> and <see cref="TotalFrames"/> reflect the active loader
    /// and can be read by UI components for progress display.
    /// </summary>
    public enum LiveConnectionState { Disconnected, Connecting, Live }

    public static class ControlBridge
    {
        static bool  s_IsPlaying                = true;
        static float s_PlaybackSpeed            = 1f;
        static bool  s_DepthVisualizationEnabled;
        static LiveConnectionState s_ConnectionState = LiveConnectionState.Disconnected;

        /// <summary>Whether the session is currently advancing frames.</summary>
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

        /// <summary>Playback speed multiplier. 1.0 = normal, 2.0 = double speed, etc.</summary>
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

        /// <summary>
        /// Current 0-based frame index being displayed.
        /// Wraps to session range in looping mode.
        /// Returns 0 when no session is active.
        /// </summary>
        public static int CurrentFrame
        {
            get
            {
                var loader = CameraSubsystem.CameraDataProvider.ActiveLoader;
                if (loader == null) return 0;
                int ph    = Mathf.Max(0, loader.PlayHead);
                int total = loader.TotalFrames;
                if (total > 0 && total < int.MaxValue && ph >= total)
                    ph %= total;
                return ph;
            }
        }

        /// <summary>
        /// Total frames in the session. 0 means unknown (e.g. WebSocket or not yet loaded).
        /// </summary>
        public static int TotalFrames
        {
            get
            {
                var loader = CameraSubsystem.CameraDataProvider.ActiveLoader;
                if (loader == null) return 0;
                int t = loader.TotalFrames;
                return t == int.MaxValue ? 0 : t;
            }
        }

        /// <summary>Current WebSocket live-stream connection state.</summary>
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

        /// <summary>Whether depth heat-map visualization is active.</summary>
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

        /// <summary>Fires on the main thread when <see cref="ConnectionState"/> changes.</summary>
        public static event Action<LiveConnectionState> OnConnectionStateChanged;

        /// <summary>Fires on the main thread when <see cref="IsPlaying"/> changes.</summary>
        public static event Action<bool> OnPlayStateChanged;

        /// <summary>Fires on the main thread when <see cref="DepthVisualizationEnabled"/> changes.</summary>
        public static event Action<bool> OnDepthVisualizationChanged;

        /// <summary>Fires on the main thread when <see cref="PlaybackSpeed"/> changes.</summary>
        public static event Action<float> OnSpeedChanged;

        /// <summary>
        /// Fires when <see cref="StepForward"/> is called while paused.
        /// The camera subsystem advances the playhead by one frame in response.
        /// </summary>
        public static event Action OnStepForward;

        /// <summary>
        /// Fires when <see cref="Restart"/> is called.
        /// The camera subsystem stops the current loader and restarts frame warmup.
        /// </summary>
        public static event Action OnRestart;

        /// <summary>Resume playback.</summary>
        public static void Play()  => IsPlaying = true;

        /// <summary>Pause playback.</summary>
        public static void Pause() => IsPlaying = false;

        /// <summary>Toggle between playing and paused.</summary>
        public static void TogglePlay() => IsPlaying = !s_IsPlaying;

        /// <summary>Set the playback speed multiplier (clamped to [0.05, 8]).</summary>
        public static void SetSpeed(float speed) => PlaybackSpeed = speed;

        /// <summary>Toggle between RGB and depth heat-map visualization.</summary>
        public static void ToggleDepthVisualization() => DepthVisualizationEnabled = !s_DepthVisualizationEnabled;

        /// <summary>
        /// Advance one frame. Only effective when <see cref="IsPlaying"/> is false.
        /// </summary>
        public static void StepForward()
        {
            if (s_IsPlaying) return;
            OnStepForward?.Invoke();
        }

        /// <summary>
        /// Restart playback from frame 0. Stops the current loader and re-warms the buffer.
        /// The scanned mesh is not reloaded.
        /// </summary>
        public static void Restart() => OnRestart?.Invoke();

        /// <summary>Called by the live backend to update connection state on the main thread.</summary>
        internal static void SetConnectionState(LiveConnectionState state) => ConnectionState = state;

        /// <summary>Reset to defaults (playing, 1× speed, RGB view). Does not touch event subscriptions.</summary>
        public static void Clear()
        {
            s_IsPlaying                = true;
            s_PlaybackSpeed            = 1f;
            s_DepthVisualizationEnabled = false;
            ConnectionState            = LiveConnectionState.Disconnected;
        }
    }
}
