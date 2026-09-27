using UnityEngine;

/// <summary>
/// Flick Note's on-screen HUD (Unity IMGUI): status pill, score card, progress
/// bar, judgement pop-ups, combo counter, count-in, menu and results screens.
/// Everything scales with the window height (designed at 720p).
/// </summary>
public partial class RhythmGame
{
    // Theme shorthands (see Theme.cs)
    private static Color Cyan => Theme.AccentLight;   // light red
    private static Color Pink => Theme.Accent;        // bright red
    private static Color Dim => Theme.TextDim;

    private string _popText = "";
    private Color _popColor = Color.white;
    private float _popTime = -10f;
    private float _comboBumpTime = -10f;

    private bool _stylesReady;
    private float _styleScale;
    private GUIStyle _panel, _bar, _dot;
    private Font _font;
    // One GUIStyle per (size, alignment): IMGUI caches text layout per style, and
    // changing a shared style's font/size between labels can make it reuse the
    // wrong text. Only the text color is changed on a cached style.
    private readonly System.Collections.Generic.Dictionary<int, GUIStyle> _textStyles =
        new System.Collections.Generic.Dictionary<int, GUIStyle>();

    private void Pop(string text, Color color)
    {
        _popText = text;
        _popColor = color;
        _popTime = Time.unscaledTime;
    }

    private void ClearPop() => _popText = "";

    // ---- drawing helpers ----------------------------------------------------------

    private float S => Screen.height / 720f;

    private void EnsureStyles()
    {
        if (_stylesReady && Mathf.Approximately(_styleScale, S)) return;
        _styleScale = S;
        _font = _font != null ? _font : Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial" }, 32);
        _panel = new GUIStyle { normal = { background = NeonFX.RoundedRect(32, 12) }, border = new RectOffset(14, 14, 14, 14) };
        _bar = new GUIStyle { normal = { background = NeonFX.RoundedRect(12, 3) }, border = new RectOffset(4, 4, 4, 4) };
        _dot = new GUIStyle { normal = { background = NeonFX.RoundedRect(16, 8) } };
        _textStyles.Clear();
        PrewarmFont();
        _stylesReady = true;
    }

    // Unity's dynamic fonts render glyphs into a texture on demand. If that
    // texture is rebuilt partway through drawing, text already drawn that frame
    // turns to garbage. So: every size used is fixed (animation uses scaling,
    // not font size), and all glyphs are requested up front.
    private static readonly float[] TextSizes = { 11, 11.5f, 12, 13, 14, 15, 18, 20, 22, 30, 34, 40, 44, 46, 64, 90, 150 };

    private void PrewarmFont()
    {
        const string chars = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~·…●◆◯▲";
        foreach (float sz in TextSizes)
            _font.RequestCharactersInTexture(sz >= 64 ? "FLICKNOTE GO!0123456789SABCD" : chars, Mathf.RoundToInt(sz * S), FontStyle.Bold);
    }

    private GUIStyle TextStyle(float size, TextAnchor anchor)
    {
        int px = Mathf.Max(1, Mathf.RoundToInt(size * S));
        int key = px * 16 + (int)anchor;
        if (!_textStyles.TryGetValue(key, out var st))
        {
            st = new GUIStyle(GUI.skin.label)
            {
                font = _font, fontSize = px, fontStyle = FontStyle.Bold, alignment = anchor,
                richText = false, wordWrap = false, clipping = TextClipping.Overflow,
            };
            _textStyles[key] = st;
        }
        return st;
    }

    private void Panel(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.Box(r, GUIContent.none, _panel);
        GUI.color = old;
    }

    // Thin rounded bar (the big panel style can't be drawn thinner than its corners).
    private void Bar(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.Box(r, GUIContent.none, _bar);
        GUI.color = old;
    }

    private void Dot(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.Box(r, GUIContent.none, _dot);
        GUI.color = old;
    }

    private void Text(Rect r, string text, float size, Color color, TextAnchor anchor = TextAnchor.UpperLeft,
                      bool heavy = false, bool shadow = true, float scale = 1f)
    {
        var oldMatrix = GUI.matrix;
        if (!Mathf.Approximately(scale, 1f)) GUIUtility.ScaleAroundPivot(Vector2.one * scale, r.center);
        // "heavy" text is the same bold font; big sizes do the emphasis.
        var st = TextStyle(size, anchor);
        if (shadow)
        {
            st.normal.textColor = new Color(0f, 0f, 0f, 0.55f * color.a);
            GUI.Label(new Rect(r.x + 2 * S, r.y + 2 * S, r.width, r.height), text, st);
        }
        st.normal.textColor = color;
        GUI.Label(r, text, st);
        GUI.matrix = oldMatrix;
    }

    // Neon title: a few soft colored offsets behind white text.
    private void NeonText(Rect r, string text, float size, Color glow, float scale = 1f)
    {
        for (int k = 0; k < 6; k++)
        {
            float a = k * Mathf.PI / 3f;
            var o = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 3f * S;
            Text(new Rect(r.x + o.x, r.y + o.y, r.width, r.height), text, size, new Color(glow.r, glow.g, glow.b, 0.25f * glow.a),
                 TextAnchor.MiddleCenter, true, false, scale);
        }
        Text(r, text, size, Theme.WithAlpha(Theme.Text, glow.a), TextAnchor.MiddleCenter, true, false, scale);
    }

    private Rect R(float x, float y, float w, float h) => new Rect(x * S, y * S, w * S, h * S);
    private Rect Centered(float y, float w, float h) => new Rect((Screen.width - w * S) / 2f, y * S, w * S, h * S);

    // ---- HUD ------------------------------------------------------------------------

    private void OnGUI()
    {
        if (_hideHud) return;
        EnsureStyles();
        DrawStatus();
        if (_state == State.Playing)
        {
            DrawScoreCard();
            DrawProgress();
            DrawCombo();
            DrawCountIn();
            DrawPop();
        }
        else if (_state == State.Menu) DrawMenu();
        else DrawResults();
    }

    private void DrawStatus()
    {
        string label;
        Color dot;
        if (glove != null && glove.Connected) { label = $"GLOVE CONNECTED  ·  {glove.PacketsPerSecond}/s"; dot = Theme.Accent; }
        else if (glove != null && glove.Status.StartsWith("glove found")) { label = "GLOVE CALIBRATING: HOLD STILL"; dot = Theme.AccentLight; }
        else if (glove != null && glove.Status.StartsWith("bridge running")) { label = "LOOKING FOR GLOVE…  (keyboard: arrows + Space)"; dot = Theme.AccentDeep; }
        else { label = "NO BRIDGE: KEYBOARD MODE  (arrows aim, Space flicks)"; dot = Theme.Miss; }

        float w = 26 + label.Length * 7.6f;
        Panel(R(14, 14, w, 34), Theme.Panel);
        Dot(R(26, 26, 10, 10), dot);
        Text(R(44, 14, w, 34), label, 13, Theme.Text, TextAnchor.MiddleLeft);
        Text(R(18, 52, 600, 20), $"R  re-center     [ ]  timing {latencyOffset * 1000f:+0;-0;0} ms     Esc  quit",
             11.5f, Dim, TextAnchor.MiddleLeft);
    }

    private void DrawScoreCard()
    {
        float w = 230, x = Screen.width / S - w - 14;
        Panel(R(x, 14, w, 92), Theme.Panel);
        Text(R(x + 16, 20, w, 18), "SCORE", 11, Dim);
        Text(R(x + 14, 32, w - 28, 40), $"{_score:N0}", 30, Theme.Text, TextAnchor.MiddleLeft, true);
        Text(R(x + 16, 74, w, 22), $"{Accuracy:F1}%", 14, Cyan, TextAnchor.MiddleLeft);
        int m = Multiplier;
        Color mc = m >= 4 ? Theme.Perfect : m == 3 ? Theme.AccentLight : m == 2 ? Theme.Accent : Dim;
        Bar(R(x + w - 62, 72, 48, 24), new Color(mc.r, mc.g, mc.b, 0.25f));
        Text(R(x + w - 62, 72, 48, 24), $"x{m}", 14, mc, TextAnchor.MiddleCenter);
    }

    private void DrawProgress()
    {
        float w = 420, y = 18;
        var bg = Centered(y, w, 6);
        Bar(bg, Theme.Track);
        float p = Mathf.Clamp01(_songTime / Mathf.Max(0.1f, SongLength));
        if (p > 0.005f)
            Bar(new Rect(bg.x, bg.y, Mathf.Max(bg.height, bg.width * p), bg.height), Color.Lerp(Theme.AccentDeep, Theme.AccentLight, p));
    }

    private void DrawCombo()
    {
        if (_combo < 3) return;
        float bump = Mathf.Exp(-(Time.unscaledTime - _comboBumpTime) * 12f);
        Color c = Color.Lerp(Theme.Text, Multiplier >= 4 ? Theme.Perfect : Theme.Accent, 0.35f + 0.65f * bump);
        float x = Screen.width / S * 0.035f, y = 720 * 0.44f;
        Text(R(x, y, 120, 60), _combo.ToString(), 46, c, TextAnchor.MiddleCenter, true, true, 1f + 0.25f * bump);
        Text(R(x, y + 56, 120, 20), "COMBO", 12, Dim, TextAnchor.MiddleCenter);
    }

    private void DrawCountIn()
    {
        float b = Beat;
        if (b < 0f || b >= CountInBeats + 0.7f) return;
        float phase = Mathf.Repeat(b, 1f);
        if (b < CountInBeats - 4)
        {
            Text(Centered(720 * 0.38f, 800, 60), "GET READY", 40, Theme.Text, TextAnchor.MiddleCenter, true);
            Text(Centered(720 * 0.38f + 58, 800, 30), "point at pale notes  ·  flick red notes toward the arrow", 15, Dim, TextAnchor.MiddleCenter);
            return;
        }
        string t = b >= CountInBeats ? "GO!" : (CountInBeats - Mathf.FloorToInt(b)).ToString();
        Color c = b >= CountInBeats ? Theme.Perfect : Theme.Accent;
        c.a = 1f - phase * 0.8f;
        NeonText(Centered(720 * 0.34f, 800, 120), t, 90, c, 1.25f - 0.25f * Mathf.Clamp01(phase * 4f));
    }

    private void DrawPop()
    {
        float age = Time.unscaledTime - _popTime;
        if (_popText == "" || age > 0.6f) return;
        Color c = _popColor;
        c.a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.6f, age));
        Text(Centered(720 * 0.60f - 10 * (1f - c.a), 600, 70), _popText, 44, c, TextAnchor.MiddleCenter, true, true,
             1f + 0.45f * Mathf.Exp(-age * 18f));
    }

    private void DrawMenu()
    {
        var box = Centered(150, 640, 380);
        Panel(new Rect(box.x - 2 * S, box.y - 2 * S, box.width + 4 * S, box.height + 4 * S), Theme.WithAlpha(Theme.Accent, 0.45f));
        Panel(box, Theme.PanelSolid);
        float top = 150;
        NeonText(Centered(top + 22, 640, 80), "FLICK NOTE", 64, Theme.Accent);
        Text(Centered(top + 92, 640, 24), "a rhythm game for your IMU glove", 14, Dim, TextAnchor.MiddleCenter);

        float x = (Screen.width / S - 640) / 2f + 70, y = top + 138;
        MenuRow(x, y, "◆", Theme.AccentLight, "NOTE", "point at its ring as it arrives (follow the laser)");
        MenuRow(x, y + 36, "▲", Theme.Accent, "FLICK NOTE", "flick your wrist toward the arrow, from anywhere");
        MenuRow(x, y + 72, "◯", Theme.Perfect, "TIMING", "hit when the shrinking circle meets the ring");
        MenuRow(x, y + 108, "R", Theme.Text, "RE-CENTER", "point straight ahead and press R");

        float blink = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 4f);
        Text(Centered(top + 300, 640, 36), "FLICK TO START", 22, Theme.WithAlpha(Theme.Accent, blink),
             TextAnchor.MiddleCenter, true);
        Text(Centered(top + 334, 640, 20), "or press Enter", 12, Dim, TextAnchor.MiddleCenter);
    }

    private void MenuRow(float x, float y, string icon, Color iconColor, string title, string text)
    {
        Text(R(x, y, 30, 30), icon, 20, iconColor, TextAnchor.MiddleCenter);
        Text(R(x + 40, y, 120, 30), title, 14, iconColor, TextAnchor.MiddleLeft);
        Text(R(x + 150, y, 420, 30), text, 14, Theme.TextSoft, TextAnchor.MiddleLeft);
    }

    private void DrawResults()
    {
        var box = Centered(130, 640, 420);
        Panel(new Rect(box.x - 2 * S, box.y - 2 * S, box.width + 4 * S, box.height + 4 * S), Theme.WithAlpha(Theme.Accent, 0.45f));
        Panel(box, Theme.PanelSolid);
        float top = 130;
        Text(Centered(top + 18, 640, 30), "RESULTS", 18, Dim, TextAnchor.MiddleCenter, true);

        float acc = Accuracy;
        string grade = acc >= 95 ? "S" : acc >= 90 ? "A" : acc >= 80 ? "B" : acc >= 70 ? "C" : "D";
        Color gc = Theme.Grade(grade);
        float left = (Screen.width / S - 640) / 2f;
        NeonText(R(left + 30, top + 60, 200, 200), grade, 150, gc);

        float x = left + 260, y = top + 70;
        Text(R(x, y, 340, 20), "SCORE", 11, Dim);
        Text(R(x, y + 12, 340, 44), $"{_score:N0}", 34, Theme.Text, TextAnchor.MiddleLeft, true);
        Text(R(x, y + 62, 160, 20), "ACCURACY", 11, Dim);
        Text(R(x + 170, y + 62, 160, 20), "MAX COMBO", 11, Dim);
        Text(R(x, y + 76, 160, 30), $"{acc:F1}%", 22, Cyan, TextAnchor.MiddleLeft);
        Text(R(x + 170, y + 76, 160, 30), _maxCombo.ToString(), 22, Pink, TextAnchor.MiddleLeft);

        int total = Mathf.Max(1, _perfects + _goods + _misses);
        StatBar(x, y + 124, "PERFECT", _perfects, total, Theme.Perfect);
        StatBar(x, y + 152, "GOOD", _goods, total, Theme.Good);
        StatBar(x, y + 180, "MISS", _misses, total, Theme.Miss);

        bool ready = Time.unscaledTime - _resultsSince > 2f;
        float blink = ready ? 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 4f) : 0.25f;
        Text(Centered(top + 370, 640, 30), "FLICK TO PLAY AGAIN", 18, Theme.WithAlpha(Theme.Accent, blink),
             TextAnchor.MiddleCenter, true);
    }

    private void StatBar(float x, float y, string label, int count, int total, Color c)
    {
        Text(R(x, y, 80, 20), label, 11, c, TextAnchor.MiddleLeft);
        Bar(R(x + 80, y + 6, 200, 8), Theme.Track);
        float frac = (float)count / total;
        if (frac > 0f) Bar(R(x + 80, y + 6, Mathf.Max(8, 200 * frac), 8), c);
        Text(R(x + 290, y, 50, 20), count.ToString(), 12, Theme.Text, TextAnchor.MiddleLeft);
    }
}
