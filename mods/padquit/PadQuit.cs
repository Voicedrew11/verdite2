// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Diagnostics;
using System.Threading;
using ImGuiNET;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.PadQuit;

/// <summary>
/// Hold one pad button to close the game.
///
/// The first version polled from VSyncEvent. That event is the game's vblank,
/// and holding Options opens the in-game menu, whose loop does not call VSync.
/// The hold was reset every time the poll ran and never reached the toast, so
/// nothing appeared to happen.
///
/// The button itself is a host event, dispatched from the window thread on the
/// press and the release, menu or not. The press starts a wall-clock timer; the
/// release cancels it. The timer does not ask the pad again, because a second
/// read from another thread flickers and would cancel a real hold.
/// </summary>
public sealed class PadQuitMod : IMod
{
    const string PadKey = "kf2.padquit.pad";
    const string SecondsKey = "kf2.padquit.seconds";

    // SDL GameControllerButton indices. Start is Options on a DualSense and Menu
    // on an Xbox pad, the same index the game's own binding uses.
    static readonly (string Name, int Button)[] PadButtons =
    [
        ("None", -1),
        ("Start / Options / Menu", 6),
        ("L3", 7),
        ("R3", 8),
        ("Misc (share / mute)", 15),
    ];

    static int _padIndex = 1;
    static float _seconds = 3f;

    static long _heldSince;
    static bool _warned;
    static Timer _timer;

    static readonly Action<ControllerEvent> _onPad = OnPad;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _padIndex = IndexOf(view.GetString(PadKey, "Start / Options / Menu"));
        _seconds = Math.Clamp(view.GetFloat(SecondsKey, 3f), 1f, 10f);
        _heldSince = 0;
        _warned = false;
        Event.AddListener(_onPad);
        _timer = new Timer(_ => Tick(), null, 100, 100);
        Console.WriteLine("[PadQuit] hold " + PadButtons[_padIndex].Name + " for " + _seconds + "s to quit");
    }

    public void OnUnload()
    {
        Event.RemoveListener(_onPad);
        _timer.Dispose();
        _heldSince = 0;
    }

    static int IndexOf(string name)
    {
        for (int i = 0; i < PadButtons.Length; i++)
            if (PadButtons[i].Name == name) return i;
        return 1;
    }

    static void OnPad(ControllerEvent e)
    {
        int pad = PadButtons[_padIndex].Button;
        if (pad < 0 || e.Button != pad) return;

        if (!e.Pressed)
        {
            _heldSince = 0;
            _warned = false;
            return;
        }

        if (_heldSince == 0)
        {
            _heldSince = Stopwatch.GetTimestamp();
            Console.WriteLine("[PadQuit] holding " + PadButtons[_padIndex].Name);
        }
    }

    static void Tick()
    {
        long since = _heldSince;
        if (since == 0) return;

        double held = (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;

        if (!_warned && held >= _seconds / 3.0)
        {
            _warned = true;
            ToastNotifications.ShowText("Quitting", "Keep holding to close the game, or let go to stay.",
                                        null, _seconds * 2f / 3f);
        }

        if (held >= _seconds && _heldSince == since)
        {
            Console.WriteLine("[PadQuit] held for " + _seconds + "s, exiting");
            Environment.Exit(0);
        }
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Hold the button to close the game. Letting go before the time is up cancels it. "
                        + "Opening the menu with the same button does not.");
        ImGui.Separator();

        if (ImGui.BeginCombo("Quit pad button", PadButtons[_padIndex].Name))
        {
            for (int i = 0; i < PadButtons.Length; i++)
                if (ImGui.Selectable(PadButtons[i].Name, i == _padIndex))
                {
                    _padIndex = i;
                    RecompOne.Runtime.Runtime.View.SetString(PadKey, PadButtons[i].Name);
                    RecompOne.Runtime.Runtime.SaveView();
                }
            ImGui.EndCombo();
        }

        if (ImGui.SliderFloat("Hold for", ref _seconds, 1f, 10f, "%.1f s"))
        {
            RecompOne.Runtime.Runtime.View.SetFloat(SecondsKey, _seconds);
            RecompOne.Runtime.Runtime.SaveView();
        }
    }
}
