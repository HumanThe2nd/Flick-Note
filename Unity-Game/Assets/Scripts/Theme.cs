using UnityEngine;

/// <summary>
/// Every color in Flick Note, in one place: a dark, monochrome red theme.
/// Change these to re-skin the whole game.
/// </summary>
public static class Theme
{
    // Scene
    public static readonly Color Background = new Color(0.035f, 0.006f, 0.012f);
    public static readonly Color BackgroundPulse = new Color(0.16f, 0.02f, 0.035f);
    public static readonly Color Tunnel = new Color(0.75f, 0.06f, 0.14f);
    public static readonly Color StarA = new Color(1f, 0.45f, 0.45f, 0.6f);
    public static readonly Color StarB = new Color(0.7f, 0.12f, 0.2f, 0.6f);
    public static readonly Color Ambient = new Color(0.14f, 0.04f, 0.05f);
    public static readonly Color KeyLight = new Color(1f, 0.9f, 0.88f);
    public static readonly Color RimLight = new Color(1f, 0.18f, 0.22f);

    // Lanes: up, down, left, right. Same hue, different depth; each ring also
    // has an arrow, so lanes never rely on color alone.
    public static readonly Color[] Lanes =
    {
        new Color(1f, 0.42f, 0.4f),     // up: coral red
        new Color(0.95f, 0.1f, 0.18f),  // down: crimson
        new Color(1f, 0.72f, 0.68f),    // left: pale rose
        new Color(0.78f, 0.04f, 0.12f), // right: deep wine
    };

    // Accents
    public static readonly Color Accent = new Color(1f, 0.2f, 0.26f);       // main bright red
    public static readonly Color AccentLight = new Color(1f, 0.6f, 0.58f);  // light red
    public static readonly Color AccentDeep = new Color(0.55f, 0.03f, 0.08f);
    public static readonly Color Beam = new Color(1f, 0.55f, 0.55f);

    // Text and panels
    public static readonly Color Text = new Color(1f, 0.93f, 0.92f);
    public static readonly Color TextSoft = new Color(1f, 0.9f, 0.9f, 0.85f);
    public static readonly Color TextDim = new Color(1f, 0.78f, 0.78f, 0.5f);
    public static readonly Color Panel = new Color(0.08f, 0.012f, 0.02f, 0.9f);
    public static readonly Color PanelSolid = new Color(0.06f, 0.008f, 0.015f, 0.95f);
    public static readonly Color Track = new Color(1f, 0.3f, 0.3f, 0.12f);

    // Judgements (brightest = best)
    public static readonly Color Perfect = new Color(1f, 0.82f, 0.78f);
    public static readonly Color Good = new Color(1f, 0.4f, 0.4f);
    public static readonly Color Miss = new Color(0.55f, 0.14f, 0.18f);
    public static readonly Color MissFlash = new Color(0.45f, 0.02f, 0.05f);

    // Glove model
    public static readonly Color Glove = new Color(0.11f, 0.075f, 0.08f);
    public static readonly Color GloveTrim = new Color(0.2f, 0.05f, 0.07f);
    public static readonly Color GloveGlow = new Color(1f, 0.16f, 0.22f);
    public static readonly Color Sleeve = new Color(0.07f, 0.04f, 0.05f);

    public static Color Grade(string grade)
    {
        switch (grade)
        {
            case "S": return new Color(1f, 0.86f, 0.82f);
            case "A": return new Color(1f, 0.52f, 0.48f);
            case "B": return new Color(1f, 0.28f, 0.3f);
            case "C": return new Color(0.78f, 0.14f, 0.2f);
            default: return new Color(0.5f, 0.1f, 0.14f);
        }
    }

    public static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
}
