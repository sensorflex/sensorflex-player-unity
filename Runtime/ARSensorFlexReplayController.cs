using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Scene component that renders a replay control bar using IMGUI.
    /// Add to any scene object — no Canvas or EventSystem required.
    ///
    /// The bar appears at the bottom of the screen and provides:
    ///   - Play / Pause toggle
    ///   - Step forward one frame (enabled only when paused)
    ///   - Playback speed presets: 0.25× 0.5× 1× 2× 4×
    ///   - Progress bar and frame counter
    /// </summary>
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Replay Controller")]
    public sealed class ARSensorFlexReplayController : MonoBehaviour
    {
        [SerializeField] bool m_ShowUI = true;

        static readonly float[] k_SpeedPresets = { 0.25f, 0.5f, 1f, 2f, 4f };

        GUIStyle   m_BgStyle;
        GUIStyle   m_BtnStyle;
        GUIStyle   m_BtnActiveStyle;
        GUIStyle   m_LabelStyle;
        Texture2D  m_BgTex;
        Texture2D  m_ProgressBgTex;
        Texture2D  m_ProgressFillTex;
        bool       m_StylesReady;

        /// <summary>Show or hide the control bar at runtime.</summary>
        public bool ShowUI { get => m_ShowUI; set => m_ShowUI = value; }

        void OnGUI()
        {
            if (!m_ShowUI) return;
            EnsureStyles();
            DrawBar();
        }

        void DrawBar()
        {
            float barH = Mathf.Max(56f, Screen.height * 0.09f);
            float barY = Screen.height - barH;
            GUI.Box(new Rect(0, barY, Screen.width, barH), GUIContent.none, m_BgStyle);

            float pad  = barH * 0.15f;
            float btnH = barH - pad * 2f;
            float btnW = btnH * 1.25f;
            float x    = pad;
            float y    = barY + pad;

            // ── Play / Pause ────────────────────────────────────────────────
            string playLabel = ControlBridge.IsPlaying ? "⏸" : "▶";  // ⏸ or ▶
            if (GUI.Button(new Rect(x, y, btnW, btnH), playLabel, m_BtnStyle))
                ControlBridge.TogglePlay();
            x += btnW + pad * 0.5f;

            // ── Step forward (grayed while playing) ─────────────────────────
            bool wasEnabled = GUI.enabled;
            GUI.enabled = !ControlBridge.IsPlaying;
            if (GUI.Button(new Rect(x, y, btnW, btnH), "⏭", m_BtnStyle))  // ⏭
                ControlBridge.StepForward();
            GUI.enabled = wasEnabled;
            x += btnW + pad;

            // ── Speed presets ────────────────────────────────────────────────
            float speedBtnW = btnW * 1.3f;
            foreach (float speed in k_SpeedPresets)
            {
                bool   active = Mathf.Approximately(ControlBridge.PlaybackSpeed, speed);
                string label  = $"{speed:0.##}×";  // e.g. "0.5×"
                if (GUI.Button(new Rect(x, y, speedBtnW, btnH), label,
                               active ? m_BtnActiveStyle : m_BtnStyle))
                    ControlBridge.SetSpeed(speed);
                x += speedBtnW + pad * 0.4f;
            }

            // ── Frame counter (pinned right) ─────────────────────────────────
            int    cur       = ControlBridge.CurrentFrame;
            int    total     = ControlBridge.TotalFrames;
            string counter   = total > 0 ? $"{cur + 1} / {total}" : $"{cur + 1}";
            float  counterW  = 160f;
            float  counterX  = Screen.width - counterW - pad;
            GUI.Label(new Rect(counterX, y, counterW, btnH), counter, m_LabelStyle);

            // ── Progress bar (fills remaining space) ────────────────────────
            float progressX = x + pad * 0.5f;
            float progressW = counterX - progressX - pad;
            if (progressW > 20f)
            {
                float trackY = y + btnH * 0.38f;
                float trackH = btnH * 0.24f;
                var   bgR    = new Rect(progressX, trackY, progressW, trackH);
                GUI.DrawTexture(bgR, m_ProgressBgTex);
                if (total > 0)
                {
                    float t = Mathf.Clamp01((float)cur / total);
                    GUI.DrawTexture(new Rect(bgR.x, bgR.y, bgR.width * t, bgR.height),
                                    m_ProgressFillTex);
                }
            }
        }

        void EnsureStyles()
        {
            if (m_StylesReady) return;
            m_StylesReady = true;

            int fontSize = Mathf.Clamp(Screen.height / 42, 13, 28);

            m_BgTex           = MakeTex(new Color(0.05f, 0.05f, 0.05f, 0.82f));
            m_ProgressBgTex   = MakeTex(new Color(0.28f, 0.28f, 0.28f, 0.9f));
            m_ProgressFillTex = MakeTex(new Color(0.2f, 0.62f, 1f, 1f));

            m_BgStyle = new GUIStyle(GUI.skin.box);
            m_BgStyle.normal.background = m_BgTex;
            m_BgStyle.border = new RectOffset(0, 0, 0, 0);

            m_BtnStyle = new GUIStyle(GUI.skin.button) { fontSize = fontSize };
            m_BtnStyle.normal.textColor = Color.white;
            m_BtnStyle.hover.textColor  = new Color(0.75f, 0.9f, 1f);

            m_BtnActiveStyle = new GUIStyle(m_BtnStyle) { fontStyle = FontStyle.Bold };
            m_BtnActiveStyle.normal.textColor = new Color(0.3f, 0.78f, 1f);

            m_LabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = fontSize,
                alignment = TextAnchor.MiddleCenter,
            };
            m_LabelStyle.normal.textColor = Color.white;
        }

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }

        void OnDestroy()
        {
            if (m_BgTex)           Destroy(m_BgTex);
            if (m_ProgressBgTex)   Destroy(m_ProgressBgTex);
            if (m_ProgressFillTex) Destroy(m_ProgressFillTex);
        }
    }
}
