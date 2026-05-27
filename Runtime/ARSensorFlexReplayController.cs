using UnityEngine;

namespace SensorFlex.Player
{
    /// <summary>
    /// Scene component that renders a replay control bar using IMGUI.
    /// Add to any scene object — no Canvas or EventSystem required.
    ///
    /// Bar layout (top → bottom):
    ///   Row 0 — Info:      [frame counter]  [progress bar ─────────────── ]
    ///   Row 1 — Transport: [Restart] [Play/Pause] [Step]  [0.25x] [0.5x] [1x] [2x] [4x]
    ///
    /// When the screen is too narrow to fit all buttons at minimum width, the
    /// transport and speed groups each expand to fill their own row:
    ///   Row 1 — Transport: [  Restart  ] [ Play/Pause ] [   Step   ]
    ///   Row 2 — Speed:     [ 0.25x ] [ 0.5x ] [  1x  ] [  2x  ] [  4x  ]
    ///
    /// Buttons fill the available width evenly in every layout mode.
    /// </summary>
    [AddComponentMenu("XR/SensorFlex/AR SensorFlex Replay Controller")]
    public sealed class ARSensorFlexReplayController : MonoBehaviour
    {
        [SerializeField] bool m_ShowUI = true;

        // ── Layout constants ──────────────────────────────────────────────────
        const float k_MinBtnW   = 44f;  // below this per-button width, switch to two rows
        const float k_MinBtnH   = 36f;
        const float k_InfoH     = 26f;  // height of the info / progress strip
        const float k_RowPad    = 5f;   // vertical gap between rows and bar edges
        const float k_BtnPad    = 4f;   // horizontal gap between buttons

        static readonly float[] k_SpeedPresets = { 0.25f, 0.5f, 1f, 2f, 4f };
        const int k_CtrlCount  = 4;  // Restart  Play/Pause  Step  Color/Depth
        const int k_SpeedCount = 5;

        // ── Cached GUI resources ──────────────────────────────────────────────
        GUIStyle  m_BgStyle;
        GUIStyle  m_BtnStyle;
        GUIStyle  m_BtnActiveStyle;
        GUIStyle  m_InfoLabelStyle;
        Texture2D m_BgTex;
        Texture2D m_ProgressBgTex;
        Texture2D m_ProgressFillTex;
        int       m_CachedW, m_CachedH;

        // ── Public API ────────────────────────────────────────────────────────
        /// <summary>Show or hide the control bar at runtime.</summary>
        public bool ShowUI { get => m_ShowUI; set => m_ShowUI = value; }

        // ── Unity callbacks ───────────────────────────────────────────────────
        void OnGUI()
        {
            if (!m_ShowUI) return;
            EnsureStyles();
            DrawBar();
        }

        void OnDestroy()
        {
            DestroyTextures();
        }

        // ── Drawing ───────────────────────────────────────────────────────────
        void DrawBar()
        {
            float sidePad = Mathf.Max(6f, Screen.width * 0.008f);
            float btnH    = Mathf.Max(k_MinBtnH, Screen.height * 0.052f);
            float innerW  = Screen.width - 2f * sidePad;

            // Decide single-row vs two-row for the button section.
            float singleBtnW  = (innerW - (k_CtrlCount + k_SpeedCount - 1) * k_BtnPad)
                                 / (k_CtrlCount + k_SpeedCount);
            bool  twoRows     = singleBtnW < k_MinBtnW;

            float ctrlBtnW  = twoRows
                ? (innerW - (k_CtrlCount  - 1) * k_BtnPad) / k_CtrlCount
                : singleBtnW;
            float speedBtnW = twoRows
                ? (innerW - (k_SpeedCount - 1) * k_BtnPad) / k_SpeedCount
                : singleBtnW;

            int   btnRows = twoRows ? 2 : 1;
            float barH    = k_RowPad + k_InfoH + k_RowPad + btnRows * (btnH + k_RowPad);
            float barY    = Screen.height - barH;

            GUI.Box(new Rect(0f, barY, Screen.width, barH), GUIContent.none, m_BgStyle);

            float y = barY + k_RowPad;

            // ── Info row: frame counter + progress bar ────────────────────────
            DrawInfoRow(sidePad, y, innerW);
            y += k_InfoH + k_RowPad;

            // ── Transport buttons: ↺  ⏸/▶  ⏭ ──────────────────────────────
            DrawTransportRow(sidePad, y, ctrlBtnW, btnH);

            // ── Speed buttons (same row or next row) ─────────────────────────
            float speedX = twoRows ? sidePad : sidePad + k_CtrlCount * (singleBtnW + k_BtnPad);
            float speedY = twoRows ? y + btnH + k_RowPad : y;
            DrawSpeedRow(speedX, speedY, speedBtnW, btnH);
        }

        void DrawInfoRow(float x, float y, float w)
        {
            int    cur   = ControlBridge.CurrentFrame;
            int    total = ControlBridge.TotalFrames;
            string lbl   = total > 0 ? $"{cur + 1} / {total}" : $"Frame {cur + 1}";
            float  lblW  = Mathf.Min(150f, w * 0.22f);

            GUI.Label(new Rect(x, y, lblW, k_InfoH), lbl, m_InfoLabelStyle);

            float px = x + lblW + k_BtnPad * 2f;
            float pw = w - lblW - k_BtnPad * 2f;
            float py = y + k_InfoH * 0.3f;
            float ph = k_InfoH * 0.4f;

            GUI.DrawTexture(new Rect(px, py, pw, ph), m_ProgressBgTex);
            if (total > 0)
            {
                float t = Mathf.Clamp01((float)cur / total);
                GUI.DrawTexture(new Rect(px, py, pw * t, ph), m_ProgressFillTex);
            }
        }

        void DrawTransportRow(float x, float y, float bw, float bh)
        {
            // Restart
            if (GUI.Button(new Rect(x, y, bw, bh), "Restart", m_BtnStyle))
                ControlBridge.Restart();
            x += bw + k_BtnPad;

            // Play / Pause
            string playLbl = ControlBridge.IsPlaying ? "Pause" : "Play";
            if (GUI.Button(new Rect(x, y, bw, bh), playLbl, m_BtnStyle))
                ControlBridge.TogglePlay();
            x += bw + k_BtnPad;

            // Step forward (disabled while playing)
            bool prev = GUI.enabled;
            GUI.enabled = !ControlBridge.IsPlaying;
            if (GUI.Button(new Rect(x, y, bw, bh), "Step", m_BtnStyle))
                ControlBridge.StepForward();
            GUI.enabled = prev;
            x += bw + k_BtnPad;

            // Depth / Color toggle
            string viewLbl = ControlBridge.DepthVisualizationEnabled ? "Color" : "Depth";
            GUIStyle viewStyle = ControlBridge.DepthVisualizationEnabled ? m_BtnActiveStyle : m_BtnStyle;
            if (GUI.Button(new Rect(x, y, bw, bh), viewLbl, viewStyle))
                ControlBridge.ToggleDepthVisualization();
        }

        void DrawSpeedRow(float x, float y, float bw, float bh)
        {
            foreach (float speed in k_SpeedPresets)
            {
                bool   active = Mathf.Approximately(ControlBridge.PlaybackSpeed, speed);
                string label  = $"{speed:0.##}x";  // e.g. "0.5x"
                if (GUI.Button(new Rect(x, y, bw, bh), label,
                               active ? m_BtnActiveStyle : m_BtnStyle))
                    ControlBridge.SetSpeed(speed);
                x += bw + k_BtnPad;
            }
        }

        // ── Style management ──────────────────────────────────────────────────
        void EnsureStyles()
        {
            if (m_BgTex != null && Screen.width == m_CachedW && Screen.height == m_CachedH)
                return;

            DestroyTextures();
            m_CachedW = Screen.width;
            m_CachedH = Screen.height;

            float btnH    = Mathf.Max(k_MinBtnH, Screen.height * 0.052f);
            int   btnFont = Mathf.Clamp((int)(btnH * 0.42f), 12, 24);
            int   infoFont = Mathf.Clamp((int)(k_InfoH * 0.62f), 11, 16);

            m_BgTex           = MakeTex(new Color(0.05f, 0.05f, 0.05f, 0.82f));
            m_ProgressBgTex   = MakeTex(new Color(0.28f, 0.28f, 0.28f, 0.9f));
            m_ProgressFillTex = MakeTex(new Color(0.2f, 0.62f, 1f, 1f));

            m_BgStyle = new GUIStyle(GUI.skin.box);
            m_BgStyle.normal.background = m_BgTex;
            m_BgStyle.border = new RectOffset(0, 0, 0, 0);

            m_BtnStyle = new GUIStyle(GUI.skin.button) { fontSize = btnFont };
            m_BtnStyle.normal.textColor = Color.white;
            m_BtnStyle.hover.textColor  = new Color(0.75f, 0.9f, 1f);

            m_BtnActiveStyle = new GUIStyle(m_BtnStyle) { fontStyle = FontStyle.Bold };
            m_BtnActiveStyle.normal.textColor = new Color(0.3f, 0.78f, 1f);

            m_InfoLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = infoFont,
                alignment = TextAnchor.MiddleLeft,
            };
            m_InfoLabelStyle.normal.textColor = Color.white;
        }

        void DestroyTextures()
        {
            if (m_BgTex)           { Destroy(m_BgTex);           m_BgTex           = null; }
            if (m_ProgressBgTex)   { Destroy(m_ProgressBgTex);   m_ProgressBgTex   = null; }
            if (m_ProgressFillTex) { Destroy(m_ProgressFillTex); m_ProgressFillTex = null; }
        }

        static Texture2D MakeTex(Color col)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, col);
            t.Apply();
            return t;
        }
    }
}
