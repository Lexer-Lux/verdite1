using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Rt = RecompOne.Runtime.Runtime;

namespace Kf1;

/// <summary>Port controls, not a runtime mod. R3 is the otherwise unused run action.</summary>
public static class ControlDefaults
{
    public const string RunKey = "kf1.controls.holdrun";
    public static bool HoldRun = true;
    static bool _running;
    static readonly ModInfo Self = new() { Id = "kf1.controls", Name = "Port controls", Version = "1.0" };

    public static void Apply()
    {
        var keys = KeyLayout.Layout();
        keys.Circle = "E";
        keys.R3 = "ShiftLeft";
        ConfigManager.Game.Keys = keys;
        foreach (var pad in new[] { ConfigManager.Game.Pad, ConfigManager.Game.PadAnalog })
        {
            // Western permutation: Square -> attack; Triangle -> item or spell.
            pad.Cross = [0];
            pad.Circle = [1, 3]; // Circle backs out in menus; Triangle opens the menu.
            pad.Square = [10, 101];
            pad.Triangle = [9, 100, 2];
            pad.L1 = []; pad.R1 = []; pad.L2 = []; pad.R2 = [];
            pad.R3 = [1];
        }
        PadLayout.Western = true;
        Rt.View.SetInt(PadLayout.Key, 1);
        Rt.View.SetInt(RunKey, 1);
        HoldRun = true;
        ConfigManager.SaveGame();
        Rt.SaveView();
    }

    public static void Install()
    {
        RecompOne.Runtime.Events.Event.AddListener<RecompOne.Runtime.Events.RuntimeReadyEvent>(_ =>
            HoldRun = Rt.View.GetInt(RunKey, 1) != 0);
        HookAttach.OnOverlayLoad("hold to run", () =>
        {
            SymbolRegistry.Build();
            if (SymbolRegistry.Resolve("game", null, 0x800171FC) is not { } move) return false;
            HookManager.AddPre(Self, move, typeof(ControlDefaults).GetMethod(nameof(BeforeMove), BindingFlags.Public | BindingFlags.Static)!);
            HookManager.Commit();
            return HookAttach.Installed(move);
        });
    }

    public static ushort Read(ushort pad)
    {
        _running = HoldRun && (pad & 0x0400) != 0;
        // Circle also maps to menu back. Suppress that action only in gameplay
        // while run is held; modal menus keep their own native confirm/back.
        return _running ? (ushort)(pad & ~0x0040) : pad;
    }

    public static void BeforeMove(CpuContext context, IMemory memory)
    {
        if (!_running || AgentBeacon.Overlay != "game" || memory.ReadU16(AgentBeacon.Hp) == 0) return;
        int distance = (int)context.A1;
        if (distance > 0) context.A1 = (uint)Math.Min(distance * 2L, 0x7FFF);
    }

    public static void Draw()
    {
        if (ImGui.Button("Apply Lexer's controls")) Apply();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("RT/RB attack; LT/LB cast or use the held item; Cross/E interact; Circle/Left Shift held runs. Triangle/Tab opens the menu; Square/Q uses the held item or spell. Cross confirms and Circle backs out in menus.");
        bool run = HoldRun;
        if (ImGui.Checkbox("Hold to run", ref run))
        {
            HoldRun = run;
            Rt.View.SetInt(RunKey, run ? 1 : 0);
            Rt.SaveView();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Doubles movement while the R3 action is held. The preset binds Circle and Left Shift to it. Collision still uses the game's own routine.");
    }
}
