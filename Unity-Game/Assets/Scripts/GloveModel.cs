using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The virtual glove: a right hand built from smooth primitives (rounded palm,
/// jointed fingers, thumb, cuff, sensor board), dark glossy with glowing red
/// fingertips, cuff and LED. Fingers curl into a quick fist on each strike.
///
/// Local axes: +Z toward the fingers, +Y out of the back of the hand, thumb on -X.
/// </summary>
public class GloveModel : MonoBehaviour
{
    private class Joint
    {
        public Transform pivot;
        public float rest;      // relaxed curl (degrees)
        public float fist;      // extra curl at full fist
        public Vector3 baseEuler;
    }

    private readonly List<Joint> _joints = new List<Joint>();
    private readonly List<Material> _glowMats = new List<Material>();
    private Material _glove, _trim, _glowTemplate;
    private float _strikeTime = -10f;

    public void Build(Material baseMaterial, Material accentMaterial)
    {
        _glove = Lit(baseMaterial, Theme.Glove, 0.78f);
        _trim = Lit(baseMaterial, Theme.GloveTrim, 0.55f);
        _glowTemplate = accentMaterial != null ? accentMaterial : baseMaterial;

        // Palm and back of hand
        Part(PrimitiveType.Sphere, "Palm", transform, new Vector3(0f, 0f, 0.004f), Vector3.zero,
             new Vector3(0.09f, 0.034f, 0.1f), _glove);
        Part(PrimitiveType.Capsule, "KnuckleRidge", transform, new Vector3(0f, 0.004f, 0.04f), new Vector3(0f, 0f, 90f),
             new Vector3(0.024f, 0.04f, 0.024f), _glove);

        // Wrist, then a cuff around it: a padded dark ring with a thin glowing ring at its front edge
        // Forearm sleeve running back toward the camera, so the glove reads as an arm reaching out
        var sleeve = Lit(_trim, Theme.Sleeve, 0.45f);
        Part(PrimitiveType.Capsule, "Sleeve", transform, new Vector3(0f, -0.004f, -0.16f), new Vector3(90f, 0f, 0f),
             new Vector3(0.064f, 0.13f, 0.054f), sleeve);
        Part(PrimitiveType.Sphere, "Wrist", transform, new Vector3(0f, -0.002f, -0.036f), Vector3.zero,
             new Vector3(0.066f, 0.048f, 0.05f), _glove);
        Ring("Cuff", new Vector3(0f, -0.002f, -0.05f), 0.031f, 0.009f, new Vector3(1.15f, 0.85f, 1f), _trim);
        Ring("CuffGlow", new Vector3(0f, -0.002f, -0.041f), 0.034f, 0.003f, new Vector3(1.15f, 0.85f, 1f), Glow(1f));

        // Sensor board on the back of the hand, with a status LED
        var board = Lit(_trim, Theme.Glove, 0.2f);
        Part(PrimitiveType.Cube, "SensorBoard", transform, new Vector3(0f, 0.019f, -0.004f), Vector3.zero,
             new Vector3(0.048f, 0.007f, 0.032f), board);
        Part(PrimitiveType.Sphere, "LED", transform, new Vector3(0.015f, 0.0235f, 0.008f), Vector3.zero,
             Vector3.one * 0.006f, Glow(1f));

        // Fingers: index, middle, ring, pinky (index nearest the thumb)
        Finger("Index", new Vector3(-0.029f, 0f, 0.05f), -5f, 0.0105f, new[] { 0.036f, 0.023f, 0.018f });
        Finger("Middle", new Vector3(-0.0095f, 0.001f, 0.053f), 0f, 0.0110f, new[] { 0.040f, 0.027f, 0.020f });
        Finger("Ring", new Vector3(0.0105f, 0f, 0.05f), 4f, 0.0105f, new[] { 0.038f, 0.025f, 0.019f });
        Finger("Pinky", new Vector3(0.029f, -0.003f, 0.044f), 9f, 0.0092f, new[] { 0.029f, 0.020f, 0.016f });

        // Thumb: sticks out sideways and forward from the base of the palm
        var thumbBase = Pivot("Thumb", transform, new Vector3(-0.04f, -0.008f, -0.004f), new Vector3(12f, -52f, -20f), 0f, 25f);
        Segments(thumbBase, 0.0122f, new[] { 0.032f, 0.025f }, new[] { 12f, 14f }, new[] { 25f, 35f });
    }

    // A torus around the wrist (the ring lies in the XY plane, around the Z axis).
    private void Ring(string name, Vector3 pos, float radius, float tube, Vector3 scale, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = Torus(radius, tube, 40, 12);
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    private static Mesh Torus(float radius, float tube, int segments, int sides)
    {
        var verts = new Vector3[(segments + 1) * (sides + 1)];
        var normals = new Vector3[verts.Length];
        var tris = new int[segments * sides * 6];
        for (int i = 0; i <= segments; i++)
        {
            float a = i * 2f * Mathf.PI / segments;
            var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            for (int j = 0; j <= sides; j++)
            {
                float b = j * 2f * Mathf.PI / sides;
                Vector3 n = dir * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                int k = i * (sides + 1) + j;
                verts[k] = dir * radius + n * tube;
                normals[k] = n;
            }
        }
        int t = 0;
        for (int i = 0; i < segments; i++)
            for (int j = 0; j < sides; j++)
            {
                int a0 = i * (sides + 1) + j, a1 = a0 + 1, b0 = a0 + sides + 1, b1 = b0 + 1;
                tris[t++] = a0; tris[t++] = b0; tris[t++] = a1;
                tris[t++] = a1; tris[t++] = b0; tris[t++] = b1;
            }
        var mesh = new Mesh { name = "Torus", vertices = verts, normals = normals, triangles = tris };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Quick fist-and-release, played when a strike is detected.</summary>
    public void Strike() => _strikeTime = Time.unscaledTime;

    private void Update()
    {
        float t = Time.unscaledTime - _strikeTime;
        // Close fast (60 ms), open over 220 ms.
        float curl = t < 0.06f ? t / 0.06f : Mathf.Clamp01(1f - (t - 0.06f) / 0.22f);
        curl = curl * curl * (3f - 2f * curl);
        foreach (var j in _joints)
            j.pivot.localRotation = Quaternion.Euler(j.baseEuler.x + j.rest + j.fist * curl, j.baseEuler.y, j.baseEuler.z);

        // Glow: gentle breathing, flaring on strikes.
        float breathe = 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 2.2f);
        float flare = Mathf.Clamp01(1f - t / 0.35f);
        Color c = Color.Lerp(Theme.GloveGlow * breathe, Theme.Perfect, flare * 0.8f);
        foreach (var m in _glowMats) m.color = c;
    }

    // ---- construction helpers ----------------------------------------------------

    private void Finger(string name, Vector3 basePos, float splay, float radius, float[] lengths)
    {
        var root = Pivot(name, transform, basePos, new Vector3(0f, splay, 0f), 8f, 70f);
        Segments(root, radius, lengths, new[] { 10f, 8f }, new[] { 85f, 55f });
    }

    // Builds finger segments from a root pivot. rest/fist arrays are for the
    // joints after the first segment.
    private void Segments(Transform root, float radius, float[] lengths, float[] rest, float[] fist)
    {
        Transform pivot = root;
        for (int k = 0; k < lengths.Length; k++)
        {
            float len = lengths[k];
            float r = radius * (1f - 0.08f * k);
            Part(PrimitiveType.Sphere, "Knuckle", pivot, Vector3.zero, Vector3.zero, Vector3.one * r * 2.15f, _glove);
            Part(PrimitiveType.Capsule, "Segment", pivot, new Vector3(0f, 0f, len / 2f), new Vector3(90f, 0f, 0f),
                 new Vector3(r * 2f, len / 2f + r, r * 2f), _glove);
            if (k == lengths.Length - 1)
            {
                // Glowing fingertip
                Part(PrimitiveType.Sphere, "Tip", pivot, new Vector3(0f, 0.002f, len + r * 0.4f), Vector3.zero,
                     Vector3.one * r * 1.5f, Glow(1f));
            }
            else
            {
                pivot = Pivot("Joint", pivot, new Vector3(0f, 0f, len), Vector3.zero, rest[k], fist[k]);
            }
        }
    }

    private Transform Pivot(string name, Transform parent, Vector3 pos, Vector3 euler, float rest, float fist)
    {
        var p = new GameObject(name).transform;
        p.SetParent(parent, false);
        p.localPosition = pos;
        p.localRotation = Quaternion.Euler(euler.x + rest, euler.y, euler.z);
        _joints.Add(new Joint { pivot = p, rest = rest, fist = fist, baseEuler = euler });
        return p;
    }

    private static Transform Part(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 euler,
                                  Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    private static Material Lit(Material template, Color color, float gloss)
    {
        var m = new Material(template) { color = color };
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.1f);
        return m;
    }

    private Material Glow(float intensity)
    {
        var m = new Material(_glowTemplate) { color = Theme.GloveGlow * intensity };
        _glowMats.Add(m);
        return m;
    }
}
