using System.Numerics;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf1;

/// <summary>Optional world sprites. Mods own eligibility/history; this bridge only
/// anchors a small billboard to submitted geometry and uses normal world depth.</summary>
public static class InteractionGlint
{
    public enum Glint { None, White, Red }
    public delegate Glint Query(InteractionHistory.ObjectId id, InteractionHistory.Kind kind, bool hasLoot, bool alreadyOpened);
    sealed class Scope
    {
        public Glint Colour;
        public int Phase;
        public bool Placed;
        public IMemory Memory = null!;
        public uint OrderSlot = uint.MaxValue;
        public long Frame;
        public byte Area;
        public uint ArenaRecord, ArenaStart, Table;
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public RetainedScene.View View;
    }
    static readonly Stack<Scope?> Scopes = new();
    readonly record struct NativeSprite(Scope Scope,float X,float Y,float Z,float Size,float Pulse);
    static readonly List<NativeSprite> Pending = new(160);
    static readonly float[] Unlit = new float[12];
    static Query? _query;
    public static bool Observing => Scopes.Count != 0 && Scopes.Peek() != null;
    public static long RetainedSprites { get; private set; }
    public static long NativeSprites { get; private set; }
    public static long FullArenaSkips { get; private set; }
    static bool _installed;
    static bool _lifecycle;
    static long _frame;
    static readonly ModInfo Self = new() {Id="kf1.interaction-sprites",Name="Interaction sprite renderer",Version="1.0"};

    public static void Install()
    {
        if(!_lifecycle)
        {
            InteractionHistory.GameRestored+=ResetFrame;
            Event.AddListener<OverlayLoadedEvent>(_=>ResetFrame());
            _lifecycle=true;
        }
        HookAttach.OnOverlayLoad("interaction sprites", () =>
        {
            SymbolRegistry.Build();
            var head=SymbolRegistry.Resolve("game",null,0x8001BFB8);
            var present=SymbolRegistry.Resolve("game",null,0x8001C050);
            var addPrim=SymbolRegistry.Resolve("game",null,0x80054290);
            if(head==null||present==null||addPrim==null)return false;
            if(!_installed)
            {
                HookManager.AddPre(Self,head,typeof(InteractionGlint).GetMethod(nameof(FrameHead))!);
                HookManager.AddPre(Self,present,typeof(InteractionGlint).GetMethod(nameof(BeforePresent))!);
                HookManager.AddPre(Self,addPrim,typeof(InteractionGlint).GetMethod(nameof(BeforeAddPrim))!);
                HookManager.Commit();_installed=true;
            }
            return HookAttach.Installed(head)&&HookAttach.Installed(present)&&HookAttach.Installed(addPrim);
        });
    }

    static void ResetFrame() { _frame++;Pending.Clear();Scopes.Clear(); }
    public static void FrameHead(CpuContext c,IMemory m) => ResetFrame();
    public static void BeforePresent(CpuContext c,IMemory m)
    {
        try
        {
            if(_query==null||Literal.Verifying!=0||AgentBeacon.Overlay!="game")return;
            foreach(var sprite in Pending)
                if(ReferenceEquals(sprite.Scope.Memory,m))
                    DrawNative(sprite.Scope,sprite.X,sprite.Y,sprite.Z,sprite.Size,sprite.Pulse);
        }
        finally {Pending.Clear();}
    }

    /// <summary>Both original writers have finished their final packet XY,
    /// colour and OT calculation at these AddPrim calls. The same S2..S5 cache
    /// pointers are used immediately before the calls in original game code.
    /// Observe here once, including when literal writers/depth recording are off.</summary>
    public static void BeforeAddPrim(CpuContext c,IMemory m)
    {
        if(!Observing || Literal.Verifying!=0 || c.RA is not (0x8001D6D4 or 0x8001DDBC)||!InRam(c.A1,8))return;
        uint code=m.ReadU8(c.A1+7);
        bool quad=(code&8)!=0;
        if(!InRam(c.A1,quad?24u:20u)||!InRam(c.S2,8)||!InRam(c.S3,8)||!InRam(c.S4,8)||(quad&&!InRam(c.S5,8)))return;
        Packet(m,c.A1,m.ReadU16(c.S2+4),m.ReadU16(c.S3+4),m.ReadU16(c.S4+4),quad?m.ReadU16(c.S5+4):0,c.A0);
    }
    // A pixel sprite, not an emissive treatment of the object's faces. Empty
    // cells remain transparent; brightness varies from the tips to the core.
    static readonly string[] Star = [
        "....1....", "....2....", "...121...", "..12321..", "123343321",
        "..12321..", "...121...", "....2....", "....1...."
    ];

    public static void SetProvider(Query provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (ReferenceEquals(_query, provider)) return;
        if (_query != null) throw new InvalidOperationException("An interaction glint provider is already registered.");
        _query = provider;
    }
    public static bool RemoveProvider(Query provider)
    {
        if (!ReferenceEquals(_query, provider)) return false;
        _query = null;
        Scopes.Clear();
        Pending.Clear();
        return true;
    }

    /// <summary>Scoped by SubmitInteraction's existing try/finally.</summary>
    public static byte Begin(IMemory memory, uint record)
    {
        byte previous = (byte)Scopes.Count;
        if (Scopes.Count >= 8) return previous;
        Scope? scope = null;
        var query = _query;
        if (query != null && GpuWorld.Domain == "models" && Literal.Verifying == 0
            && InteractionHistory.TryDescribe(memory, record, out var id, out var kind))
        {
            try
            {
                Glint colour = query(id, kind, InteractionHistory.HasLoot(memory, record),
                    InteractionHistory.HasOpenedInCurrentAction(memory, record));
                if (colour is Glint.White or Glint.Red)
                {
                    uint recordPointer=memory.ReadU32(0x80070EB8);
                    if(InRam(recordPointer,12)) scope = new() {
                        Colour=colour, Phase=id.Index*37, Memory=memory, Frame=_frame, Area=id.Area,
                        ArenaRecord=recordPointer, ArenaStart=memory.ReadU32(recordPointer), Table=memory.ReadU32(0x80090EBC)
                    };
                }
            }
            catch (Exception error)
            {
                RemoveProvider(query);
                Console.Error.WriteLine($"[KF1] interaction sprite provider disabled: {error.Message}");
                return previous;
            }
        }
        Scopes.Push(scope);
        return previous;
    }

    /// <summary>Only the existing retained mesh supplies bounds: no guessed door
    /// pivots, duplicated record layouts, or marker for an undrawn fallback mesh.</summary>
    public static void Submitted(IMemory memory, in RetainedScene.ModelInstance instance, uint vertices, int count)
    {
        if (Scopes.Count == 0 || Scopes.Peek() is not { } scope || !GpuWorld.Drawing
            || GpuWorld.Domain != "models" || count is <= 0 or > 4096) return;
        uint ram = RecompOne.Runtime.Runtime.RamSize;
        uint physical = MemoryMap.ToPhysical(vertices);
        if (physical >= MemoryMap.RamWindow || ram == 0 || (physical & (ram - 1)) + count * 8L > ram) return;
        for (uint i = 0; i < count; i++)
        {
            uint p = vertices + i * 8;
            int x = (short)memory.ReadU16(p), y = (short)memory.ReadU16(p + 2), z = (short)memory.ReadU16(p + 4);
            var point = new Vector3(
                ((long)instance.V00*x + (long)instance.V01*y + (long)instance.V02*z)/4096f + instance.Vtx,
                ((long)instance.V10*x + (long)instance.V11*y + (long)instance.V12*z)/4096f + instance.Vty,
                ((long)instance.V20*x + (long)instance.V21*y + (long)instance.V22*z)/4096f + instance.Vtz);
            scope.Min = Vector3.Min(scope.Min, point);
            scope.Max = Vector3.Max(scope.Max, point);
        }
        scope.View = GpuWorld.ReadView(memory);
        scope.Placed = true;
    }

    public static void End(byte previous)
    {
        while (Scopes.Count > previous)
        {
            var scope = Scopes.Pop();
            if (scope is { Placed: true } && _query != null && Literal.Verifying == 0) Draw(scope);
        }
    }

    /// <summary>The packet model path supplies its actual projected corners and
    /// unsigned cache depths when a model is not retained. Invert the native
    /// projection quotient read-only; clipped/saturated corners cannot anchor.</summary>
    public static void Packet(IMemory memory, uint packet, float z0, float z1, float z2, float z3, uint orderSlot = 0)
    {
        if (Scopes.Count == 0 || Scopes.Peek() is not { } scope || GpuWorld.Domain != "models") return;
        uint code = memory.ReadU8(packet + 7);
        int corners = (code & 8) != 0 ? 4 : 3;
        uint stride = 4u + ((code & 4) != 0 ? 4u : 0u) + ((code & 16) != 0 ? 4u : 0u);
        float cx = (int)Gte.ReadControl(24) / 65536f, cy = (int)Gte.ReadControl(25) / 65536f;
        Span<float> depths = stackalloc float[] {z0,z1,z2,z3};
        for (int i=0;i<corners;i++)
        {
            uint xy = memory.ReadU32(packet + 8 + (uint)i * stride);
            int x=(short)xy, y=(short)(xy>>16);
            if(x is <= -1024 or >= 1023 || y is <= -1024 or >= 1023) continue;
            uint quotient=Gte.DepthQuotient((uint)depths[i]);
            if(quotient==0 || quotient==0x1FFFF) continue;
            var point=new Vector3((x-cx)*65536f/quotient,(y-cy)*65536f/quotient,depths[i]);
            scope.Min=Vector3.Min(scope.Min,point);scope.Max=Vector3.Max(scope.Max,point);
            scope.Placed=true;
        }
        scope.View=GpuWorld.ReadView(memory);
        scope.View.H=(ushort)Gte.ReadControl(26);scope.View.Cx=cx;scope.View.Cy=cy;
        uint table=memory.ReadU32(0x80090EBC);
        if(orderSlot>=table && orderSlot-table<0x10000 && (orderSlot-table)%4==0)
            scope.OrderSlot=Math.Min(scope.OrderSlot,(orderSlot-table)/4);
    }

    static void Draw(Scope scope)
    {
        // Place on the camera-facing surface, rather than inside a closed chest
        // or behind a door. Intervening world geometry retains normal occlusion.
        float z = scope.Min.Z - 12f;
        if (z < 200 || z > 10000) return;
        float x = (scope.Min.X + scope.Max.X) * .5f, y = (scope.Min.Y + scope.Max.Y) * .5f;
        float size = Math.Clamp(z * 8f / Math.Max(1f, scope.View.H), 12f, 240f);
        float pulse = .8f + .2f * MathF.Cos((Environment.TickCount64 + scope.Phase) * (MathF.PI * 2f / 1600f));
        var view = scope.View;
        if (!GpuWorld.Drawing)
        {
            // Present follows the arm/HUD too. Allocate only after their real
            // native packets, so optional sprites cannot displace game geometry.
            if(Pending.Count<160)Pending.Add(new(scope,x,y,z,size,pulse));
            return;
        }
        RetainedSprites++;
        Vector3 World(float sx, float sy)
        {
            float dx = sx - view.Tx, dy = sy - view.Ty, dz = z - view.Tz;
            return new(view.R00*dx + view.R10*dy + view.R20*dz + (float)view.CamX,
                view.R01*dx + view.R11*dy + view.R21*dz + (float)view.CamY,
                view.R02*dx + view.R12*dy + view.R22*dz + (float)view.CamZ);
        }
        Span<RetainedScene.Vertex> quad = stackalloc RetainedScene.Vertex[6];
        for (int row = 0; row < Star.Length; row++)
            for (int col = 0; col < Star[row].Length; col++)
            {
                char pixel = Star[row][col];
                if (pixel == '.') continue;
                float brightness = (pixel - '0') / 4f * pulse;
                float r = 255f * brightness;
                float g = (scope.Colour == Glint.Red ? 38f : 244f) * brightness;
                float b = (scope.Colour == Glint.Red ? 24f : 214f) * brightness;
                float x0 = x + (col - 4.5f) * size / 9f, y0 = y + (row - 4.5f) * size / 9f;
                float x1 = x0 + size / 9f, y1 = y0 + size / 9f;
                Vector3 a = World(x0,y0), c = World(x1,y1), bb = World(x1,y0), d = World(x0,y1);
                RetainedScene.Vertex Vertex(Vector3 p) => new() {
                    X=p.X, Y=p.Y, Z=p.Z, R=r, G=g, B=b, Texpage=0x8000,
                    Flags=RetainedScene.FlagNoShadow, Curve=0, Rgbc=0
                };
                quad[0]=Vertex(a);quad[1]=Vertex(bb);quad[2]=Vertex(c);
                quad[3]=Vertex(a);quad[4]=Vertex(c);quad[5]=Vertex(d);
                RetainedScene.AddMainModel(quad,Unlit);
            }
    }

    // Native SetPolyF4/AddPrim packet shape, from 8001CA44/8001D6B0 and
    // AddPrim_game. Allocate the complete sprite before writing any packet.
    // A slot just in front of the closest actual face preserves the submitter's
    // native class bias; no guessed object depth or global HUD layer is used.
    static void DrawNative(Scope scope,float x,float y,float z,float size,float pulse)
    {
        if(scope.OrderSlot is < 6 or > 0x3FFF || scope.View.H<=0) return;
        var memory=scope.Memory;
        uint record=memory.ReadU32(0x80070EB8),table=memory.ReadU32(0x80090EBC);
        if(!InRam(record,12)||!InRam(table,0x10000)) return;
        uint start=memory.ReadU32(record),end=memory.ReadU32(record+4),cursor=memory.ReadU32(record+8);
        if(((record|table|start|end|cursor)&3)!=0)return;
        if(scope.Frame!=_frame || scope.Area!=memory.ReadU8(0x800A078A)
            || record!=scope.ArenaRecord || table!=scope.Table || start!=scope.ArenaStart) return;
        int pixels=0;foreach(string row in Star)foreach(char pixel in row)if(pixel!='.')pixels++;
        uint bytes=(uint)pixels*24;
        if(cursor<start || end<cursor || !InRam(start,end-start) || bytes>end-cursor || !InRam(cursor,bytes))
        { FullArenaSkips++;return; }
        uint slot=table+(scope.OrderSlot-1)*4;
        float scale=scope.View.H/z;
        uint Xy(float vx,float vy)
        {
            int sx=(int)MathF.Round(scope.View.Cx+vx*scale),sy=(int)MathF.Round(scope.View.Cy+vy*scale);
            return (ushort)Math.Clamp(sx,-1024,1023) | (uint)(ushort)Math.Clamp(sy,-1024,1023)<<16;
        }
        for(int row=0;row<Star.Length;row++)
            for(int col=0;col<Star[row].Length;col++)
            {
                char pixel=Star[row][col];if(pixel=='.')continue;
                float brightness=(pixel-'0')/4f*pulse;
                uint r=(uint)(255*brightness),g=(uint)((scope.Colour==Glint.Red?38:244)*brightness),b=(uint)((scope.Colour==Glint.Red?24:214)*brightness);
                float x0=x+(col-4.5f)*size/9f,y0=y+(row-4.5f)*size/9f;
                float x1=x0+size/9f,y1=y0+size/9f;
                memory.WriteU32(cursor,0x05000000u|(memory.ReadU32(slot)&0xFFFFFF));
                memory.WriteU32(cursor+4,0x28000000u|r|g<<8|b<<16);
                memory.WriteU32(cursor+8,Xy(x0,y0));memory.WriteU32(cursor+12,Xy(x1,y0));
                memory.WriteU32(cursor+16,Xy(x0,y1));memory.WriteU32(cursor+20,Xy(x1,y1));
                PacketDepth.Sprite(memory,cursor,z);
                memory.WriteU32(slot,(memory.ReadU32(slot)&0xFF000000u)|(cursor&0xFFFFFF));
                cursor+=24;
            }
        memory.WriteU32(record+8,cursor);
        NativeSprites++;
    }

    static bool InRam(uint address,uint bytes)
    {
        uint ram=RecompOne.Runtime.Runtime.RamSize,physical=MemoryMap.ToPhysical(address);
        return ram!=0 && (ram&(ram-1))==0 && physical<MemoryMap.RamWindow
            && bytes<=ram && (physical&(ram-1))<=ram-bytes;
    }
}
