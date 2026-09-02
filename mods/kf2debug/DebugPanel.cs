// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace Kf2.Mods.Debug;

/// <summary>
/// The dockable panel.
///
/// A registered IPanel rather than everything living in IMod.DrawSettings,
/// because DrawSettings only draws while the Mods popup is open -- and a readout
/// you can only see with a modal over the game is not a readout. This docks
/// alongside CPU State and Memory Editor and stays up while you play.
/// </summary>
internal sealed class DebugPanel : IPanel
{
    internal static readonly DebugPanel Instance = new();

    public string Name => "KF2 Debug";
    public string TitleKey => "panel.kf2_debug";
    public bool IsOpen { get; set; }

    int _warpX, _warpY, _warpZ;
    int _warpArea;
    bool _coordsPrimed;

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(380, 560), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin(this.Title(), ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null)
        {
            ImGui.TextDisabled("Not running.");
            ImGui.End();
            return;
        }

        bool inGame = GameState.IsInGame(mem);
        if (!inGame)
        {
            // Max HP is zero until an area is up, which covers the title screen
            // and the attract demo. Everything here no-ops in that state anyway;
            // saying so is better than showing a panel of dead controls.
            ImGui.TextDisabled("No area running.");
            ImGui.TextWrapped("Load a save or start a game -- the player state block is cleared "
                            + "until an area is up, so there is nothing to read or change yet.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##kf2debugtabs"))
        {
            if (ImGui.BeginTabItem("Cheats"))     { DrawCheats(mem);     ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Attributes")) { DrawAttributes(mem); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Warp"))       { DrawWarp(mem);       ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("State"))      { DrawState(mem);      ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Keys"))       { DrawKeys();          ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ---- cheats ----

    void DrawCheats(IMemory mem)
    {
        bool noclip = Noclip.Enabled;
        if (ImGui.Checkbox("Noclip flight", ref noclip)) Noclip.Enabled = noclip;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fly through walls, with the body coming along. The left stick or "
                           + "D-pad moves, Space and Left Ctrl go up and down, Left Shift is fast. "
                           + "Collision is not disabled -- the position is written after the "
                           + "game's own movement, so enemies and items keep theirs.");

        if (Noclip.Enabled)
        {
            ImGui.Indent();

            // The one failure this mod cannot prevent, said plainly and where it
            // will be read. Flying past the loaded area's geometry leaves the
            // renderer walking an entity table of stale pointers, and it dies on
            // an unmapped read -- the neighbouring area's module simply is not in
            // RAM. Changing area is what the Warp tab is for.
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.72f, 0.25f, 1f));
            ImGui.TextWrapped("Stay inside the area you are in. Flying out past the loaded "
                            + "geometry crashes the renderer -- the next area is not in memory. "
                            + "Use the Warp tab to change area.");
            ImGui.PopStyleColor();

            long strayed = Noclip.DistanceFromEntry(mem);
            ImGui.Text($"{strayed:n0} units from where you switched it on");
            ImGui.SameLine();
            if (ImGui.Button("Go back")) Noclip.ReturnToEntry();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Back to where noclip was switched on -- the undo for a flight "
                               + "that went too far.");

            ImGui.SliderFloat("Speed", ref Noclip.Speed, 100f, 4000f, "%.0f units/frame");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The game's own walk speed is 200 units a frame.");
            ImGui.SliderFloat("Fast multiplier", ref Noclip.FastMultiplier, 1f, 10f, "x%.1f");

            bool invY = Noclip.InvertVertical;
            if (ImGui.Checkbox("Invert up/down", ref invY)) Noclip.InvertVertical = invY;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Which way \"up\" is on the height axis is a convention this port "
                               + "has not proven -- both known spawn points sit at a negative Y. "
                               + "Flip this if Space takes you into the floor.");

            bool invS = Noclip.InvertStrafe;
            if (ImGui.Checkbox("Invert strafe", ref invS)) Noclip.InvertStrafe = invS;

            ImGui.TextDisabled($"entered at {Noclip.Format(Noclip.EntryPosition)}");
            ImGui.Unindent();
        }

        ImGui.Separator();

        bool god = Cheats.Invincible;
        if (ImGui.Checkbox("Invincibility", ref god)) Cheats.Invincible = god;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Zeroes the damage argument to all three of the game's HP routines, "
                           + "refuses the death latch, and restores HP each frame as a backstop. "
                           + "The hit reaction still plays. Blocking the latch is what covers the "
                           + "deaths that happen at full HP -- bottomless pits, being crushed, "
                           + "and falling out of the level.");

        bool mp = Cheats.InfiniteMp;
        if (ImGui.Checkbox("Infinite MP", ref mp)) Cheats.InfiniteMp = mp;

        ImGui.Separator();

        bool speed = Cheats.SpeedEnabled;
        if (ImGui.Checkbox("Speed multiplier", ref speed)) Cheats.SpeedEnabled = speed;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scales this frame's walk speed and turn rate before the game's own "
                           + "movement reads them, so collision and animation still run. Ignored "
                           + "while noclip is on, which has its own speed.");
        if (Cheats.SpeedEnabled)
        {
            ImGui.Indent();
            ImGui.SliderFloat("Multiplier", ref Cheats.SpeedMultiplier, 0.25f, 8f, "x%.2f");
            ImGui.Unindent();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"hits blocked {Cheats.BlockedHits}, deaths refused {Cheats.BlockedDeaths}, "
                         + $"HP restores {Cheats.RestoredHp}");
    }

    // ---- attributes ----
    //
    // Two halves, and the split is the whole point of the tab: the character is
    // plain memory and a write sticks, while the seventeen combat ratings and the
    // two POWER words are a cache func_800244CC rebuilds from the equipment.
    // Attributes.cs carries the reasoning; this draws it.

    void DrawAttributes(IMemory mem)
    {
        ImGui.TextWrapped("Everything here writes the game's own state, so it is saved with your "
                        + "character and it takes effect immediately.");

        if (ImGui.Button("Full heal")) Attributes.FullHeal(mem);
        ImGui.SameLine();
        if (ImGui.Button("Cure conditions")) Attributes.CureConditions(mem);
        ImGui.SameLine();
        if (ImGui.Button("Level up")) Attributes.QueueLevelUp();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs the game's own level-up routine, so the level, both maxima, "
                           + "both base attributes and the next-level threshold all move by the "
                           + "game's table. EXP is topped up to the threshold first, since the "
                           + "routine returns early below it.");
        ImGui.SameLine();
        if (ImGui.Button("Recalculate")) Attributes.QueueRecalculate();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs func_800244CC, which rebuilds STR/MAG POWER and the seventeen "
                           + "ratings from your equipment. Press it after editing a base "
                           + "attribute to see the change on the status screen at once.");

        if (!string.IsNullOrEmpty(Attributes.Status))
            ImGui.TextDisabled(Attributes.Status);

        ImGui.Separator();

        if (ImGui.CollapsingHeader("Character", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawFields(mem, Attributes.Progress);
            ImGui.Spacing();
            DrawFields(mem, Attributes.Vitals);
            ImGui.Spacing();
            DrawFields(mem, Attributes.BaseAttributes);
            ImGui.TextWrapped("Base strength and base magic are the real attributes -- raising one "
                            + "is what makes you stronger for good. The POWER numbers below are "
                            + "these plus your equipment, and the game rewrites them.");
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Condition"))
        {
            ImGui.Indent();
            ImGui.TextWrapped("The five timers behind CONDITION on the status screen, which reads "
                            + "GOOD when all of them are zero.");
            DrawFields(mem, Attributes.Conditions);
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Combat ratings"))
        {
            ImGui.Indent();

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.72f, 0.25f, 1f));
            ImGui.TextWrapped("These nineteen words are a cache. func_800244CC zeroes them and "
                            + "rebuilds them from your equipment on every equip, item use, load "
                            + "and level-up, so a number typed here lasts seconds unless you hold "
                            + "it.");
            ImGui.PopStyleColor();

            bool locked = Attributes.LockDerived;
            if (ImGui.Checkbox("Hold these values", ref locked))
            {
                // Prime from memory first, so switching the lock on is a no-op
                // rather than a jump to whatever was last typed.
                if (locked && !Attributes.LockDerived) Attributes.PrimeHold(mem);
                Attributes.LockDerived = locked;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Rewrites all nineteen after every recompute, from a post hook on "
                               + "func_800244CC itself. Off by default -- the base attributes "
                               + "above are the honest way to change these.");

            ImGui.Spacing();
            ImGui.Text("Power");
            DrawFields(mem, Attributes.Powers);

            ImGui.Spacing();
            ImGui.Text("Offense");
            DrawFields(mem, Attributes.Offense, "off");

            ImGui.Spacing();
            ImGui.Text("Defense");
            DrawFields(mem, Attributes.Defense, "def");

            ImGui.Unindent();
        }
    }

    /// <summary>
    /// A column of InputInts over live memory. Each box reads the game every
    /// frame unless it is being edited, so a value the game changes shows here
    /// without a refresh; an edit writes straight through.
    ///
    /// When the hold is on, an edit to one of the held words updates the hold as
    /// well -- otherwise the next recompute would put the old number back and
    /// the box would look like it had ignored the typing.
    /// </summary>
    void DrawFields(IMemory mem, Attributes.Field[] fields, string id = "")
    {
        for (int i = 0; i < fields.Length; i++)
        {
            ref readonly var f = ref fields[i];
            int value = Attributes.Read(mem, f);

            ImGui.PushID($"{id}{f.Address:X8}");
            ImGui.SetNextItemWidth(140);
            if (ImGui.InputInt(f.Name, ref value))
            {
                Attributes.Write(mem, f, value);

                int held = Attributes.HeldIndex(f.Address);
                if (held >= 0) Attributes.Held[held] = Attributes.Read(mem, f);
            }
            ImGui.PopID();

            if (f.Tip.Length != 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(f.Tip);
        }
    }

    // ---- warp ----

    void DrawWarp(IMemory mem)
    {
        if (!_coordsPrimed)
        {
            // Start the boxes on where you actually are, so the first edit is a
            // nudge rather than a jump to the origin.
            (_warpX, _warpY, _warpZ) = GameState.Position(mem);
            _warpArea = mem.ReadU8(GameState.Area);
            _coordsPrimed = true;
        }

        ImGui.TextWrapped("Bookmarks are position and view angle only. They carry no area, so "
                        + "restoring one taken elsewhere puts you at those coordinates in the "
                        + "area you are actually in.");
        ImGui.Separator();

        for (int i = 0; i < Warp.SlotCount; i++)
        {
            ImGui.PushID(i);

            if (ImGui.Button("Save")) Warp.Save(i);
            ImGui.SameLine();

            if (!Warp.IsSet(i)) ImGui.BeginDisabled();
            if (ImGui.Button("Go")) Warp.Restore(i);
            ImGui.SameLine();
            if (ImGui.Button("Clear")) Warp.Clear(i);
            if (!Warp.IsSet(i)) ImGui.EndDisabled();

            ImGui.SameLine();
            if (Warp.IsSet(i)) ImGui.Text($"{i + 1}: {Noclip.Format(Warp.SlotPosition(i))}");
            else ImGui.TextDisabled($"{i + 1}: empty");

            ImGui.PopID();
        }

        ImGui.Separator();
        ImGui.Text("Coordinates");
        ImGui.InputInt("X", ref _warpX);
        ImGui.InputInt("Y (height)", ref _warpY);
        ImGui.InputInt("Z", ref _warpZ);

        if (ImGui.Button("Go to coordinates")) Warp.Teleport(_warpX, _warpY, _warpZ);
        ImGui.SameLine();
        if (ImGui.Button("Read current")) (_warpX, _warpY, _warpZ) = GameState.Position(mem);
        ImGui.SameLine();
        if (ImGui.Button("Snap to floor")) Noclip.SnapToFloor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts you on the ground at your current X/Z, using the game's own "
                           + "floor query with the player's own radius and height. This is the "
                           + "way out of a flight that ended inside a wall.");

        ImGui.Separator();
        ImGui.Text("Area");
        ImGui.TextDisabled($"currently area {mem.ReadU8(GameState.Area)}");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##area", ref _warpArea);
        _warpArea = Math.Clamp(_warpArea, 0, Kf2.AreaWarp.Reachable[^1]);
        ImGui.SameLine();
        if (ImGui.Button("Warp to area")) Warp.ToArea(_warpArea);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Queues the game's own area-entry routine -- the same call the load "
                           + "path makes -- for the end of the next player-stage. This unloads "
                           + "the current area module and reads another off the disc, so it is a "
                           + "real transition and it takes a moment. You land on the new area's "
                           + "geometry, not at the coordinates you left.");

        ImGui.TextWrapped("Areas 0-7 are the eight the game ships. Areas 8 and 9 have no data "
                        + "at all. Area 10 is cut content whose map, objects and textures are "
                        + "still on the disc and do load; only its code module is linked for a "
                        + "different build of the game, so it has no doors, levers or scripted "
                        + "triggers, and its props are drawn with the model set of the area you "
                        + "came from.");

        if (!string.IsNullOrEmpty(Warp.Status))
        {
            ImGui.Separator();
            ImGui.TextWrapped(Warp.Status);
        }
    }

    // ---- readout ----

    void DrawState(IMemory mem)
    {
        var (x, y, z) = GameState.Position(mem);
        var (pitch, yaw, roll) = GameState.Angles(mem);

        ImGui.Text("Position");
        ImGui.Indent();
        ImGui.Text($"X {x,12}   Y {y,12}   Z {z,12}");
        ImGui.Unindent();

        ImGui.Text("View");
        ImGui.Indent();
        // Degrees as well as raw: the raw value is what you compare against the
        // game's own constants, the degrees are what tells you where you face.
        ImGui.Text($"yaw   {yaw,6}  ({yaw * 360f / GameState.AngleFull,6:0.0} deg)  increases turning left");
        ImGui.Text($"pitch {pitch,6}  ({pitch * 360f / GameState.AngleFull,6:0.0} deg)  increases looking down");
        ImGui.Text($"roll  {roll,6}");
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.Text("Character");
        ImGui.Indent();
        ImGui.Text($"HP  {mem.ReadU16(GameState.Hp),5} / {mem.ReadU16(GameState.MaxHp),-5}");
        ImGui.Text($"MP  {mem.ReadU16(GameState.Mp),5} / {mem.ReadU16(GameState.MaxMp),-5}");
        ImGui.Text($"LV  {mem.ReadU8(GameState.Level),5}   EXP {mem.ReadU32(GameState.Exp)}");
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.Text("Engine");
        ImGui.Indent();
        byte state = mem.ReadU8(GameState.State);
        ImGui.Text($"action state  0x{state:X2}{(state == GameState.StateDead ? "  (dead)" : "")}");
        ImGui.Text($"area          {mem.ReadU8(GameState.Area)}");
        ImGui.Text($"save slot     {mem.ReadU8(GameState.CurrentSlot)}");
        ImGui.Text($"walk speed    {mem.ReadU32(GameState.MoveSpeed)}");
        ImGui.Text($"turn rate     {mem.ReadU32(GameState.TurnRate)}");
        ImGui.Text($"pad word      0x{mem.ReadU16(GameState.Pad):X4}");
        if (state == GameState.StateDead)
            ImGui.Text($"death frame   {mem.ReadU16(GameState.DeathFrames)}");
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.Text("Velocities");
        ImGui.Indent();
        ImGui.Text($"forward {GameState.ReadS16(mem, GameState.FwdVel),6}   " +
                   $"strafe {GameState.ReadS16(mem, GameState.StrafeVel),6}");
        ImGui.Text($"turn    {GameState.ReadS16(mem, GameState.TurnVel),6}   " +
                   $"pitch  {GameState.ReadS16(mem, GameState.PitchVel),6}");
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.TextDisabled("Inventory, equipment, magic and the entity table are still unmapped. "
                         + "Watching this panel while doing a thing in-game is how they get found.");
    }

    // ---- keys ----

    void DrawKeys()
    {
        bool on = Hotkeys.Enabled;
        if (ImGui.Checkbox("Hotkeys enabled", ref on)) Hotkeys.Enabled = on;

        ImGui.Separator();
        if (ImGui.BeginTable("##keys", 2, ImGuiTableFlags.SizingStretchProp))
        {
            Row("F2", "toggle this panel");
            Row("F3", "toggle noclip");
            Row("F4", "toggle invincibility");
            Row("F5 / F6", "save / go to bookmark 1");
            Row("F7", "snap to floor");
            Row("F8", "back to where noclip was switched on");
            Row("Space / Left Ctrl", "fly up / down");
            Row("Left Shift", "fly fast (hold)");
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextWrapped("F1 and F11 are the host's own (top bar, fullscreen) and are left alone. "
                        + "The game's default keymap uses Z X A S Q W E R F G, Enter, Right Shift "
                        + "and the arrows, so the function keys do not collide with play.");

        static void Row(string key, string what)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(key);
            ImGui.TableNextColumn(); ImGui.TextDisabled(what);
        }
    }
}
