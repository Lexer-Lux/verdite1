using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

public sealed class SettingsPopup : Popup
{
    private const float SidebarWidth = 180f;

    private string _selectedId = "";

    protected override string TitleKey => "settings.title";
    protected override Vector2 Size => new(780f, 500f);

    /// <summary>Open the existing settings window at a registered section.</summary>
    public void OpenSection(string sectionId)
    {
        if (!SettingsRegistry.Sections.Any(section => section.Id == sectionId))
            throw new ArgumentException("Unknown settings section", nameof(sectionId));
        _selectedId = sectionId;
        Open();
    }

    protected override void DrawContent()
    {
        var sections = SettingsRegistry.Sections;
        var current = ResolveSelection(sections);

        DrawSidebar(sections, current);
        ImGui.SameLine();
        DrawContent(current);
    }

    private ISettingsSection? ResolveSelection(IReadOnlyList<ISettingsSection> sections)
    {
        if (sections.Count == 0) return null;
        foreach (var section in sections)
            if (section.Id == _selectedId)
                return section;

        _selectedId = sections[0].Id;
        return sections[0];
    }

    private void DrawSidebar(IReadOnlyList<ISettingsSection> sections, ISettingsSection? current)
    {
        ImGui.BeginChild("##settings-sidebar", new Vector2(SidebarWidth, 0f), ImGuiChildFlags.Border);

        foreach (var section in sections)
            if (ImGui.Selectable($"{Localization.T(section.TitleKey)}##sec-{section.Id}", current == section))
                _selectedId = section.Id;

        ImGui.EndChild();
    }

    private static void DrawContent(ISettingsSection? current)
    {
        ImGui.BeginChild("##settings-content", Vector2.Zero, ImGuiChildFlags.Border);

        if (current == null)
        {
            ImGui.TextDisabled(Localization.T("settings.empty"));
        }
        else
        {
            ImGui.SeparatorText(Localization.T(current.TitleKey));
            ImGui.Spacing();
            current.Draw();
            foreach (var extension in SettingsRegistry.GetExtensions(current.Id))
                extension();
        }

        ImGui.EndChild();
    }
}
