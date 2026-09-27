using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives glove data from the Python bridge (pc-app/unity_bridge.py) over UDP.
///
/// Put one in the scene. Other scripts read its properties every frame, and can
/// subscribe to <see cref="Gesture"/> to react to "flick", "shake",
/// "flipped face down" and "flipped face up".
/// </summary>
public class GloveReceiver : MonoBehaviour
{
    [Tooltip("Must match --port of unity_bridge.py")]
    public int port = 5005;

    /// <summary>True while fresh data is arriving from a connected glove.</summary>
    public bool Connected { get; private set; }

    /// <summary>Hand orientation in Unity axes (before re-centering and mount correction).</summary>
    public Quaternion Rotation { get; private set; } = Quaternion.identity;

    /// <summary>Acceleration without gravity, m/s², world axes.</summary>
    public Vector3 LinearAcceleration { get; private set; }

    /// <summary>Angular velocity, degrees per second.</summary>
    public Vector3 AngularVelocity { get; private set; }

    /// <summary>True when the hand is at rest.</summary>
    public bool Still { get; private set; }

    /// <summary>Packets received during the last second.</summary>
    public int PacketsPerSecond { get; private set; }

    /// <summary>Latest status from the bridge, for display.</summary>
    public string Status { get; private set; } = "waiting for bridge (run unity_bridge.py)";

    /// <summary>Raised on the main thread for each gesture the bridge detects.</summary>
    public event Action<string> Gesture;

    // Matches the JSON sent by the bridge. Missing fields stay at their defaults.
    [Serializable]
    private class Packet
    {
        public bool connected;
        public float t;
        public float[] q;
        public float[] lin;
        public float[] gyro;
        public bool still;
        public string[] events;
        public string status;       // only in "connected": false packets
    }

    private UdpClient _udp;
    private Thread _thread;
    private volatile bool _running;
    private readonly object _lock = new object();
    private Packet _latest;
    private float _lastPacketTime = -999f;
    private int _received;          // written by the network thread
    private readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
    private float _rateTimer;
    private int _rateCount;
    private volatile string _bridgeStatus;      // null until the bridge says something
    private long _lastAnyPacketTicks;           // DateTime ticks of the last packet of any kind

    private void OnEnable()
    {
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException e)
        {
            Status = $"can't listen on UDP port {port}: {e.Message}";
            Debug.LogError($"GloveReceiver: {Status}");
            return;
        }
        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "GloveReceiver" };
        _thread.Start();
    }

    private void OnDisable()
    {
        _running = false;
        _udp?.Close();              // unblocks Receive()
        _thread?.Join(500);
        _udp = null;
        _thread = null;
    }

    private void ReceiveLoop()
    {
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref any);
                var p = JsonUtility.FromJson<Packet>(Encoding.UTF8.GetString(data));
                Interlocked.Exchange(ref _lastAnyPacketTicks, DateTime.UtcNow.Ticks);
                _bridgeStatus = p.connected ? "connected" : (p.status ?? "searching");
                if (!p.connected || p.q == null || p.q.Length != 4)
                    continue;
                lock (_lock) _latest = p;
                Interlocked.Increment(ref _received);
                if (p.events != null)
                    foreach (var e in p.events) _events.Enqueue(e);
            }
            catch (SocketException) { /* socket closed on shutdown */ }
            catch (ObjectDisposedException) { return; }
            catch (Exception e) { Debug.LogWarning($"GloveReceiver: bad packet ({e.Message})"); }
        }
    }

    private void Update()
    {
        Packet p;
        lock (_lock) { p = _latest; _latest = null; }
        int newPackets = Interlocked.Exchange(ref _received, 0);

        if (p != null)
        {
            _lastPacketTime = Time.unscaledTime;
            Rotation = new Quaternion(p.q[0], p.q[1], p.q[2], p.q[3]);
            if (p.lin != null && p.lin.Length == 3) LinearAcceleration = new Vector3(p.lin[0], p.lin[1], p.lin[2]);
            if (p.gyro != null && p.gyro.Length == 3) AngularVelocity = new Vector3(p.gyro[0], p.gyro[1], p.gyro[2]);
            Still = p.still;
        }

        Connected = Time.unscaledTime - _lastPacketTime < 0.5f;
        double sinceAny = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastAnyPacketTicks)) / (double)TimeSpan.TicksPerSecond;
        if (Connected) Status = "connected";
        else if (_bridgeStatus == null || sinceAny > 2) Status = "waiting for bridge (run unity_bridge.py)";
        else if (_bridgeStatus == "calibrating") Status = "glove found, calibrating: hold it still";
        else Status = "bridge running, looking for the glove (is the board on?)";

        _rateCount += newPackets;
        _rateTimer += Time.unscaledDeltaTime;
        if (_rateTimer >= 1f)
        {
            PacketsPerSecond = Mathf.RoundToInt(_rateCount / _rateTimer);
            _rateCount = 0;
            _rateTimer = 0f;
        }

        while (_events.TryDequeue(out var e))
            Gesture?.Invoke(e);
    }
}
