using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flick Note's 3D look: neon rings, approach circles, glowing notes with trails,
/// hit bursts, an aim beam, a beat-synced tunnel and a starfield.
/// Glow comes from additive blending (no post-processing needed).
/// </summary>
public partial class RhythmGame
{
    private const float RingRadius = 0.28f;

    private Material _glowLine;      // additive, untextured: lines and trails
    private Material _glowSoft;      // additive, soft dot texture: halos and particles
    private Material _unlit;

    private LineRenderer[] _ringOuter, _ringInner, _approach, _rails;
    private Transform[] _ringHalo;
    private float[] _laneFlashTime;
    private Color[] _laneFlashColor;
    private LineRenderer _beam, _reticleRing;
    private Transform _reticleHalo;
    private ParticleSystem _burst;
    private readonly List<(LineRenderer line, float dist)> _frames = new List<(LineRenderer, float)>();
    private float _nextFrameBeat;
    private float _idleClock;
    private Color _bg => Theme.Background;
    private Color _tunnelColor => Theme.Tunnel;
    private MaterialPropertyBlock _mpb;

    // ---- setup ------------------------------------------------------------------

    private void BuildStage()
    {
        _mpb = new MaterialPropertyBlock();
        Material glowSrc = glowMaterial != null ? glowMaterial : baseMaterial;
        _glowLine = new Material(glowSrc);
        _glowSoft = new Material(glowSrc) { mainTexture = NeonFX.SoftDot() };
        _unlit = unlitMaterial != null ? unlitMaterial : baseMaterial;

        if (_cam != null)
        {
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = _bg;
        }

        _ringOuter = new LineRenderer[4];
        _ringInner = new LineRenderer[4];
        _approach = new LineRenderer[4];
        _rails = new LineRenderer[4];
        _ringHalo = new Transform[4];
        _laneFlashTime = new[] { -10f, -10f, -10f, -10f };
        _laneFlashColor = new Color[4];

        for (int i = 0; i < 4; i++)
        {
            Color c = LaneColors[i];
            _rails[i] = NeonFX.Line(transform, $"Rail_{LaneNames[i]}", _glowLine, c, 0.02f);
            _rails[i].SetPosition(0, _laneSpawn[i]);
            _rails[i].SetPosition(1, _laneHit[i]);
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.45f, 1f) });
            _rails[i].colorGradient = g;

            _ringHalo[i] = Halo($"RingGlow_{LaneNames[i]}", _laneHit[i], RingRadius * 3.2f);
            _ringOuter[i] = NeonFX.Circle(transform, $"Ring_{LaneNames[i]}", _glowLine, c, 0.03f);
            _ringInner[i] = NeonFX.Line(transform, $"Arrow_{LaneNames[i]}", _glowLine, c, 0.022f, 3);
            _approach[i] = NeonFX.Circle(transform, $"Approach_{LaneNames[i]}", _glowLine, c, 0.02f);
            _approach[i].enabled = false;
        }

        _beam = NeonFX.Line(transform, "AimBeam", _glowLine, Theme.Beam, 0.012f);
        var bg = new Gradient();
        bg.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                   new[] { new GradientAlphaKey(0.05f, 0f), new GradientAlphaKey(0.5f, 1f) });
        _beam.colorGradient = bg;
        _beam.widthCurve = AnimationCurve.Linear(0f, 0.3f, 1f, 1f);
        _reticleRing = NeonFX.Circle(transform, "ReticleRing", _glowLine, Theme.Beam, 0.01f, 32);
        _reticleHalo = Halo("ReticleGlow", _eye + _body * Vector3.forward * hitDistance, 0.18f);

        _burst = NeonFX.Burst(transform, "HitBurst", _glowSoft);
        BuildStarfield();

        for (int k = 0; k < 10; k++)
        {
            var f = NeonFX.Line(transform, $"TunnelFrame{k}", _glowLine, _tunnelColor, 0.02f, 4);
            f.loop = true;
            f.enabled = false;
            _frames.Add((f, -1f));
        }
    }

    // A camera-facing glow quad (soft additive dot).
    private Transform Halo(string name, Vector3 pos, float size)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * size;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = _glowSoft;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    // A chevron inside a ring, pointing in the lane's direction (up/down/left/right).
    private void SetArrow(LineRenderer arrow, int lane, float size)
    {
        Vector3 fwd = _body * Vector3.forward;
        Vector3 outward = Vector3.ProjectOnPlane(_laneDir[lane], fwd).normalized;
        Vector3 side = Vector3.Cross(_laneDir[lane], outward).normalized;
        Vector3 c = _laneHit[lane];
        arrow.SetPosition(0, c - outward * size * 0.25f + side * size * 0.7f);
        arrow.SetPosition(1, c + outward * size * 0.45f);
        arrow.SetPosition(2, c - outward * size * 0.25f - side * size * 0.7f);
    }

    private void SetHalo(Transform halo, Color color)
    {
        halo.rotation = Quaternion.LookRotation(halo.position - _eye);
        var r = halo.GetComponent<Renderer>();
        r.GetPropertyBlock(_mpb);
        _mpb.SetColor("_TintColor", color * 0.5f);   // additive particle shader doubles _TintColor
        _mpb.SetColor("_Color", color);
        r.SetPropertyBlock(_mpb);
    }

    private void BuildStarfield()
    {
        var go = new GameObject("Starfield");
        go.transform.SetParent(transform, false);
        go.transform.position = _eye + _body * Vector3.forward * (spawnDistance + 6f);
        go.transform.rotation = _body;
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.startSpeed = 0f;
        main.startLifetime = 3.2f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
        main.startColor = new ParticleSystem.MinMaxGradient(Theme.StarA, Theme.StarB);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 600;
        var em = ps.emission;
        em.rateOverTime = 90f;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(18f, 11f, 1f);
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        Vector3 v = _body * Vector3.back * 6f;
        vel.x = new ParticleSystem.MinMaxCurve(v.x);
        vel.y = new ParticleSystem.MinMaxCurve(v.y);
        vel.z = new ParticleSystem.MinMaxCurve(v.z);
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = _glowSoft;
        ps.Simulate(3f, true, true);   // start already full of stars
        ps.Play();
    }

    private static Color NoteColor(int lane) => Color.Lerp(LaneColors[lane], Theme.Text, 0.55f);

    private void CreateNoteVisual(Note n)
    {
        // Point notes: a pale spinning cube. Flick notes: a bright red orb with a
        // glowing arrow pointing the way to flick.
        bool flick = n.type == NoteType.Flick;
        Color c = flick ? LaneColors[n.lane] : NoteColor(n.lane);
        var go = GameObject.CreatePrimitive(flick ? PrimitiveType.Sphere : PrimitiveType.Cube);
        go.name = $"{n.type}_{LaneNames[n.lane]}_{n.time:F2}";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * (flick ? 0.26f : 0.22f);
        go.GetComponent<Renderer>().material = new Material(_unlit) { color = c };

        var trail = go.AddComponent<TrailRenderer>();
        trail.sharedMaterial = _glowLine;
        trail.time = 0.16f;
        trail.minVertexDistance = 0.05f;
        trail.widthCurve = AnimationCurve.Linear(0f, 0.2f, 1f, 0f);
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                  new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
        trail.colorGradient = g;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        n.trail = trail;

        // Glow halo around the note (a child so it moves and hides with it).
        var halo = Halo("Glow", Vector3.zero, 2.6f);
        halo.SetParent(go.transform, false);
        halo.localPosition = Vector3.zero;
        SetHalo(halo, c * (flick ? 0.8f : 0.55f));

        if (flick)
        {
            // Arrow in front of the orb; the note is turned each frame so the
            // arrow's local +Y points along the lane on screen.
            var arrow = NeonFX.Line(go.transform, "FlickArrow", _glowLine, Theme.Perfect, 0.045f, 3);
            arrow.useWorldSpace = false;
            arrow.SetPosition(0, new Vector3(-0.85f, -0.15f, -0.7f));
            arrow.SetPosition(1, new Vector3(0f, 0.8f, -0.7f));
            arrow.SetPosition(2, new Vector3(0.85f, -0.15f, -0.7f));
        }

        go.SetActive(false);
        n.go = go;
    }

    // ---- per frame ----------------------------------------------------------------

    private void UpdateVisuals()
    {
        float beat = _state == State.Playing ? Beat : (_idleClock += Time.deltaTime * 1.2f);
        float beatPhase = Mathf.Repeat(beat, 1f);
        float pulse = Mathf.Exp(-beatPhase * 6f);                 // 1 on the beat, decays
        bool downbeat = Mathf.Repeat(beat, 4f) < 1f;

        if (_cam != null)
            _cam.backgroundColor = Color.Lerp(_bg, Theme.BackgroundPulse, pulse * (downbeat ? 0.7f : 0.3f));

        // Rings: breathe with the beat, light up when aimed, flash on hit/miss.
        for (int i = 0; i < 4; i++)
        {
            bool aimed = i == _aimedLane;
            float flash = Mathf.Clamp01(1f - (Time.unscaledTime - _laneFlashTime[i]) / 0.3f);
            float scale = 1f + 0.06f * pulse + (aimed ? 0.12f : 0f) + 0.25f * flash;
            Color c = Color.Lerp(LaneColors[i] * (aimed ? 1f : 0.55f), _laneFlashColor[i], flash);
            c.a = 1f;
            _ringOuter[i].startColor = _ringOuter[i].endColor = c;
            _ringOuter[i].widthMultiplier = aimed ? 0.05f : 0.03f;
            NeonFX.SetCircle(_ringOuter[i], _laneHit[i], _laneDir[i], RingRadius * scale);
            _ringInner[i].startColor = _ringInner[i].endColor = c * 0.85f;
            SetArrow(_ringInner[i], i, 0.1f * scale);
            SetHalo(_ringHalo[i], c * (0.18f + (aimed ? 0.3f : 0f) + 0.7f * flash));
        }

        UpdateApproachCircles();
        UpdateNotePositions();
        UpdateBeam();
        UpdateTunnel(beat);
    }

    // For each lane, a circle that shrinks onto the ring exactly on the beat of
    // the next note: the main timing cue.
    private void UpdateApproachCircles()
    {
        const float lead = 0.9f;                      // seconds before the hit it appears
        for (int i = 0; i < 4; i++)
        {
            Note next = null;
            if (_state == State.Playing)
                foreach (var n in _notes)
                    if (!n.judged && n.lane == i && n.time - _songTime > -0.05f && (next == null || n.time < next.time))
                        next = n;
            float dt = next != null ? next.time - _songTime : 99f;
            bool show = dt < lead;
            _approach[i].enabled = show;
            if (!show) continue;
            float u = Mathf.Clamp01(dt / lead);        // 1 → 0 as the beat arrives
            Color c = next.type == NoteType.Flick ? LaneColors[i] : NoteColor(i);
            c.a = Mathf.Lerp(1f, 0.15f, u);
            _approach[i].startColor = _approach[i].endColor = c;
            NeonFX.SetCircle(_approach[i], _laneHit[i], _laneDir[i], RingRadius * (1f + 2.2f * u));
        }
    }

    private void UpdateNotePositions()
    {
        if (_state != State.Playing) return;
        foreach (var n in _notes)
        {
            if (n.judged) continue;
            float dt = n.time - _songTime;
            if (dt >= noteTravelTime || dt <= -goodWindow - 0.1f) continue;
            float u = 1f - dt / noteTravelTime;       // 0 at spawn, 1 at the ring
            n.go.transform.position = Vector3.LerpUnclamped(_laneSpawn[n.lane], _laneHit[n.lane], u);
            if (!n.go.activeSelf)
            {
                n.go.SetActive(true);
                n.trail.Clear();                       // don't streak in from where it was created
            }
            if (n.type == NoteType.Flick)
                n.go.transform.rotation = Quaternion.LookRotation(n.go.transform.position - _eye, _laneOut[n.lane]);
            else
                n.go.transform.Rotate(90f * Time.deltaTime, 200f * Time.deltaTime, 0f);
            var halo = n.go.transform.GetChild(0);
            halo.rotation = Quaternion.LookRotation(halo.position - _eye);
        }
    }

    private void UpdateBeam()
    {
        Vector3 target = _eye + _aim * hitDistance;
        Color c = _aimedLane >= 0 ? LaneColors[_aimedLane] : Theme.Beam;
        Vector3 from = hand != null && hand.useArmModel ? hand.transform.position + hand.AimDirection * 0.12f
                                                        : _eye + Vector3.down * 0.3f;
        _beam.SetPosition(0, from);
        _beam.SetPosition(1, target);
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                  new[] { new GradientAlphaKey(0.05f, 0f), new GradientAlphaKey(0.6f, 1f) });
        _beam.colorGradient = g;
        _reticleRing.startColor = _reticleRing.endColor = c;
        NeonFX.SetCircle(_reticleRing, target, _aim, 0.045f);
        _reticleHalo.position = target;
        SetHalo(_reticleHalo, c * 0.6f);
    }

    // Square frames flying toward you, one per beat: a tunnel that moves with the music.
    private void UpdateTunnel(float beat)
    {
        float farDist = spawnDistance + 4f, nearDist = 1.2f;
        float speed = (farDist - nearDist) / 2.5f;    // meters per second
        if (beat < _nextFrameBeat - 2f) _nextFrameBeat = Mathf.Floor(beat) + 1f;   // clock switched (menu <-> song)
        if (beat >= _nextFrameBeat)
        {
            _nextFrameBeat = Mathf.Floor(beat) + 1f;
            for (int k = 0; k < _frames.Count; k++)
                if (_frames[k].dist < 0f) { _frames[k] = (_frames[k].line, farDist); break; }
        }
        for (int k = 0; k < _frames.Count; k++)
        {
            var (line, dist) = _frames[k];
            if (dist < 0f) { line.enabled = false; continue; }
            dist -= speed * Time.deltaTime;
            if (dist < nearDist) { line.enabled = false; _frames[k] = (line, -1f); continue; }
            line.enabled = true;
            float fadeIn = Mathf.Clamp01((farDist - dist) / 3f), fadeOut = Mathf.Clamp01((dist - nearDist) / 2f);
            Color c = _tunnelColor;
            c.a = 0.4f * fadeIn * fadeOut;
            line.startColor = line.endColor = c;
            line.widthMultiplier = 0.015f + 0.01f * dist / farDist;
            NeonFX.SetFrame(line, _eye + _body * Vector3.forward * dist, _body, 4.2f, 2.6f);
            _frames[k] = (line, dist);
        }
    }

    // ---- events ---------------------------------------------------------------------

    private void OnJudgedFX(int lane, Judgement j)
    {
        _laneFlashTime[lane] = Time.unscaledTime;
        _laneFlashColor[lane] = j == Judgement.Miss ? Theme.MissFlash : Theme.Perfect;
        if (j != Judgement.Miss)
        {
            var p = new ParticleSystem.EmitParams
            {
                position = _laneHit[lane],
                startColor = Color.Lerp(LaneColors[lane], Theme.Perfect, 0.35f),
                applyShapeToPosition = true,
            };
            _burst.Emit(p, j == Judgement.Perfect ? 40 : 18);
            _comboBumpTime = Time.unscaledTime;
        }
    }

    private void OnStrikeFX()
    {
        // A quick brighten of the beam shows the strike registered, hit or not.
        _beam.widthMultiplier = 0.03f;
        CancelInvoke(nameof(ResetBeamWidth));
        Invoke(nameof(ResetBeamWidth), 0.08f);
    }

    private void ResetBeamWidth() => _beam.widthMultiplier = 0.012f;
}
