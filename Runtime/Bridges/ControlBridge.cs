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
    public static class ControlBridge
    {
        static bool  s_IsPlaying     = true;
        static float s_PlaybackSpeed = 1f;

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

        /// <summary>Fires on the main thread when <see cref="IsPlaying"/> changes.</summary>
        public static event Action<bool> OnPlayStateChanged;

        /// <summary>Fires on the main thread when <see cref="PlaybackSpeed"/> changes.</summary>
        public static event Action<float> OnSpeedChanged;

        /// <summary>
        /// Fires when <see cref="StepForward"/> is called while paused.
        /// The camera subsystem advances the playhead by one frame in response.
        /// </summary>
        public static event Action OnStepForward;

        /// <summary>Resume playback.</summary>
        public static void Play()  => IsPlaying = true;

        /// <summary>Pause playback.</summary>
        public static void Pause() => IsPlaying = false;

        /// <summary>Toggle between playing and paused.</summary>
        public static void TogglePlay() => IsPlaying = !s_IsPlaying;

        /// <summary>Set the playback speed multiplier (clamped to [0.05, 8]).</summary>
        public static void SetSpeed(float speed) => PlaybackSpeed = speed;

        /// <summary>
        /// Advance one frame. Only effective when <see cref="IsPlaying"/> is false.
        /// </summary>
        public static void StepForward()
        {
            if (s_IsPlaying) return;
            OnStepForward?.Invoke();
        }

        /// <summary>Reset to defaults (playing, 1× speed). Does not touch event subscriptions.</summary>
        public static void Clear()
        {
            s_IsPlaying     = true;
            s_PlaybackSpeed = 1f;
        }
    }
}
