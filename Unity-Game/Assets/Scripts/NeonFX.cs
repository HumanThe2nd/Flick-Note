using UnityEngine;

/// <summary>
/// Small helpers for the neon look: glowing line circles/frames, soft particle
/// sprites and rounded HUD panels, all generated in code.
/// </summary>
public static class NeonFX
{
    /// <summary>A LineRenderer drawing a circle (closed loop). Update it with <see cref="SetCircle"/>.</summary>
    public static LineRenderer Circle(Transform parent, string name, Material mat, Color color, float width, int segments = 64)
    {
        var lr = Line(parent, name, mat, color, width, segments);
        lr.loop = true;
        return lr;
    }

    public static void SetCircle(LineRenderer lr, Vector3 center, Vector3 normal, float radius)
    {
        Vector3 a = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 b = Vector3.Cross(normal, a).normalized;
        int n = lr.positionCount;
        for (int i = 0; i < n; i++)
        {
            float t = i * Mathf.PI * 2f / n;
            lr.SetPosition(i, center + (a * Mathf.Cos(t) + b * Mathf.Sin(t)) * radius);
        }
    }

    /// <summary>Set a 4-point looped LineRenderer to a width x height rectangle with the given rotation.</summary>
    public static void SetFrame(LineRenderer lr, Vector3 center, Quaternion rotation, float width, float height)
    {
        Vector3 r = rotation * Vector3.right * (width / 2f), u = rotation * Vector3.up * (height / 2f);
        lr.SetPosition(0, center - r - u);
        lr.SetPosition(1, center + r - u);
        lr.SetPosition(2, center + r + u);
        lr.SetPosition(3, center - r + u);
    }

    public static LineRenderer Line(Transform parent, string name, Material mat, Color color, float width, int points = 2)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = mat;
        lr.useWorldSpace = true;
        lr.positionCount = points;
        lr.widthMultiplier = width;
        lr.startColor = lr.endColor = color;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    /// <summary>A soft round dot (white, alpha falls off to the edge), for particles.</summary>
    public static Texture2D SoftDot(int size = 64)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float c = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                float a = Mathf.Clamp01(1f - d);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        tex.Apply();
        return tex;
    }

    /// <summary>White rounded rectangle for GUI panels (use as a 9-sliced GUIStyle background).</summary>
    public static Texture2D RoundedRect(int size = 32, int radius = 10)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(radius - x, x - (size - 1 - radius)));
                float dy = Mathf.Max(0, Mathf.Max(radius - y, y - (size - 1 - radius)));
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f)));
            }
        tex.Apply();
        return tex;
    }

    /// <summary>A particle system for one-shot bursts; call Emit to fire it.</summary>
    public static ParticleSystem Burst(Transform parent, string name, Material particleMat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 400;
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.05f;
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = particleMat;
        return ps;
    }
}
