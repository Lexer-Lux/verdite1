using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Game = Recompiled.KingsField1_game;
using Rt = RecompOne.Runtime.Runtime;

namespace Kf1;

/// <summary>SLPS-00017: replace ordinary respawn with the last successful native save/load.</summary>
public sealed class AutoReload : ISettingsSection
{
    public const string Key = "kf1.autoreload.enabled";
    public static bool Enabled { get; private set; } = true;
    public static int LastSlot { get; private set; } = -1;
    public static long Reloads { get; private set; }
    static byte _card;
    static byte[]? _header;
    static readonly Stack<(uint Slot, byte Card)> Saves = new(), Loads = new();
    static readonly ModInfo Self = new() { Id = "kf1.autoreload", Name = "Auto reload", Version = "1.0" };
    public string Id => "kf1gameplay";
    public string TitleKey => "settings.kf1gameplay";
    public int Order => 12;

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = Rt.View.GetInt(Key, 1) != 0;
            Localization.Merge("""{"strings":{"settings.kf1gameplay":{"en":"Gameplay","pt-BR":"Jogabilidade","es-419":"Juego"}}}""");
            SettingsRegistry.Register(new AutoReload());
        });
        HookAttach.OnOverlayLoad("auto reload", () =>
        {
            SymbolRegistry.Build();
            var hooks = new (uint Address, string? Pre, string? Post)[] {
                (0x800154B0, nameof(BeforeRespawn), null),
                (0x8002B73C, nameof(BeforeSave), nameof(AfterSave)),
                (0x8002BEB0, nameof(BeforeLoad), nameof(AfterLoad)),
                (0x800151CC, null, nameof(AfterInitialize))
            };
            var targets = hooks.Select(h => SymbolRegistry.Resolve("game", null, h.Address)).ToArray();
            if (targets.Any(t => t == null)) return false;
            for (int i = 0; i < hooks.Length; i++)
            {
                if (hooks[i].Pre is { } pre) HookManager.AddPre(Self, targets[i]!, Method(pre));
                if (hooks[i].Post is { } post) HookManager.AddPost(Self, targets[i]!, Method(post));
            }
            HookManager.Commit();
            return targets.All(HookAttach.Installed);
        });
    }

    static MethodInfo Method(string name) => typeof(AutoReload).GetMethod(name)!;
    public static void SetEnabled(bool value)
    {
        Enabled = value;
        Rt.View.SetInt(Key, value ? 1 : 0);
        Rt.SaveView();
    }

    public void Draw()
    {
        bool value = Enabled;
        if (ImGui.Checkbox("Reload last save after death", ref value)) SetEnabled(value);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Uses the last slot saved or loaded this session. Revival items still work.\nWithout a usable save, the normal death sequence continues.");
    }

    public static void BeforeSave(CpuContext c, IMemory m) => Saves.Push((c.A0, m.ReadU8(0x80056036)));
    public static void BeforeLoad(CpuContext c, IMemory m) => Loads.Push((c.A0, m.ReadU8(0x80056036)));
    public static void AfterSave(CpuContext c, IMemory m) => Remember(c, m, Saves);
    public static void AfterLoad(CpuContext c, IMemory m) => Remember(c, m, Loads);
    static void Remember(CpuContext c, IMemory m, Stack<(uint Slot, byte Card)> pending)
    {
        if (!pending.TryPop(out var io) || c.V0 != 1 || io.Slot is < 1 or > 3) return;
        LastSlot = (int)io.Slot;
        _card = io.Card;
        // Both native menu wrappers keep these buffers in their own stack frames.
        // Capture while that frame still exists, never retain its dangling guest pointer.
        uint header = m.ReadU32(0x800668D8);
        _header = new byte[0x280];
        for (uint i = 0; i < _header.Length; i++) _header[i] = m.ReadU8(header + i);
    }
    public static void AfterInitialize(CpuContext c, IMemory m)
    {
        // The same initializer also serves ordinary respawn; only a new character resets the slot.
        if (c.RA == 0x80016E7C) LastSlot = -1;
    }

    public static bool BeforeRespawn(CpuContext c, IMemory m)
    {
        if (!Enabled || LastSlot < 0 || _header == null || AgentBeacon.Overlay != "game" || c.RA != 0x8001869C
            || m.ReadU8(0x800A0822) != 255 || m.ReadU16(0x800A0792) != 0) return true;
        // Retail's Dragon King Grass Fruit revival has priority, exactly as in the original.
        if (m.ReadU8(0x8009DDB7) == 1 && m.ReadU8(0x800652D7) != 0) return true;

        var saved = c.Snapshot();
        uint oldHeader = m.ReadU32(0x800668D8), oldPayload = m.ReadU32(0x800668E0);
        byte oldCard = m.ReadU8(0x80056036);
        try
        {
            // The menus reserve 0x280 bytes of header and 0x2580 of payload.
            // Own that storage for the entire reader call, below the interrupted frame.
            c.SP -= 0x3000;
            uint header = c.SP + 0x10, payload = c.SP + 0x290;
            for (uint i = 0; i < _header.Length; i++) m.WriteU8(header + i, _header[i]);
            m.WriteU32(0x800668D8, header);
            m.WriteU32(0x800668E0, payload);
            m.WriteU8(0x80056036, _card);
            c.A0 = (uint)LastSlot;
            Game.func_8002BEB0(c, m);
            // The reader checks the card header and finishes its reads before changing player state.
            // Failure leaves the original respawn free to run; no menu or synthetic input is involved.
            if (c.V0 != 1)
            {
                Console.Error.WriteLine($"[KF1] auto reload: native reader returned {c.V0}; continuing ordinary respawn");
                return true;
            }

            // Stage A's native "menu loaded a save" arm, 0x800189A4..0x80018A04.
            Game.func_80020A2C(c, m);
            Game.func_80032CB0(c, m);
            Game.func_800365F8(c, m);
            Game.func_80016EE8(c, m);
            m.WriteU8(0x800A084D, m.ReadU8(0x800A084B));
            m.WriteU8(0x800A084C, m.ReadU8(0x800A084A));
            c.A0 = m.ReadU8(0x800A07E4);
            Game.func_80016A30(c, m);
            c.A0 = m.ReadU8(0x800A07DC);
            Game.func_800167E4(c, m);

            // Reuse the original respawn's fade back from black. Keep saved stats and conditions.
            m.WriteU8(0x800A0822, 254);
            m.WriteU16(0x800A0854, 0);
            m.WriteU16(0x800A0858, 0);
            m.WriteU16(0x800A085A, 0);
            m.WriteU8(0x80095064, 0);
            m.WriteU16(0x800A081A, 0);
            m.WriteU16(0x800A081C, 0);
            m.WriteU16(0x800A081E, 0);
            m.WriteU32(0x800A0828, m.ReadU32(0x800A0834) - 1500);
            Reloads++;
            Console.WriteLine($"[KF1] auto reload: native slot {LastSlot}");
            return false;
        }
        finally
        {
            m.WriteU32(0x800668D8, oldHeader);
            m.WriteU32(0x800668E0, oldPayload);
            m.WriteU8(0x80056036, oldCard);
            c.Restore(saved);
        }
    }
}
