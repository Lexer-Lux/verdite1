using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf1;

/// <summary>
/// Optional, depth-tested emissive material on an interaction's existing faces.
/// A source mod owns the outcome history and chooses the colour; this class
/// supplies stable identity and classification and changes no guest state.
/// Requires the existing Z-buffer and retained lighting paths. No overlay,
/// duplicate geometry, depth bypass or changes to the game's lights are used.
/// </summary>
public static class InteractionGlint
{
    public enum Glint { None, White, Red }
    public delegate Glint Query(InteractionHistory.ObjectId id, InteractionHistory.Kind kind, bool hasLoot, bool alreadyOpened);

    // Port-owned authored slots. SurfaceMaterial has no allocator; the audited
    // KF1 port owns no other authored material IDs. Keep this reservation here.
    const byte WhiteMaterial = SurfaceMaterial.FirstAuthored, RedMaterial = WhiteMaterial + 1;
    static Query? _query;
    static byte _material;
    static bool _materials;
    static (float R, float G, float B, bool Additive, bool Unfogged) _white, _red;

    public static void SetProvider(Query provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (ReferenceEquals(_query, provider)) return;
        if (_query != null) throw new InvalidOperationException("An interaction glint provider is already registered.");
        _query = provider;
    }

    /// <summary>Only the provider's owner can remove it; restores authored slots.</summary>
    public static bool RemoveProvider(Query provider)
    {
        if (!ReferenceEquals(_query, provider)) return false;
        _query = null;
        _material = 0;
        if (_materials)
        {
            Restore(WhiteMaterial, _white);
            Restore(RedMaterial, _red);
            SurfaceMaterial.Changed();
            _materials = false;
        }
        return true;
    }

    /// <summary>
    /// At SubmitInteraction entry, before c.A0 stops being the original record.
    /// Return value is restored by End in a finally block, including nested draws.
    /// </summary>
    public static byte Begin(IMemory memory, uint record)
    {
        byte previous = _material;
        _material = 0;
        var query = _query;
        if (query == null || !GteDepth.ZBuffer || !RetainedScene.Lit
            || !InteractionHistory.TryDescribe(memory, record, out var id, out var kind))
            return previous;
        Glint glint;
        try { glint = query(id, kind, InteractionHistory.HasLoot(memory, record),
            InteractionHistory.HasOpenedInCurrentAction(memory, record)); }
        catch (Exception error)
        {
            RemoveProvider(query);
            Console.Error.WriteLine($"[KF1] interaction glint provider disabled: {error.Message}");
            return previous;
        }
        if (glint is not (Glint.White or Glint.Red)) return previous;
        EnsureMaterials();
        _material = glint == Glint.White ? WhiteMaterial : RedMaterial;
        return previous;
    }

    public static void End(byte previous) => _material = previous;

    /// <summary>
    /// Call for retained instances and freshly initialized packet depth records.
    /// An explicit material already owned by another feature is preserved.
    /// Packet callers only apply this to faces with a valid depth record.
    /// </summary>
    public static byte Apply(byte existingMaterial) => existingMaterial != 0 || _query == null
        || !GteDepth.ZBuffer || !RetainedScene.Lit ? existingMaterial : _material;

    static (float, float, float, bool, bool) Snapshot(byte id) =>
        (SurfaceMaterial.Emissive[id * 3], SurfaceMaterial.Emissive[id * 3 + 1], SurfaceMaterial.Emissive[id * 3 + 2],
         SurfaceMaterial.EmissiveAdditive[id], SurfaceMaterial.EmissiveUnfogged[id]);

    static void Restore(byte id, (float R, float G, float B, bool Additive, bool Unfogged) value)
    {
        SurfaceMaterial.Emissive[id * 3] = value.R;
        SurfaceMaterial.Emissive[id * 3 + 1] = value.G;
        SurfaceMaterial.Emissive[id * 3 + 2] = value.B;
        SurfaceMaterial.EmissiveAdditive[id] = value.Additive;
        SurfaceMaterial.EmissiveUnfogged[id] = value.Unfogged;
    }

    static void EnsureMaterials()
    {
        if (_materials) return;
        _white = Snapshot(WhiteMaterial);
        _red = Snapshot(RedMaterial);
        // Add after texture modulation while retaining the original fog. A dark
        // texel gets a small glint; all original light and depth inputs survive.
        Restore(WhiteMaterial, (0.08f, 0.08f, 0.08f, true, false));
        Restore(RedMaterial, (0.12f, 0.01f, 0.01f, true, false));
        SurfaceMaterial.Changed();
        _materials = true;
    }
}
