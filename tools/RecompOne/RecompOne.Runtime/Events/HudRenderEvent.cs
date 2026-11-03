namespace RecompOne.Runtime.Events;

/// <summary>
/// Fires on the host render thread after panels and before ImGui renders.
/// Listeners may append HUD primitives to ImGui's foreground draw list.
/// Dimensions cover the entire display in logical ImGui coordinates.
/// </summary>
public sealed class HudRenderEvent : IEvent
{
    public readonly float Width;
    public readonly float Height;

    public HudRenderEvent(float width, float height)
    {
        Width = width;
        Height = height;
    }
}
