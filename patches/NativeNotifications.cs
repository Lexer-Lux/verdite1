using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf1;

/// <summary>Custom words in KF1's original notification queue and rotating sprite renderer.
/// Glyph codes refer to the player's COM/MIX.TIM menu atlas, never a host font.</summary>
public static class NativeNotifications
{
    const uint Queue = 0x8009506E, Payload = 0x80095076;
    const uint Tail = 0x80095086, Head = 0x80095087, Phase = 0x80095088;
    const uint Sprites = 0x80055D20, Font = 0x800583F4, Material = 0x80095058;
    const byte Empty = 255, Gold = 19;
    public const int Capacity = 16, MaximumGlyphs = 18;
    sealed record Entry(string Owner, ushort[] Glyphs);
    static readonly object Gate = new();
    static readonly List<Entry> Pending = new();
    static readonly Dictionary<int, Entry> Owned = new();
    static readonly HashSet<string> Removed = new(StringComparer.Ordinal);
    static bool _drawing, _installed;
    public static int Generation { get; private set; }
    public static int Count { get { lock (Gate) return Pending.Count + Owned.Count; } }
    static readonly ModInfo Self = new() { Id = "kf1.native-notifications", Name = "Native notifications", Version = "1.0" };

    /// <summary>Accept at most 16 outstanding lines, each at most 18 original menu glyphs.
    /// Returns false when full; callers can retry without counting a transition as delivered.</summary>
    public static bool Enqueue(string owner, ReadOnlySpan<ushort> glyphs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (glyphs.Length is < 1 or > MaximumGlyphs) throw new ArgumentOutOfRangeException(nameof(glyphs));
        foreach (ushort glyph in glyphs)
            if ((glyph & 0xC000) != 0 || (glyph & 0xFFF) > 0x14F)
                throw new ArgumentOutOfRangeException(nameof(glyphs), "Not a KF1 menu atlas glyph.");
        lock (Gate)
        {
            if (Pending.Count + Owned.Count >= Capacity) return false;
            Pending.Add(new Entry(owner, glyphs.ToArray()));
            return true;
        }
    }

    /// <summary>Cancel this owner's lines. Native slots are compacted on the next game render,
    /// preserving other notifications and preventing empty holes from blocking the native queue.</summary>
    public static void Clear(string owner)
    {
        lock (Gate)
        {
            Pending.RemoveAll(e => e.Owner == owner);
            if (Owned.Values.Any(e => e.Owner == owner)) Removed.Add(owner);
        }
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Event.AddListener<OverlayLoadedEvent>(_ => Forget());
        InteractionHistory.GameRestored += () =>
        {
            if (RecompOne.Runtime.Runtime.Mem is { } memory) Reset(memory);
            else Forget();
        };
        HookAttach.OnOverlayLoad("native notifications", () =>
        {
            SymbolRegistry.Build();
            uint[] addresses = [0x8001FAFC, 0x8001E230];
            var targets = addresses.Select(a => SymbolRegistry.Resolve("game", null, a)).ToArray();
            if (targets.Any(t => t == null)) return false;
            MethodInfo M(string name) => typeof(NativeNotifications).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            HookManager.AddPre(Self, targets[0]!, M(nameof(BeforeUpdate)), int.MinValue);
            HookManager.AddPost(Self, targets[0]!, M(nameof(AfterUpdate)), int.MaxValue);
            HookManager.AddPre(Self, targets[1]!, M(nameof(BeforeSprite)), int.MinValue);
            HookManager.Commit();
            return targets.All(HookAttach.Installed);
        });
    }

    static void Forget()
    {
        lock (Gate) { Pending.Clear(); Owned.Clear(); Removed.Clear(); Generation++; }
    }

    public static void Reset(IMemory m)
    {
        lock (Gate)
        {
            foreach (Entry e in Owned.Values) Removed.Add(e.Owner);
            Compact(m);
            Forget();
        }
    }

    static bool IsOwned(IMemory m, int slot) => Owned.ContainsKey(slot) && m.ReadU8(Queue + (uint)slot) == Gold;

    static void Compact(IMemory m)
    {
        if (Removed.Count == 0) return;
        int tail = m.ReadU8(Tail) & 7;
        bool activeRemoved = IsOwned(m, tail) && Removed.Contains(Owned[tail].Owner);
        var kept = new List<(byte Id, ushort Payload, Entry? Owner)>(8);
        for (int n = 0; n < 8; n++)
        {
            int slot = (tail + n) & 7;
            byte id = m.ReadU8(Queue + (uint)slot);
            if (id == Empty) continue;
            Entry? owner = IsOwned(m, slot) ? Owned[slot] : null;
            if (owner != null && Removed.Contains(owner.Owner)) continue;
            kept.Add((id, m.ReadU16(Payload + (uint)slot * 2), owner));
        }
        Owned.Clear();
        for (int n = 0; n < 8; n++) m.WriteU8(Queue + (uint)n, Empty);
        for (int n = 0; n < kept.Count; n++)
        {
            int slot = (tail + n) & 7;
            var item = kept[n];
            m.WriteU8(Queue + (uint)slot, item.Id);
            m.WriteU16(Payload + (uint)slot * 2, item.Payload);
            if (item.Owner != null) Owned[slot] = item.Owner;
        }
        m.WriteU8(Head, (byte)((tail + kept.Count) & 7));
        if (activeRemoved)
        {
            m.WriteU8(Phase, 0);
            HideSprites(m);
        }
        Removed.Clear();
    }

    public static void BeforeUpdate(CpuContext c, IMemory m)
    {
        lock (Gate)
        {
            Compact(m);
            while (Pending.Count != 0)
            {
                int slot = m.ReadU8(Head) & 7;
                if (m.ReadU8(Queue + (uint)slot) != Empty) break;
                var saved = c.Snapshot();
                try
                {
                    c.SP -= 0x100;
                    // GOLD is an existing, valid native atlas ID. Its dequeue path never
                    // groups adjacent equal IDs. No unknown ID reaches the original updater.
                    c.A0 = Gold; c.A1 = 0;
                    KingsField1_game.func_8001FA44(c, m);
                }
                finally { c.Restore(saved); }
                if (m.ReadU8(Queue + (uint)slot) != Gold) break;
                Owned[slot] = Pending[0];
                Pending.RemoveAt(0);
            }
        }
    }

    public static void AfterUpdate(CpuContext c, IMemory m)
    {
        lock (Gate)
        {
            foreach (int slot in Owned.Keys.ToArray())
                if (m.ReadU8(Queue + (uint)slot) != Gold) Owned.Remove(slot);
            int tail = m.ReadU8(Tail) & 7;
            if (!IsOwned(m, tail) || m.ReadU8(Phase) == 0) return;
            HideSprites(m);
            m.WriteU8(Sprites, 1);
        }
    }

    static void HideSprites(IMemory m)
    {
        for (uint n = 0; n < 6; n++) m.WriteU8(Sprites + n * 14, 0);
    }

    public static bool BeforeSprite(CpuContext c, IMemory m)
    {
        if (_drawing || c.RA != 0x80020238 || c.A0 != Sprites + 2) return true;
        lock (Gate)
        {
            int tail = m.ReadU8(Tail) & 7;
            if (!IsOwned(m, tail)) return true;
            var saved = c.Snapshot();
            ushort clut = m.ReadU16(Material), page = m.ReadU16(Material + 2);
            try
            {
                _drawing = true;
                c.SP -= 0x200;
                uint sprite = c.SP + 0x180;
                var glyphs = Owned[tail].Glyphs;
                int x = -glyphs.Length * 14 / 2;
                m.WriteU16(Material, m.ReadU16(Font + 2));
                m.WriteU16(Material + 2, m.ReadU16(Font));
                for (int i = 0; i < glyphs.Length; i++)
                {
                    ushort glyph = glyphs[i];
                    if (glyph == 0xFF) continue; // MENU_TEXT_BLANK advances without sampling an atlas cell.
                    DrawGlyph(c, m, sprite, glyph & 0xFFF, x + i * 14);
                    if ((glyph & 0x1000) != 0) DrawGlyph(c, m, sprite, -1, x + i * 14);
                    if ((glyph & 0x2000) != 0) DrawGlyph(c, m, sprite, -2, x + i * 14);
                }
            }
            finally
            {
                m.WriteU16(Material, clut); m.WriteU16(Material + 2, page);
                c.Restore(saved);
                _drawing = false;
            }
            return false;
        }
    }

    static void DrawGlyph(CpuContext c, IMemory m, uint sprite, int glyph, int x)
    {
        // Same cells/diacritic UVs as original menu_draw_string (80029DE0).
        int u = glyph >= 0 ? (glyph & 15) * 14 : glyph == -1 ? 196 : 210;
        int v = glyph >= 0 ? (glyph >> 4) * 12 : 24;
        m.WriteU8(sprite, (byte)u); m.WriteU8(sprite + 1, (byte)v);
        m.WriteU8(sprite + 2, (byte)m.ReadU16(Font + 8));
        m.WriteU8(sprite + 3, (byte)m.ReadU16(Font + 10));
        m.WriteU16(sprite + 4, unchecked((ushort)x));
        m.WriteU16(sprite + 6, unchecked((ushort)-96));
        m.WriteU16(sprite + 8, m.ReadU16(Font + 8));
        m.WriteU16(sprite + 10, m.ReadU16(Font + 10));
        c.A0 = sprite; c.A1 = 0; c.A2 = 0;
        // The caller already installed the game's notification rotation/translation.
        KingsField1_game.func_8001E230(c, m);
    }
}
