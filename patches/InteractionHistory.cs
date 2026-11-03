using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf1;

/// <summary>
/// SLPS-00017 interaction and save-extension bridge for source mods. Observes retail outcomes;
/// never changes guest RAM, inventory, door state, map cells or card bytes.
/// Install once from Program; each mod owns its policy and serialized state.
/// </summary>
public static class InteractionHistory
{
    public enum Kind : byte { HingedDoor, HingedDoorPartner, LiftDoor, HingedContainer = 8, ItemContainer = 9 }
    public enum Outcome : byte { Selected, Locked, Opened }
    public readonly record struct ObjectId(byte Area, byte Index);
    public static event Action<ObjectId, Outcome>? Changed;
    public static event Action? GameRestored;
    const uint Objects = 0x8006EDE0, Definitions = 0x8006E8E0, Grid = 0x8009A748;
    public const int PersistentObjectsPerArea = 160;
    const int Count = PersistentObjectsPerArea, Areas = 5, PayloadSize = 0x2580;
    static uint _selected, _neighbor, _actionRecord;
    static byte _oldAction;
    static bool _dispatch;
    static int _bank = -1;
    static uint _payload;
    static string? _payloadDigest;
    static string? _cardPath;
    static Dictionary<string, byte[]> _loaded = new();
    sealed record Extension(Func<byte[]> Capture, Action<byte[]> Restore, Action Reset);
    static readonly Dictionary<string, Extension> Extensions = new();
    static readonly ModInfo Self = new() { Id = "kf1.interaction-history", Name = "Interaction outcome API", Version = "1.0" };

    public static void Install() => HookAttach.OnOverlayLoad("interaction history", () =>
    {
        SymbolRegistry.Build();
        var hooks = new (uint Address, string? Pre, string? Post)[] {
            (0x80034DE4, nameof(BeforeDispatch), nameof(AfterDispatch)),
            (0x800315C4, null, nameof(AfterSelection)),
            (0x8001FA44, nameof(BeforeNotification), null),
            (0x80031784, nameof(BeforeAction), nameof(AfterAction)),
            (0x8002B73C, nameof(BeforeSave), nameof(AfterSave)),
            (0x8002BEB0, nameof(BeforeLoad), nameof(AfterLoad)),
            (0x800502AC, nameof(BeforeWrite), null),
            (0x8005047C, nameof(BeforeRead), nameof(AfterRead)),
            // 0x80015280 is an instruction inside this initializer, not a
            // Dispatcher function boundary. Hook the actual mapped entry.
            (0x800151CC, null, nameof(AfterInitialize))
        };
        // Resolve everything before registration, so an unavailable symbol cannot
        // leave half an observer installed and then duplicate it on the retry.
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

    static MethodInfo Method(string name) => typeof(InteractionHistory).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    static bool Describe(IMemory m, uint record, out ObjectId id, out byte kind)
    {
        id = default; kind = 255;
        if (AgentBeacon.Overlay != "game" || m.ReadU16(AgentBeacon.Hp) == 0 || record < Objects
            // Retail has 190 runtime slots; 160..189 are transient drop/effect
            // pools, not stable placed-object identities serialized with a floor.
            || (record - Objects) % 44 != 0 || (record - Objects) / 44 >= PersistentObjectsPerArea) return false;
        byte area = m.ReadU8(0x800A078A), type = m.ReadU8(record);
        if (area is < 1 or > Areas || type >= 160) return false;
        kind = m.ReadU8(Definitions + 8u * type);
        if (kind is not (0 or 1 or 2 or 8 or 9)) return false;
        uint x = m.ReadU16(record + 2), z = m.ReadU16(record + 4);
        // Hidden doors are grid attributes, not the real door operations. Deny
        // their cells too; no API client gets a highlight there by coincidence.
        if (x >= 100 || z >= 100 || m.ReadU8(Grid + 100 * z + x) == 0x45) return false;
        id = new(area, (byte)((record - Objects) / 44));
        return true;
    }

    public static bool TryDescribe(IMemory m, uint record, out ObjectId id, out Kind kind)
    {
        bool valid = Describe(m, record, out id, out byte operation);
        kind = (Kind)operation;
        return valid;
    }
    public static bool HasLoot(IMemory m, uint record)
    {
        if (!Describe(m, record, out _, out byte kind) || kind is not (8 or 9)) return false;
        uint first = record + (kind == 8 ? 0x21u : 0x20u);
        for (uint i = 0; i < 4; i++) if (m.ReadU8(first + i) != 255) return true;
        return false;
    }

    /// <summary>
    /// True once a real door's current native opening/holding/closing action has
    /// advanced. This is a live fact, not lifetime history: retail saves do not
    /// preserve the door action/timer, and an idle closed door cannot reveal
    /// whether it was opened before this mod existed.
    /// </summary>
    public static bool HasOpenedInCurrentAction(IMemory m, uint record)
    {
        if (!Describe(m, record, out var id, out byte kind) || kind > 2) return false;
        if (kind == 1)
        {
            // Partner operation 1 does not tick. Retail's operation 0 advances
            // both leaves using the reciprocal action_parameter indexes.
            byte primaryIndex = m.ReadU8(record + 0x21);
            if (primaryIndex >= Count) return false;
            uint primary = Objects + 44u * primaryIndex;
            if (!Describe(m, primary, out _, out byte primaryKind) || primaryKind != 0
                || m.ReadU8(primary + 0x21) != id.Index) return false;
            record = primary;
            kind = 0;
        }
        byte action = m.ReadU8(record + 0x28);
        ushort timer = m.ReadU16(record + 0x2A);
        return action == kind && timer > 0 && timer <= (kind == 2 ? 341 : 332);
    }

    public static void Register(string owner, Func<byte[]> capture, Action<byte[]> restore, Action reset)
    {
        if (!ValidOwner(owner))
            throw new ArgumentException("Use a native mod id as the save-extension owner", nameof(owner));
        if (Extensions.ContainsKey(owner) || _loaded.Keys.Union(Extensions.Keys).Append(owner).Distinct().Count() > 32)
            throw new InvalidOperationException("Save-extension owner already registered or capacity reached");
        var extension = new Extension(capture, restore, reset);
        Extensions.Add(owner, extension);
        Invoke(owner, reset);
        if (_loaded.TryGetValue(owner, out var bytes)) Invoke(owner, () => restore((byte[])bytes.Clone()));
    }
    static bool ValidOwner(string owner) => owner.Length is >= 1 and <= 100
        && owner.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    public static void Unregister(string owner)
    {
        if (!Extensions.TryGetValue(owner, out var extension)) return;
        _loaded.Remove(owner);
        Invoke(owner, () => _loaded[owner] = CheckedCapture(extension));
        Extensions.Remove(owner);
    }
    static byte[] CheckedCapture(Extension extension)
    {
        byte[] bytes = extension.Capture();
        if (bytes == null || bytes.Length > 4096) throw new InvalidOperationException("Save extension exceeds 4096 bytes or returned no blob");
        return (byte[])bytes.Clone();
    }
    static void Invoke(string owner, Action action)
    {
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[KF1] save-extension {owner}: {e.Message}"); }
    }

    static void Record(IMemory m, uint record, Outcome state)
    {
        if (!Describe(m, record, out var id, out _)) return;
        if (Changed is { } changed)
            foreach (Action<ObjectId, Outcome> handler in changed.GetInvocationList())
                Invoke("interaction observer", () => handler(id, state));
    }

    public static void BeforeDispatch(CpuContext c, IMemory m) { _dispatch = true; _selected = _neighbor = 0; }
    public static void AfterDispatch(CpuContext c, IMemory m) { _dispatch = false; _selected = _neighbor = 0; }
    public static void AfterSelection(CpuContext c, IMemory m)
    {
        if (_dispatch && c.RA == 0x80035164)
        {
            _selected = c.V0 < Count ? Objects + 44 * c.V0 : 0;
            _neighbor = 0;
            Record(m, _selected, Outcome.Selected);
        }
        else if (_dispatch && c.RA == 0x8003546C)
            _neighbor = c.V0 < Count ? Objects + 44 * c.V0 : 0;
    }
    public static void BeforeNotification(CpuContext c, IMemory m)
    {
        if (!_dispatch || !Describe(m, _selected, out _, out byte kind)) return;
        if ((c.RA == 0x80035320 && kind == 8) || (c.RA == 0x80035628 && kind is 0 or 1 or 2))
        {
            Record(m, _selected, Outcome.Locked);
            // 0x8003546C is retail's actual partner search. At the blocked-
            // partner branch S1 is that returned record, whose operation must
            // be the primary hinged leaf and whose link must still be locked.
            // Never infer a pair merely from spatial proximity or shared keys.
            if (c.RA == 0x80035628 && (kind is 0 or 1) && _neighbor != _selected && c.S1 == _neighbor
                && Describe(m, _neighbor, out _, out byte neighborKind) && neighborKind == 0 && m.ReadU8(_neighbor + 0x20) != 255)
                Record(m, _neighbor, Outcome.Locked);
        }
    }
    public static void BeforeAction(CpuContext c, IMemory m)
    {
        _actionRecord = 0;
        if (!_dispatch || c.RA is not (0x80035400 or 0x800354F0 or 0x80035514)) return;
        if (!Describe(m, c.A0, out _, out byte kind) || kind > 2) return;
        _actionRecord = c.A0; _oldAction = m.ReadU8(c.A0 + 0x28);
    }
    public static void AfterAction(CpuContext c, IMemory m)
    {
        if (_actionRecord != 0 && _oldAction == 255 && m.ReadU8(_actionRecord + 0x28) <= 2)
            Record(m, _actionRecord, Outcome.Opened);
        _actionRecord = 0;
    }
    static void ResetExtensions()
    {
        _loaded.Clear();
        foreach (var (owner, extension) in Extensions.ToArray()) Invoke(owner, extension.Reset);
        if (GameRestored is { } restored)
            foreach (Action handler in restored.GetInvocationList()) Invoke("restore observer", handler);
    }
    public static void NewGame(CpuContext c, IMemory m) => ResetExtensions();
    public static void AfterInitialize(CpuContext c, IMemory m)
    {
        // This stat initializer is also used by the special respawn path.
        // Only the actual new-player constructor's call starts a new lineage.
        if (c.RA == 0x80016E7C) NewGame(c, m);
    }
    static void BeginIo() { _bank = -1; _payload = 0; _payloadDigest = null; _cardPath = null; }
    public static void BeforeSave(CpuContext c, IMemory m) => BeginIo();
    public static void BeforeLoad(CpuContext c, IMemory m) => BeginIo();
    static void Capture(CpuContext c, IMemory m, uint bank)
    {
        _bank = -1; _payload = 0; _payloadDigest = null; _cardPath = null;
        uint ramSize = RecompOne.Runtime.Runtime.RamSize;
        if (bank >= 4 || c.A2 != PayloadSize || ramSize < MemoryMap.RetailRamSize
            || ramSize > MemoryMap.RamWindow || (ramSize & (ramSize - 1u)) != 0) return;
        // PSMemory maps the 8 MiB physical RAM window onto the configured backing
        // size. A high mirrored heap pointer is valid even with 2 MiB backing.
        uint physical = MemoryMap.ToPhysical(c.A1);
        if (physical >= MemoryMap.RamWindow) return;
        uint offset = physical & (ramSize - 1u);
        if (offset > ramSize - PayloadSize) return;
        _bank = (int)bank; _payload = c.A1;
        bool second = m.ReadU8(0x80056036) == (byte)'1'; // bu00:/bu10: retail filename
        string configured = second ? ConfigManager.Game.CardBPath : ConfigManager.Game.CardAPath;
        _cardPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? (second ? "cardb.sav" : "carda.sav") : configured);
    }
    public static void BeforeWrite(CpuContext c, IMemory m)
    {
        if (c.RA != 0x8002BAB0) return;
        Capture(c, m, c.S3);
        if (_bank >= 0) _payloadDigest = Digest(m);
    }
    public static void BeforeRead(CpuContext c, IMemory m)
    {
        // The reader has already multiplied its bank by 0x2580 at 0x8002C050.
        if (c.RA == 0x8002C098 && c.S3 % PayloadSize == 0)
        {
            _payloadDigest = null;
            Capture(c, m, c.S3 / PayloadSize);
        }
    }
    public static void AfterRead(CpuContext c, IMemory m)
    {
        // Capture while BIOS's successful read has just populated the buffer;
        // subsequent native restore work and allocator lifetime cannot affect it.
        if (c.RA == 0x8002C098 && c.V0 == PayloadSize && _bank >= 0) _payloadDigest = Digest(m);
    }
    static string Digest(IMemory m)
    {
        byte[] bytes = new byte[PayloadSize];
        for (uint i = 0; i < PayloadSize; i++) bytes[i] = m.ReadU8(_payload + i);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    public sealed class BankMetadata { public string Digest { get; set; } = ""; public Dictionary<string, byte[]> Blobs { get; set; } = new(); }
    public sealed class Metadata { public int Version { get; set; } = 1; public BankMetadata?[] Banks { get; set; } = new BankMetadata?[4]; }
    static Metadata ReadMetadata(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1048576) return new();
        var data = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(path));
        if (data is not { Version: 1, Banks.Length: 4 }) return new();
        foreach (var bank in data.Banks)
            if (bank != null && (bank.Digest == null || bank.Digest.Length != 64 || !bank.Digest.All(Uri.IsHexDigit)
                || bank.Blobs == null || bank.Blobs.Count > 32 || bank.Blobs.Any(b => !ValidOwner(b.Key) || b.Value == null || b.Value.Length > 4096))) return new();
        return data;
    }
    public static void AfterLoad(CpuContext c, IMemory m)
    {
        if (c.V0 != 1) return;
        ResetExtensions();
        if (_bank < 0 || _cardPath == null || _payloadDigest == null) return;
        try
        {
            var bank = ReadMetadata(_cardPath + ".kf1-extensions.json").Banks[_bank];
            if (bank != null && bank.Digest == _payloadDigest && bank.Blobs.Keys.Union(Extensions.Keys).Count() <= 32)
            {
                _loaded = bank.Blobs;
                foreach (var (owner, extension) in Extensions.ToArray())
                    if (_loaded.TryGetValue(owner, out var bytes)) Invoke(owner, () => extension.Restore((byte[])bytes.Clone()));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine($"[KF1] interaction history could not load: {e.Message}"); }
    }
    public static void AfterSave(CpuContext c, IMemory m)
    {
        if (c.V0 != 1 || _bank < 0 || _cardPath == null || _payloadDigest == null || (Extensions.Count == 0 && _loaded.Count == 0)) return;
        try
        {
            string digest = _payloadDigest;
            // BIOS close reports success even when the host flush failed. Compare
            // the actual disk payload before committing matching metadata.
            if (DiskDigest(_cardPath, _bank) != digest) return;
            string path = _cardPath + ".kf1-extensions.json";
            var data = ReadMetadata(path);
            // The current loaded/new-game lineage owns these blobs. Never merge
            // the overwritten physical bank, which may hold an unrelated save.
            var blobs = _loaded.ToDictionary(b => b.Key, b => (byte[])b.Value.Clone());
            foreach (var (owner, extension) in Extensions.ToArray())
            {
                // A failed active capture must not retain stale state for that
                // owner, while unrelated disabled owners keep their knowledge.
                blobs.Remove(owner);
                Invoke(owner, () =>
                {
                    blobs.Add(owner, CheckedCapture(extension));
                });
            }
            data.Banks[_bank] = new() { Digest = digest, Blobs = blobs };
            RecompOne.Runtime.IO.DurableFile.WriteText(path, JsonSerializer.Serialize(data), sync: true);
            _loaded = blobs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Console.Error.WriteLine($"[KF1] interaction history could not save: {e.Message}"); }
    }
    static string? DiskDigest(string path, int bank)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != 0x20000) return null;
        byte[] card = File.ReadAllBytes(path);
        if (card[0] != 'M' || card[1] != 'C') return null;
        int block = -1;
        // Retail's filename is all 20 bytes: the 14-character name followed
        // by six spaces, not a NUL-terminated 14-character directory entry.
        const string nativeSaveName = "BISLPS-00017KF      ";
        for (int i = 1; i <= 15; i++)
            if (card[i * 128] == 0x51 && Encoding.ASCII.GetString(card, i * 128 + 10, 20) == nativeSaveName) { block = i; break; }
        if (block < 0) return null;
        byte[] payload = new byte[PayloadSize];
        int start = 0x280 + bank * PayloadSize;
        var seen = new HashSet<int>();
        for (int pos = 0; pos < start + PayloadSize;)
        {
            if (block is < 1 or > 15 || !seen.Add(block)) return null;
            int end = Math.Min(pos + 8192, start + PayloadSize);
            for (int i = Math.Max(pos, start); i < end; i++) payload[i - start] = card[block * 8192 + i - pos];
            pos += 8192;
            block = (card[block * 128 + 8] | card[block * 128 + 9] << 8) + 1;
        }
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
