using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using Rt = RecompOne.Runtime.Runtime;

namespace Kf1;

/// <summary>Native profiles and a movement seam for independent sprint mods.</summary>
public static class ControlDefaults
{
    public const string RunKey = "kf1.controls.holdrun", SetupKey = "kf1.controls.setup";
    public static bool HoldRun;
    public static bool RequestedRun { get; private set; }
    static bool _sprinting;
    static bool _inputSampled;
    static bool _menuReading, _menuInheritedRun;
    static uint _startX, _startZ;
    static readonly ModInfo Self = new() { Id = "kf1.controls", Name = "Port controls", Version = "2.0" };
    static int _profileChoice;
    static bool _replaceCustom;
    static string? _profileError;
    static Func<IMemory, bool>? _canSprint;
    static Action<RunTick>? _afterRun;
    public readonly record struct RunTick(IMemory Memory, bool Sprinting, bool Moved, float Seconds)
    {
        // A skipped render iteration is not a released sprint input.
        public bool InputSampled { get; init; }
        public bool Requested { get; init; }
    }

    public static void SetSprintPolicy(Func<IMemory, bool> canSprint, Action<RunTick> afterRun)
    {
        if (_canSprint != null) throw new InvalidOperationException("A sprint policy is already registered");
        _canSprint = canSprint; _afterRun = afterRun;
    }
    public static void RemoveSprintPolicy(Func<IMemory, bool> canSprint)
    {
        if (!ReferenceEquals(_canSprint, canSprint)) return;
        _canSprint = null; _afterRun = null;
    }

    public static string ProfilesPath => Path.Combine(AppContext.BaseDirectory, "content", "config", "controls.json");
    static JsonDocument Profiles()
    {
        var document = JsonDocument.Parse(File.ReadAllText(ProfilesPath));
        if (document.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported control profile version");
        return document;
    }
    static bool Same<T>(T current, JsonElement stored) => JsonSerializer.Serialize(current) == JsonSerializer.Serialize(stored.Deserialize<T>());
    public static string DetectProfile()
    {
        using var profiles = Profiles();
        foreach (string name in new[] { "classic", "modern" })
        {
            var p = profiles.RootElement.GetProperty(name);
            if (Same(ConfigManager.Game.Keys, p.GetProperty("Keys")) && Same(ConfigManager.Game.Keys2, p.GetProperty("Keys2")) &&
                Same(ConfigManager.Game.Pad, p.GetProperty("Pad")) && Same(ConfigManager.Game.PadAnalog, p.GetProperty("PadAnalog")) &&
                Rt.View.GetInt(PadLayout.Key, 1) == 1 && HoldRun == (name == "modern")) return name;
        }
        return "custom";
    }
    public static void ApplyProfile(string name)
    {
        if (name is not ("classic" or "modern")) throw new ArgumentException("Choose Classic Controls or Modern Controls");
        using var profiles = Profiles();
        var p = profiles.RootElement.GetProperty(name);
        ConfigManager.Game.Keys = p.GetProperty("Keys").Deserialize<KeyBindings>()!;
        ConfigManager.Game.Keys2 = p.GetProperty("Keys2").Deserialize<KeyBindings>()!;
        ConfigManager.Game.Pad = p.GetProperty("Pad").Deserialize<GamepadBindings>()!;
        ConfigManager.Game.PadAnalog = p.GetProperty("PadAnalog").Deserialize<GamepadBindings>()!;
        foreach (var field in p.GetProperty("view").EnumerateObject()) Rt.View.SetString(field.Name, field.Value.GetString()!);
        PadLayout.Western = true; HoldRun = name == "modern";
        Rt.View.SetInt(SetupKey, 1);
        ConfigManager.SaveGame(); Rt.SaveView();
    }
    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            HoldRun = Rt.View.GetInt(RunKey, 0) != 0;
            SettingsRegistry.Extend("input.profiles", Draw);
            if (!Rt.View.Has(SetupKey) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KF1_AUTOSTART"))) OpenSetup();
        });
        HookAttach.OnOverlayLoad("held run", () =>
        {
            SymbolRegistry.Build();
            if (SymbolRegistry.Resolve("game", null, 0x800171FC) is not { } move || SymbolRegistry.Resolve("game", null, 0x80018880) is not { } stage
                || SymbolRegistry.Resolve("game", null, 0x80036E38) is not { } menu) return false;
            HookManager.AddPre(Self, move, typeof(ControlDefaults).GetMethod(nameof(BeforeMove), BindingFlags.Public | BindingFlags.Static)!);
            HookManager.AddPre(Self, stage, typeof(ControlDefaults).GetMethod(nameof(BeforeStage), BindingFlags.Public | BindingFlags.Static)!, int.MinValue);
            HookManager.AddPost(Self, stage, typeof(ControlDefaults).GetMethod(nameof(AfterStage), BindingFlags.Public | BindingFlags.Static)!);
            HookManager.AddPre(Self, menu, typeof(ControlDefaults).GetMethod(nameof(BeforeMenu))!, int.MinValue);
            HookManager.AddPost(Self, menu, typeof(ControlDefaults).GetMethod(nameof(AfterMenu))!, int.MaxValue);
            HookManager.Commit();
            return HookAttach.Installed(move) && HookAttach.Installed(stage) && HookAttach.Installed(menu);
        });
    }
    public static ushort Read(IMemory memory, ushort pad)
    {
        _inputSampled = true;
        _menuReading = _menuInheritedRun = false;
        RequestedRun = HoldRun && (pad & 0x0400) != 0; _sprinting = false;
        _startX = memory.ReadU32(AgentBeacon.PosX); _startZ = memory.ReadU32(AgentBeacon.PosZ);
        return RequestedRun && !IndependentMenuHeld() ? (ushort)(pad & ~0x0040) : pad;
    }

    public static void BeforeMenu(CpuContext c, IMemory m)
    { _menuReading = true; _menuInheritedRun = RequestedRun; }
    public static void AfterMenu(CpuContext c, IMemory m)
    { _menuReading = _menuInheritedRun = false; }

    public static ushort ReadModal(ushort pad)
    {
        if (!HoldRun) return pad;
        if ((pad & 0x0400) == 0) _menuInheritedRun = false;
        // Ignore only the inherited run gesture, not a new Cancel press inside
        // the menu. Independent Tab/Start remains usable while Run stays held.
        if (_menuReading && _menuInheritedRun && !IndependentMenuHeld()) pad &= unchecked((ushort)~0x0040);
        return (ushort)(pad & ~0x0400); // R3 has no native menu action.
    }

    static bool IndependentMenuHeld()
    {
        // The face button shared with Run must not open a menu by itself, but
        // an independently bound menu key/Start must still work while running.
        var keys = ConfigManager.Game.Keys;
        if (keys.Cross != keys.R3 && Enum.TryParse<Key>(keys.Cross, out var key) && HostWindow.IsKeyDown(key)) return true;
        var buttons = ConfigManager.Game.PadFor(0);
        foreach (int button in PadLayout.Western ? buttons.Circle : buttons.Cross)
            if (!buttons.R3.Contains(button) && HostWindow.IsPadButtonDown(button)) return true;
        return false;
    }
    public static void BeforeStage(CpuContext context, IMemory memory)
    {
        _inputSampled = false;
        RequestedRun = false; _sprinting = false;
        _startX = memory.ReadU32(AgentBeacon.PosX); _startZ = memory.ReadU32(AgentBeacon.PosZ);
    }
    public static void BeforeMove(CpuContext context, IMemory memory)
    {
        if (!RequestedRun || AgentBeacon.Overlay != "game" || memory.ReadU16(AgentBeacon.Hp) == 0) return;
        if (_canSprint != null && !_canSprint(memory)) return;
        int distance = (int)context.A1;
        if (distance > 0) { context.A1 = (uint)Math.Min(distance * 2L, 0x7FFF); _sprinting = true; }
    }
    public static void AfterStage(CpuContext context, IMemory memory)
    {
        if (_afterRun == null) return;
        bool moved = memory.ReadU32(AgentBeacon.PosX) != _startX || memory.ReadU32(AgentBeacon.PosZ) != _startZ;
        _afterRun(new RunTick(memory, _sprinting, moved, 1f / Math.Max(1f, (float)FramePacing.LogicHz))
            { InputSampled = _inputSampled, Requested = RequestedRun });
    }
    public static void OpenSetup()
    {
        _profileChoice = 0; _replaceCustom = false; _profileError = null;
        PopupManager.Get<SettingsPopup>()?.OpenSection("input");
    }

    public static void Draw()
    {
        bool custom;
        try { custom = DetectProfile() == "custom"; }
        catch (Exception ex) { _profileError = ex.Message; custom = true; }
        ImGui.RadioButton("Keep current bindings", ref _profileChoice, 0);
        ImGui.RadioButton("Classic Controls", ref _profileChoice, 1);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The baseline upstream port bindings. Held run is off.");
        ImGui.RadioButton("Modern Controls", ref _profileChoice, 2);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("RT/RB attack; LT/LB cast; Cross/E interact; Circle/Left Shift held run.\nStart/Select/Tab open the menu. Triangle/Q uses the equipped item or spell, including maps.");
        if (custom && _profileChoice != 0) ImGui.Checkbox("Replace my custom bindings with this preset", ref _replaceCustom);
        if (_profileError != null) ImGui.TextWrapped(_profileError);
        ImGui.BeginDisabled((custom && _profileChoice != 0 && !_replaceCustom) || (_profileError != null && _profileChoice != 0));
        if (ImGui.Button("Apply control profile"))
        {
            try
            {
                if (_profileChoice != 0) ApplyProfile(_profileChoice == 1 ? "classic" : "modern");
                else { Rt.View.SetInt(SetupKey, 1); Rt.SaveView(); }
                _profileError = null; _replaceCustom = false; _profileChoice = 0;
            }
            catch (Exception ex) { _profileError = ex.Message; }
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
    }
}
