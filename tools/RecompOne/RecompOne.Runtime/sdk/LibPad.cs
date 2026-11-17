using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibPad
{
    private const byte Connected = 0x00;
    private const byte Disconnected = 0xFF;
    private const byte DigitalId = 0x41;
    private const uint PadStateDiscon = 0;
    private const uint PadStateStable = 6;

    private static readonly PadReadEvent _padEvent = new();
    private static uint _buf1;
    private static uint _buf2;
    private static int _smallMotorIdx = 0;
    private static int _largeMotorIdx = 1;

    //0122. The actuator buffer PadSetAct registers, sent again on every refresh as the
    //pad driver sends it every vblank; the motors last sent and when.
    private const long RumbleResendMs = 250;
    private static uint _actPtr;
    private static uint _actLen;
    private static bool _aligned;
    private static (byte Large, byte Small) _rumbleSent;
    private static long _rumbleSentAt;

    internal static void Reset()
    {
        _buf1 = 0;
        _buf2 = 0;
        _actPtr = 0;
        _aligned = false;
    }

    internal static void Detach()
    {
        _buf1 = 0;
        _buf2 = 0;
        _actPtr = 0;
        _aligned = false;
    }

    public static void PadInitDirect(CpuContext c, IMemory m)
    {
        _buf1 = c.A0;
        _buf2 = c.A1;
        Log.Sdk($"PadInitDirect buf1=0x{_buf1:X8} buf2=0x{_buf2:X8}");
        c.V0 = 0;
    }

    public static void PadStartCom(CpuContext c, IMemory m)
    {
        Refresh(m);
        c.V0 = 0;
    }

    public static void PadStopCom(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadEnableCom(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadChkVsync(CpuContext c, IMemory m)
    {
        c.V0 = 1;
    }

    public static void PadChkMtap(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadGetState(CpuContext c, IMemory m)
    {
        c.V0 = IsPort1(c.A0) || Controller.Connected2 ? PadStateStable : PadStateDiscon;
    }

    public static void PadInfoMode(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadInfoComb(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadInfoAct(CpuContext c, IMemory m)
    {
        //0122. PadInfoAct(port, acno, term): acno -1 asks how many actuators the pad
        //has, and a DualShock has two.
        c.V0 = (int)c.A1 == -1 || (int)c.A2 < 0 ? 2u : 1u;
    }

    public static void PadSetMainMode(CpuContext c, IMemory m)
    {
        c.V0 = 0;
    }

    public static void PadSetActAlign(CpuContext c, IMemory m)
    {
        if (!IsPort1(c.A0))
        {
            c.V0 = 1;
            return;
        }

        //0117. PadSetActAlign(port, data) has two arguments and data is always six
        //bytes; A2 is whatever the caller left there.
        var ptr = c.A1;
        if (ptr == 0)
        {
            c.V0 = 0;
            return;
        }

        for (var i = 0; i < 6; i++)
        {
            var v = m.ReadU8(ptr + (uint)i);
            if (v == 0x00) _smallMotorIdx = i;
            else if (v == 0x01) _largeMotorIdx = i;
        }
        _aligned = true;

        c.V0 = 1;
    }

    public static void PadSetAct(CpuContext c, IMemory m)
    {
        if (!IsPort1(c.A0))
        {
            c.V0 = 1;
            return;
        }

        var ptr = c.A1;
        var len = c.A2;
        if (ptr == 0 || len == 0)
        {
            c.V0 = 0;
            return;
        }

        _actPtr = ptr;
        _actLen = len;
        SendActuators(m, force: true);
        c.V0 = 1;
    }

    //0122. A game may call PadSetAct once and then only write the motors into its
    //buffer, as the real driver reads it every vblank; so the buffer is read again
    //on every refresh, and sent when it changes or, while a motor runs, every 250 ms
    //(SDL's rumble is given 500 ms). Nothing is sent before PadSetActAlign: until then
    //the pad has no motor tied to any byte of the buffer.
    private static void SendActuators(IMemory m, bool force)
    {
        if (_actPtr == 0 || !_aligned) return;
        var small = _smallMotorIdx < (int)_actLen ? m.ReadU8(_actPtr + (uint)_smallMotorIdx) : (byte)0;
        var large = _largeMotorIdx < (int)_actLen ? m.ReadU8(_actPtr + (uint)_largeMotorIdx) : (byte)0;
        var now = Environment.TickCount64;
        var running = large != 0 || small != 0;
        if (!force && (large, small) == _rumbleSent && !(running && now - _rumbleSentAt >= RumbleResendMs)) return;
        if ((large, small) != _rumbleSent) Log.Sdk($"PadSetAct motors large={large} small={small}");
        InputManager.SetRumble(large, small);
        _rumbleSent = (large, small);
        _rumbleSentAt = now;
    }

    //0121. The hardware fills these buffers from the vblank interrupt, so a game can
    //wait for a button in a loop that never calls VSync. Refresh from the vblank too.
    private const double PadPumpIntervalMs = 4.0;

    internal static void OnVBlank(IMemory m)
    {
        if (_buf1 == 0 && _buf2 == 0) return;
        HostWindow.PumpInput(PadPumpIntervalMs);
        Refresh(m);
    }

    public static void Refresh(IMemory m)
    {
        SendActuators(m, force: false);
        if (_buf1 != 0)
            WritePad(m, _buf1, FirePad(m, 0, Controller.State), true,
                Controller.RightX, Controller.RightY, Controller.LeftX, Controller.LeftY);
        if (_buf2 != 0)
            WritePad(m, _buf2, FirePad(m, 1, Controller.State2), Controller.Connected2,
                Controller.RightX2, Controller.RightY2, Controller.LeftX2, Controller.LeftY2);
    }

    //0120. The same event, and the same swapped layout, as the BIOS pad paths, so a
    //listener written for PAD_dr works on a game that links this library too.
    private static ushort FirePad(IMemory m, int port, ushort buttons)
    {
        if (!Event.HasAnyListeners<PadReadEvent>() || Runtime.Cpu == null) return buttons;
        var e = _padEvent;
        e.Context = Runtime.Cpu;
        e.Memory = m;
        e.Port = port;
        e.Buttons = (ushort)((buttons >> 8) | (buttons << 8));
        Event.Dispatch(e);
        return (ushort)((e.Buttons >> 8) | (e.Buttons << 8));
    }

    private static bool IsPort1(uint port)
    {
        return (port & 0x10u) == 0;
    }

    private static void WritePad(IMemory m, uint buf, ushort buttons, bool present, byte rx, byte ry, byte lx, byte ly)
    {
        m.WriteU8(buf + 0, present ? Connected : Disconnected);
        m.WriteU8(buf + 1, present ? DigitalId : Disconnected);
        m.WriteU8(buf + 2, (byte)(buttons & 0xFF));
        m.WriteU8(buf + 3, (byte)(buttons >> 8));
        m.WriteU8(buf + 4, present ? rx : (byte)0x80);
        m.WriteU8(buf + 5, present ? ry : (byte)0x80);
        m.WriteU8(buf + 6, present ? lx : (byte)0x80);
        m.WriteU8(buf + 7, present ? ly : (byte)0x80);
    }
}