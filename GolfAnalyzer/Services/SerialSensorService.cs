using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using GolfAnalyzer.Models;

namespace GolfAnalyzer.Services;

public sealed class SerialSensorService : ISerialSensorService
{
    private SerialPort? _p1, _p2, _p3;
    private readonly CircularBuffer _b1 = new(8192);
    private readonly CircularBuffer _b2 = new(8192);
    private readonly CircularBuffer _b3 = new(8192);
    private CancellationTokenSource? _cts;
    private Task? _t1, _t2, _t3;

    private const byte SOF = 0x02;
    private const byte LEN = 0x18; // 24 bytes payload
    private const byte EOF = 0x03;

    public bool IsConnected { get; private set; }

    public event EventHandler<SensorFrameEventArgs>? FrameReceived;

    public void Connect(params string[] portNames)
    {
        if (IsConnected) return;
        if (portNames is null || portNames.Length < 3)
            throw new ArgumentException("Expect 3 port names (e.g., COM29, COM30, COM31).");

        _cts = new CancellationTokenSource();

        _p1 = CreatePort(portNames[0]);
        _p2 = CreatePort(portNames[1]);
        _p3 = CreatePort(portNames[2]);

        _p1.DataReceived += (_, __) => ReadAvailable(_p1!, _b1);
        _p2.DataReceived += (_, __) => ReadAvailable(_p2!, _b2);
        _p3.DataReceived += (_, __) => ReadAvailable(_p3!, _b3);

        _p1.Open(); _p2.Open(); _p3.Open();

        _t1 = Task.Run(() => DecodeLoop(_b1, 1, _cts.Token));
        _t2 = Task.Run(() => DecodeLoop(_b2, 2, _cts.Token));
        _t3 = Task.Run(() => DecodeLoop(_b3, 3, _cts.Token));

        IsConnected = true;
    }

    public void Disconnect()
    {
        if (!IsConnected) return;

        try { _cts?.Cancel(); } catch { }
        try { _t1?.Wait(100); } catch { }
        try { _t2?.Wait(100); } catch { }
        try { _t3?.Wait(100); } catch { }

        SafeClose(_p1); SafeClose(_p2); SafeClose(_p3);
        _p1 = _p2 = _p3 = null;
        _t1 = _t2 = _t3 = null;
        _cts?.Dispose(); _cts = null;

        IsConnected = false;
    }

    public void Dispose() => Disconnect();

    private static SerialPort CreatePort(string name) =>
        new SerialPort(name, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 50,
            WriteTimeout = 50
        };

    private static void SafeClose(SerialPort? p)
    {
        if (p == null) return;
        try { if (p.IsOpen) p.Close(); p.Dispose(); } catch { }
    }

    private static void ReadAvailable(SerialPort port, CircularBuffer buf)
    {
        try
        {
            int toRead = port.BytesToRead;
            if (toRead <= 0) return;
            var tmp = new byte[toRead];
            int read = port.Read(tmp, 0, toRead);
            if (read > 0)
                buf.EnqueueRange(tmp, 0, read);
        }
        catch { /* ignore transient read errors */ }
    }

    private enum State { WaitSof, WaitLen, ReadPayload, WaitEof }

    private sealed class Parser
    {
        private State _st = State.WaitSof;
        private byte _len;
        private int _idx;
        private readonly byte[] _payload = new byte[24];
        private readonly int _sensorId;
        private readonly Action<int, SensorFrame> _onFrame;

        public Parser(int sensorId, Action<int, SensorFrame> onFrame)
        {
            _sensorId = sensorId;
            _onFrame = onFrame;
        }

        public void Push(byte b)
        {
            switch (_st)
            {
                case State.WaitSof:
                    if (b == SOF) _st = State.WaitLen;
                    break;
                case State.WaitLen:
                    _len = b;
                    if (_len == LEN) { _idx = 0; _st = State.ReadPayload; }
                    else _st = State.WaitSof;
                    break;
                case State.ReadPayload:
                    _payload[_idx++] = b;
                    if (_idx >= _payload.Length) _st = State.WaitEof;
                    break;
                case State.WaitEof:
                    if (b == EOF)
                    {
                        var f = Decode(_payload);
                        _onFrame(_sensorId, f);
                    }
                    _st = State.WaitSof;
                    break;
            }
        }

        private static SensorFrame Decode(byte[] p)
        {
            static short R(byte[] d, int i) => (short)(d[i] | (d[i + 1] << 8));
            return new SensorFrame(
                R(p, 0), R(p, 2), R(p, 4), R(p, 6), R(p, 8), R(p, 10),
                R(p, 12), R(p, 14), R(p, 16), R(p, 18), R(p, 20), R(p, 22));
        }
    }

    private void DecodeLoop(CircularBuffer buf, int sensorId, CancellationToken ct)
    {
        var parser = new Parser(sensorId, (id, frame) =>
        {
            FrameReceived?.Invoke(this, new SensorFrameEventArgs(id, frame));
        });

        while (!ct.IsCancellationRequested)
        {
            while (buf.TryDequeue(out var b))
                parser.Push(b);

            Thread.Sleep(2);
        }
    }
}