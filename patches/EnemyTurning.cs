using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf1;

/// <summary>Compensate only creatures' angular approach steps for faster stick yaw.</summary>
public static class EnemyTurning
{
    static readonly ModInfo Self = new() { Id = "kf1.enemyturning", Name = "Stick turn compensation", Version = "1.0" };

    // Sensitivity 1 reaches the original 28 yaw units/tick. Analog caps stick
    // yaw at four times that rate. Saved values and disabled-stick modes count;
    // pitch, mouse motion, stick deflection and optional acceleration do not.
    public static float Ratio => Analog.Enabled && (Analog.Tank ? Analog.AnalogMove : Analog.AnalogLook)
        && float.IsFinite(Analog.TurnSens) ? Math.Clamp(Analog.TurnSens, 1f, Analog.OverspeedCap) : 1f;

    public static void Install() => HookAttach.OnOverlayLoad("enemy turn compensation", () =>
    {
        SymbolRegistry.Build();
        if (SymbolRegistry.Resolve("game", null, 0x80014A64) is not { } target) return false;
        HookManager.AddPre(Self, target, typeof(EnemyTurning).GetMethod(nameof(BeforeApproach), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.Commit();
        bool installed = HookAttach.Installed(target);
        Console.WriteLine($"[KF1] enemy turn compensation: {(installed ? "attached" : "not installed")}");
        return installed;
    });

    public static void BeforeApproach(CpuContext c, IMemory memory)
    {
        // actor_move_along_heading: sliding (floor(3*typeRate/2)), normal;
        // actor return-home: typeRate. All other callers include doors/effects.
        if (c.RA is not (0x8002ED58 or 0x8002ED74 or 0x800304A0)) return;
        // Original rates are u8, at most 382 after sliding's adjustment. Leave
        // unexpected arguments alone, including another mod's replacement.
        if (c.A2 > 382) return;
        c.A2 = (uint)MathF.Round(c.A2 * Ratio, MidpointRounding.AwayFromZero);
    }
}
