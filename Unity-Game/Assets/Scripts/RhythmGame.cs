using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Flick Note": a rhythm game played by pointing with the IMU glove.
///
/// Notes fly toward you in four lanes: up, down, left, right. Every note works
/// the same way: point at its lane (the laser shows where you're pointing) when
/// it reaches the ring. A wrist flick starts the song.
///
/// Keys: Enter = start, R = re-center, [ ] = latency offset, Space = start and
/// arrow keys = aim (for testing without the glove), Esc = quit (in a build).
///
/// This file is the game logic. The 3D effects are in RhythmGame.Visuals.cs and
/// the on-screen HUD in RhythmGame.Hud.cs.
/// </summary>
public partial class RhythmGame : MonoBehaviour
{
    public GloveReceiver glove;
    public GloveHand hand;

    [Header("Materials (assigned by SceneBuilder; they keep their shaders in builds)")]
    public Material baseMaterial;       // lit, for solid objects
    public Material unlitMaterial;      // flat bright color, for notes
    public Material glowMaterial;       // additive glow: lines, trails, particles, halos

    [Header("Song")]
    [Tooltip("Leave empty to use the built-in generated beat. If you set a song, also set its BPM and offset.")]
    public AudioClip song;
    public float bpm = 100f;
    [Tooltip("Seconds from the start of the audio to beat 0")]
    public float songOffset = 0f;
    [Tooltip("Song length in beats (for the generated beat and the chart)")]
    public int lengthBeats = 104;
    public int chartSeed = 7;

    [Header("Timing")]
    [Tooltip("Seconds to subtract from input times to make up for Bluetooth/bridge delay. Adjust with [ and ].")]
    public float latencyOffset = 0.045f;
    [Tooltip("Pointing at the lane within this many seconds of the beat still counts (as GOOD)")]
    public float goodWindow = 0.14f;
    [Tooltip("How long a note takes to fly from spawn to the hit ring")]
    public float noteTravelTime = 1.8f;

    [Header("Layout")]
    public float hitDistance = 3f;
    public float spawnDistance = 13f;
    [Tooltip("How far from the center (meters) notes start, far away; they fly outward to their ring")]
    public float spawnSpread = 0.9f;
    public float laneYaw = 28f;
    public float lanePitch = 22f;
    [Tooltip("How close (degrees) the hand must point to a lane to count as aiming at it. " +
             "Keep it below the lane angles so pointing straight ahead aims at nothing.")]
    public float aimTolerance = 14f;

    // ---------------------------------------------------------------------------

    private enum State { Menu, Playing, Results }
    private enum Judgement { None, Perfect, Good, Miss }

    private class Note
    {
        public float time;
        public int lane;
        public GameObject go;
        public TrailRenderer trail;
        public bool judged;
        public bool aimedInWindow;
    }

    private const int CountInBeats = 8;
    private static readonly string[] LaneNames = { "UP", "DOWN", "LEFT", "RIGHT" };
    private static Color[] LaneColors => Theme.Lanes;

    private State _state = State.Menu;
    private AudioSource _audio;
    private double _dspStart;
    private float _songTime;
    private readonly List<Note> _notes = new List<Note>();
    private Vector3 _eye;
    private Quaternion _body = Quaternion.identity;
    private Vector3[] _laneDir;
    private Vector3[] _laneSpawn, _laneHit;
    private Vector3 _aim = Vector3.forward;
    private int _aimedLane = -1;
    private Camera _cam;

    private int _score, _combo, _maxCombo, _perfects, _goods, _misses;
    private float _lastStrike = -10f;
    private float _resultsSince;

    // Self-test (used when checking a build from the command line).
    private float _autoTestSeconds = -1f;
    private string _autoTestShot;
    private bool _bot;                                 // self-test: aim automatically
    private string _autoTestShot2;                     // second screenshot (hand close-up)
    private bool _hideHud;

    private float SongLength => lengthBeats * 60f / bpm + songOffset;
    private float Beat => (_songTime - songOffset) * bpm / 60f;
    private float Accuracy
    {
        get
        {
            int total = _perfects + _goods + _misses;
            return total == 0 ? 100f : (_perfects + 0.5f * _goods) * 100f / total;
        }
    }
    private int Multiplier => 1 + Mathf.Min(_combo / 10, 3);

    // ---------------------------------------------------------------------------

    private void Start()
    {
        if (glove == null) glove = FindAnyObjectByType<GloveReceiver>();
        if (hand == null) hand = FindAnyObjectByType<GloveHand>();
        if (glove != null) glove.Gesture += OnGesture;

        _cam = Camera.main;
        _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        if (song == null) song = BeatSynth.Create(bpm, lengthBeats);

        BuildLanes();
        BuildStage();          // visuals
        ReadCommandLine();
    }

    private void OnDestroy()
    {
        if (glove != null) glove.Gesture -= OnGesture;
    }

    private void BuildLanes()
    {
        _eye = _cam != null ? _cam.transform.position : new Vector3(0f, 1.4f, 0f);
        Vector3 fwd = _cam != null ? _cam.transform.forward : Vector3.forward;
        fwd.y = 0f;
        _body = Quaternion.LookRotation(fwd.sqrMagnitude > 0 ? fwd.normalized : Vector3.forward);

        _laneDir = new[]
        {
            _body * Quaternion.Euler(-lanePitch, 0f, 0f) * Vector3.forward,   // up
            _body * Quaternion.Euler(lanePitch, 0f, 0f) * Vector3.forward,    // down
            _body * Quaternion.Euler(0f, -laneYaw, 0f) * Vector3.forward,     // left
            _body * Quaternion.Euler(0f, laneYaw, 0f) * Vector3.forward,      // right
        };

        // Notes start near a vanishing point far ahead, slightly toward their
        // lane, and fly out to the lane's ring, so their approach is visible.
        _laneSpawn = new Vector3[4];
        _laneHit = new Vector3[4];
        Vector3 center = _eye + _body * Vector3.forward * spawnDistance;
        for (int i = 0; i < 4; i++)
        {
            _laneHit[i] = _eye + _laneDir[i] * hitDistance;
            Vector3 lateral = Vector3.ProjectOnPlane(_laneDir[i], _body * Vector3.forward).normalized;
            _laneSpawn[i] = center + lateral * spawnSpread;
        }
    }

    // ---- chart ----------------------------------------------------------------

    private void BuildChart()
    {
        foreach (var n in _notes) if (n.go != null) Destroy(n.go);
        _notes.Clear();

        var rng = new System.Random(chartSeed);
        float spb = 60f / bpm;
        int lastBeat = lengthBeats - 4;
        int prevLane = -1, repeat = 0;

        // A note every 2 beats for the first ~30% of the song, then every beat.
        for (float beat = CountInBeats; beat < lastBeat;)
        {
            float progress = (beat - CountInBeats) / (lastBeat - CountInBeats);
            int lane = rng.Next(4);
            if (lane == prevLane && ++repeat >= 2) lane = (lane + 1 + rng.Next(3)) % 4;
            if (lane != prevLane) repeat = 0;
            prevLane = lane;

            AddNote(songOffset + beat * spb, lane);
            beat += progress < 0.3f ? 2f : 1f;
        }
    }

    private void AddNote(float time, int lane)
    {
        var n = new Note { time = time, lane = lane };
        CreateNoteVisual(n);     // visuals
        _notes.Add(n);
    }

    // ---- game flow --------------------------------------------------------------

    private void StartSong()
    {
        BuildChart();
        _score = _combo = _maxCombo = _perfects = _goods = _misses = 0;
        _audio.clip = song;
        _dspStart = AudioSettings.dspTime + 0.3;
        _audio.PlayScheduled(_dspStart);
        _songTime = -0.3f;
        _state = State.Playing;
        ClearPop();
    }

    private void Update()
    {
        if (KeyInput.Pressed(KeyInput.K.Escape)) Application.Quit();
        if (KeyInput.Pressed(KeyInput.K.LeftBracket)) latencyOffset -= 0.01f;
        if (KeyInput.Pressed(KeyInput.K.RightBracket)) latencyOffset += 0.01f;
        if (KeyInput.Pressed(KeyInput.K.Space)) OnStrike();
        if (KeyInput.Pressed(KeyInput.K.Enter) && _state != State.Playing) StartSong();

        UpdateAim();

        if (_state == State.Playing)
        {
            UpdateSongTime();
            UpdateNotes();
            if (_songTime > SongLength + 1f)
            {
                _state = State.Results;
                _resultsSince = Time.unscaledTime;
            }
        }
        UpdateVisuals();         // visuals
        UpdateAutoTest();
    }

    private void UpdateSongTime()
    {
        // The audio clock updates in coarse steps; advance smoothly with frame
        // time and gently pull toward it.
        float dsp = (float)(AudioSettings.dspTime - _dspStart);
        _songTime += Time.unscaledDeltaTime;
        if (Mathf.Abs(dsp - _songTime) > 0.05f) _songTime = dsp;
        else _songTime += (dsp - _songTime) * 0.1f;
    }

    private void UpdateAim()
    {
        _aimedLane = -1;
        if (hand == null) return;
        _aim = _bot ? BotAim() : hand.AimDirection;
        float best = aimTolerance;
        for (int i = 0; i < 4; i++)
        {
            float a = Vector3.Angle(_aim, _laneDir[i]);
            if (a < best) { best = a; _aimedLane = i; }
        }
    }

    // Every note: PERFECT if you're pointing at its lane when it reaches the
    // ring, GOOD if you pointed at it within goodWindow of the beat, else MISS.
    private void UpdateNotes()
    {
        float now = _songTime - latencyOffset;         // allow for Bluetooth delay
        foreach (var n in _notes)
        {
            if (n.judged) continue;
            float dt = n.time - now;                   // seconds until the note reaches the ring
            if (Mathf.Abs(dt) <= goodWindow && _aimedLane == n.lane) n.aimedInWindow = true;
            if (dt <= 0f && _aimedLane == n.lane) Judge(n, Judgement.Perfect);
            else if (dt < -goodWindow) Judge(n, n.aimedInWindow ? Judgement.Good : Judgement.Miss);
        }
    }

    // ---- input ------------------------------------------------------------------

    private void OnGesture(string gesture)
    {
        if (gesture == "flick") OnStrike();
    }

    // A flick (or Space) starts the song from the menu / results screen. During
    // play it only animates the glove; notes are scored by pointing.
    private void OnStrike()
    {
        if (Time.unscaledTime - _lastStrike < 0.12f) return;   // ignore a flick's rebound
        _lastStrike = Time.unscaledTime;

        if (_state != State.Playing)
        {
            if (_state == State.Menu || Time.unscaledTime - _resultsSince > 2f) StartSong();
            return;
        }
        OnStrikeFX();            // visuals
        if (hand != null) hand.PlayStrike();
    }

    private void Judge(Note n, Judgement j)
    {
        n.judged = true;
        if (n.go != null) n.go.SetActive(false);
        switch (j)
        {
            case Judgement.Perfect:
                _perfects++; _combo++; _score += 300 * Multiplier;
                Pop("PERFECT", Theme.Perfect);
                break;
            case Judgement.Good:
                _goods++; _combo++; _score += 100 * Multiplier;
                Pop("GOOD", Theme.Good);
                break;
            default:
                _misses++; _combo = 0;
                Pop("MISS", Theme.Miss);
                break;
        }
        _maxCombo = Mathf.Max(_maxCombo, _combo);
        OnJudgedFX(n.lane, j);   // visuals
    }

    // ---- self-test ------------------------------------------------------------------
    // Command line: -glovetest <seconds> <screenshot.png> [-glovebot] [-glovemenu]
    // Starts the song (unless -glovemenu), takes a screenshot after <seconds>,
    // logs a summary line starting with GLOVETEST, then quits. -glovebot plays perfectly.

    private void ReadCommandLine()
    {
        var args = System.Environment.GetCommandLineArgs();
        _bot = System.Array.IndexOf(args, "-glovebot") >= 0;
        bool menuOnly = System.Array.IndexOf(args, "-glovemenu") >= 0;
        for (int i = 0; i < args.Length - 2; i++)
        {
            if (args[i] == "-glovetest" && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                                                          System.Globalization.CultureInfo.InvariantCulture, out float sec))
            {
                _autoTestSeconds = sec;
                _autoTestShot = args[i + 2];
                if (!menuOnly) StartSong();
            }
        }
    }

    // Bot: point at the lane of the next note, shortly before it arrives.
    private Vector3 BotAim()
    {
        Note next = null;
        foreach (var n in _notes)
            if (!n.judged && n.time > _songTime - latencyOffset - goodWindow && (next == null || n.time < next.time)) next = n;
        return next != null && next.time - (_songTime - latencyOffset) < 0.25f ? _laneDir[next.lane] : _body * Vector3.forward;
    }

    private void UpdateAutoTest()
    {
        if (_autoTestSeconds < 0f || Time.timeSinceLevelLoad < _autoTestSeconds) return;
        if (_autoTestShot != null)
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-glovehandview") >= 0 && hand != null && _cam != null)
            {
                // Close-up of the glove model (for checking how it looks).
                Vector3 h = hand.transform.position;
                _cam.transform.position = h + hand.transform.rotation * new Vector3(-0.12f, 0.16f, 0.26f);
                _cam.transform.LookAt(h + hand.transform.forward * 0.03f);
                _cam.fieldOfView = 40f;
                hand.enabled = false;               // freeze it where it is
                _hideHud = true;
                ScreenCapture.CaptureScreenshot(_autoTestShot);     // open hand
                hand.PlayStrike();                                  // then the fist, 60 ms later
                _autoTestSeconds += 0.06f;
                _autoTestShot2 = _autoTestShot.Replace(".png", "_fist.png");
                _autoTestShot = null;
                return;
            }
            ScreenCapture.CaptureScreenshot(_autoTestShot);
            Debug.Log($"GLOVETEST screenshot={_autoTestShot} state={_state} songTime={_songTime:F2} notes={_notes.Count} " +
                      $"perfect={_perfects} good={_goods} miss={_misses} glove={(glove != null && glove.Connected)} " +
                      $"pps={(glove != null ? glove.PacketsPerSecond : 0)} status='{(glove != null ? glove.Status : "")}' " +
                      $"aim={_aim:F2}");
            _autoTestShot = null;
            _autoTestSeconds += 1.5f;                  // give the screenshot time to be written
            return;
        }
        if (_autoTestShot2 != null)
        {
            ScreenCapture.CaptureScreenshot(_autoTestShot2);
            _autoTestShot2 = null;
            _autoTestSeconds += 1.5f;
            return;
        }
        Application.Quit();
    }
}
