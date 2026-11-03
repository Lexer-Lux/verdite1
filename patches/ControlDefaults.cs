using System.Numerics;
using System.Reflection;
using System.Text.Json;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Rt = RecompOne.Runtime.Runtime;

namespace Kf1;

/// <summary>Native profiles and a movement seam for independent sprint mods.</summary>
public static class ControlDefaults
{
    public const string RunKey = "kf1.controls.holdrun", SetupKey = "kf1.controls.setup";
    public static bool HoldRun;
    public static bool RequestedRun { get; private set; }
    static bool _sprinting;
    static uint _startX, _startZ;
    static readonly ModInfo Self = new() { Id = "kf1.controls", Name = "Port controls", Version = "2.0" };
    static readonly ControlSetup Setup = new();
    static Func<IMemory, bool>? _canSprint;
    static Action<RunTick>? _afterRun;
    public readonly record struct RunTick(IMemory Memory, bool Sprinting, bool Moved, float Seconds);

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
            Localization.Merge("""{"strings":{"kf1.controls.setup.title":{"en":"Control setup"}}}""");
            if (!Rt.View.Has(SetupKey) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KF1_AUTOSTART"))) Setup.Open();
        });
        HookAttach.OnOverlayLoad("held run", () =>
        {
            SymbolRegistry.Build();
            if (SymbolRegistry.Resolve("game", null, 0x800171FC) is not { } move || SymbolRegistry.Resolve("game", null, 0x80018880) is not { } stage) return false;
            HookManager.AddPre(Self, move, typeof(ControlDefaults).GetMethod(nameof(BeforeMove), BindingFlags.Public | BindingFlags.Static)!);
            HookManager.AddPre(Self, stage, typeof(ControlDefaults).GetMethod(nameof(BeforeStage), BindingFlags.Public | BindingFlags.Static)!, int.MinValue);
            HookManager.AddPost(Self, stage, typeof(ControlDefaults).GetMethod(nameof(AfterStage), BindingFlags.Public | BindingFlags.Static)!);
            HookManager.Commit();
            return HookAttach.Installed(move);
        });
    }
    public static ushort Read(IMemory memory, ushort pad)
    {
        RequestedRun = HoldRun && (pad & 0x0400) != 0; _sprinting = false;
        _startX = memory.ReadU32(AgentBeacon.PosX); _startZ = memory.ReadU32(AgentBeacon.PosZ);
        return RequestedRun ? (ushort)(pad & ~0x0040) : pad;
    }
    public static void BeforeStage(CpuContext context, IMemory memory)
    {
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
        _afterRun(new RunTick(memory, _sprinting, moved, 1f / Math.Max(1f, (float)FramePacing.LogicHz)));
    }
    public static void Draw()
    {
        if (ImGui.Button("Run control setup again")) Setup.Open();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Choose the baseline upstream port controls, Modern Controls, or keep your current bindings. Opening setup changes nothing.");
    }
    sealed class ControlSetup : Popup
    {
        protected override string TitleKey => "kf1.controls.setup.title";
        protected override Vector2 Size => new(620, 0);
        int _choice;
        bool _replaceCustom, _custom;
        string? _error;
        protected override void OnOpened()
        {
            _choice = 0; _replaceCustom = false; _error = null;
            try { _custom = DetectProfile() == "custom"; } catch (Exception ex) { _error = ex.Message; }
        }
        protected override void DrawContent()
        {
            ImGui.RadioButton("Keep current bindings", ref _choice, 0);
            ImGui.RadioButton("Classic Controls", ref _choice, 1);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The unmodified archive/recomp upstream controls, including its WASD keyboard and Western face-button layout. No held run.");
            ImGui.RadioButton("Modern Controls", ref _choice, 2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RT/RB attack; LT/LB cast; Cross/E interact; Circle/Left Shift held runs. Original Square attack, Triangle item/spell, Select menu, Tab menu and Q item/spell stay available. Menu confirm/back stay Cross/Circle.");
            if (_custom && _choice != 0) ImGui.Checkbox("Replace my custom bindings with this preset", ref _replaceCustom);
            if (_error != null) ImGui.TextWrapped(_error);
            ImGui.BeginDisabled((_custom && _choice != 0 && !_replaceCustom) || (_error != null && _choice != 0));
            if (ImGui.Button("Finish", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                try
                {
                    if (_choice != 0) ApplyProfile(_choice == 1 ? "classic" : "modern");
                    else { Rt.View.SetInt(SetupKey, 1); Rt.SaveView(); }
                    Close();
                }
                catch (Exception ex) { _error = ex.Message; }
            }
            ImGui.EndDisabled();
        }
    }
}
