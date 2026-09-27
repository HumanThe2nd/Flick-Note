using UnityEngine;

/// <summary>
/// Moves this object (the virtual hand) to mirror the glove.
///
/// Rotation comes straight from the glove. Position comes from an "arm model":
/// the IMU can't measure where the hand is, so we assume a shoulder and elbow
/// and place the hand where a real arm pointing that way would put it.
///
/// Press R (or call Recenter) while pointing straight ahead to make that
/// direction "forward". Without a glove, the arrow keys aim the hand.
/// </summary>
public class GloveHand : MonoBehaviour
{
    public GloveReceiver glove;

    [Tooltip("Arm model pivot, usually a child of the camera, below and to the right of it")]
    public Transform shoulder;

    [Tooltip("Lit material the glove and sleeve are made from (keeps its shader in builds)")]
    public Material baseMaterial;
    [Tooltip("Unlit material for the glowing accents (fingertips, cuff, LED)")]
    public Material accentMaterial;

    [Header("Mounting")]
    [Tooltip("Rotation correction for how the board sits on the glove. The default assumes the " +
             "board lies flat on the back of the hand with its x axis pointing toward the fingers. " +
             "If the virtual hand moves wrong, try 90 degree steps here.")]
    public Vector3 mountRotation = Vector3.zero;

    [Header("Arm model")]
    public bool useArmModel = true;
    [Tooltip("Elbow position relative to the shoulder when the arm points forward (meters)")]
    public Vector3 elbowRestOffset = new Vector3(0f, -0.15f, 0.12f);
    public float forearmLength = 0.42f;
    [Tooltip("How much the upper arm follows the hand's direction (0 = elbow fixed, 1 = straight arm)")]
    [Range(0f, 1f)] public float elbowFollow = 0.45f;
    [Tooltip("Draw the arm (upper arm, elbow, forearm). Off = floating glove, which reads better on screen.")]
    public bool showArm = false;
    [Tooltip("Size of the glove model (1 = life size; bigger reads better at arm's length)")]
    public float modelScale = 1.45f;

    [Header("Feel")]
    [Tooltip("0 = raw (lowest latency); higher = smoother but laggier")]
    [Range(0f, 0.9f)] public float smoothing = 0.15f;
    public bool recenterOnConnect = true;
    [Tooltip("Degrees per second for arrow-key aiming when no glove is connected")]
    public float keyboardAimSpeed = 90f;

    /// <summary>World rotation of the hand after re-centering and mount correction.</summary>
    public Quaternion AimRotation { get; private set; } = Quaternion.identity;

    /// <summary>Direction the fingers point, in world space.</summary>
    public Vector3 AimDirection => AimRotation * Vector3.forward;

    public bool UsingKeyboard => glove == null || !glove.Connected;

    private Quaternion _yawOffset = Quaternion.identity;
    private bool _wasConnected;
    private float _kbYaw, _kbPitch;
    private Transform _upperArm, _forearm, _elbowJoint;
    private GloveModel _model;

    private void Start()
    {
        if (glove == null) glove = FindAnyObjectByType<GloveReceiver>();
        transform.localScale = Vector3.one * modelScale;
        if (transform.childCount == 0)
        {
            _model = gameObject.AddComponent<GloveModel>();
            _model.Build(baseMaterial, accentMaterial);
        }
        if (showArm)
        {
            _upperArm = MakeLimb("UpperArm", PrimitiveType.Capsule);
            _forearm = MakeLimb("Forearm", PrimitiveType.Capsule);
            _elbowJoint = MakeLimb("Elbow", PrimitiveType.Sphere);
            _elbowJoint.localScale = Vector3.one * 0.06f;
        }
    }

    /// <summary>Play the glove's strike animation (a quick fist).</summary>
    public void PlayStrike()
    {
        if (_model != null) _model.Strike();
    }

    /// <summary>Make the hand's current pointing direction the new "straight ahead".</summary>
    public void Recenter()
    {
        if (UsingKeyboard)
        {
            _kbYaw = _kbPitch = 0f;
            return;
        }
        Quaternion raw = glove.Rotation * Quaternion.Euler(mountRotation);
        Vector3 f = raw * Vector3.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = raw * Vector3.up;   // pointing straight up/down
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) return;
        _yawOffset = Quaternion.LookRotation(ForwardReference(), Vector3.up)
                     * Quaternion.Inverse(Quaternion.LookRotation(f.normalized, Vector3.up));
    }

    private void Update()
    {
        if (KeyInput.Pressed(KeyInput.K.R)) Recenter();

        Quaternion target;
        if (!UsingKeyboard)
        {
            if (!_wasConnected && recenterOnConnect) Recenter();
            target = _yawOffset * glove.Rotation * Quaternion.Euler(mountRotation);
        }
        else
        {
            float dt = Time.deltaTime * keyboardAimSpeed;
            if (KeyInput.Held(KeyInput.K.Left)) _kbYaw -= dt;
            if (KeyInput.Held(KeyInput.K.Right)) _kbYaw += dt;
            if (KeyInput.Held(KeyInput.K.Up)) _kbPitch -= dt;
            if (KeyInput.Held(KeyInput.K.Down)) _kbPitch += dt;
            _kbPitch = Mathf.Clamp(_kbPitch, -80f, 80f);
            target = Quaternion.LookRotation(ForwardReference(), Vector3.up) * Quaternion.Euler(_kbPitch, _kbYaw, 0f);
        }
        _wasConnected = !UsingKeyboard;

        float follow = smoothing <= 0f ? 1f : 1f - Mathf.Pow(smoothing, Time.deltaTime * 60f);
        AimRotation = Quaternion.Slerp(AimRotation, target, follow);
        transform.rotation = AimRotation;

        if (useArmModel && shoulder != null)
        {
            Quaternion body = Quaternion.LookRotation(ForwardReference(), Vector3.up);
            Vector3 fwd = AimDirection;
            // The upper arm swings part of the way toward where the hand points
            // (ignoring wrist roll, which doesn't move the elbow).
            Quaternion swing = Quaternion.FromToRotation(body * Vector3.forward, fwd);
            Quaternion elbowRot = Quaternion.Slerp(Quaternion.identity, swing, elbowFollow);
            Vector3 elbow = shoulder.position + elbowRot * (body * elbowRestOffset);
            transform.position = elbow + fwd * forearmLength;

            if (showArm && _upperArm != null)
            {
                PlaceLimb(_upperArm, shoulder.position, elbow, 0.058f);
                PlaceLimb(_forearm, elbow, transform.position - fwd * 0.065f, 0.05f);
                _elbowJoint.position = elbow;
            }
        }
    }

    // The player's "forward": the camera's heading, flattened.
    private Vector3 ForwardReference()
    {
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        Vector3 f = cam != null ? cam.forward : Vector3.forward;
        f.y = 0f;
        return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
    }

    // ---- arm (sleeve) ---------------------------------------------------------------
    // The glove itself is built by GloveModel.

    private Transform MakeLimb(string name, PrimitiveType type)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        Tint(go, Theme.Sleeve, baseMaterial);
        var m = go.GetComponent<Renderer>().material;
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.6f);
        return go.transform;
    }

    private static void PlaceLimb(Transform limb, Vector3 a, Vector3 b, float thickness)
    {
        Vector3 d = b - a;
        limb.position = (a + b) / 2f;
        limb.rotation = Quaternion.FromToRotation(Vector3.up, d.sqrMagnitude > 1e-6f ? d : Vector3.up);
        limb.localScale = new Vector3(thickness, d.magnitude / 2f, thickness);
    }

    /// <summary>Give an object its own colored copy of baseMaterial (or of its default material).</summary>
    public static void Tint(GameObject go, Color color, Material baseMaterial)
    {
        var r = go.GetComponent<Renderer>();
        r.material = new Material(baseMaterial != null ? baseMaterial : r.sharedMaterial) { color = color };
    }
}
