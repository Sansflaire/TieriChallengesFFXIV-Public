using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The searchable in-plugin manual, read from <c>HELP.md</c> at runtime.
///
/// <para><b>Two panes.</b> The left is the index — every section, or just the ones matching the
/// search. The right is the document itself. Clicking an index row scrolls the document to that
/// section, which is what makes a search result a way IN rather than a dead end.</para>
///
/// <para><b>Raw ImGui, themed.</b> This is a long scrolling document with a text field in it,
/// which PanacheUI has no primitives for. It pushes <see cref="DialogTheme"/> like every other
/// player-facing raw-ImGui surface. Using <c>ImGui.InputText</c> rather than
/// <c>PUI.TextInput</c> here is deliberate: an ImGui field claims the keyboard through ImGui's own
/// machinery, so it cannot repeat the fault in BROKEN.md 007.</para>
/// </summary>
internal sealed class HelpWindow
{
    public bool IsVisible;

    private string _search = string.Empty;

    /// <summary>Section the document pane should scroll to on the next frame, then forget.</summary>
    private string? _jumpTo;

    /// <summary>Which section is highlighted in the index.</summary>
    private string? _selected;

    private static Vector4 Accent => DialogTheme.Accent;
    private static Vector4 Muted  => DialogTheme.TextMuted;

    public void Open()
    {
        IsVisible = true;
        HelpLibrary.Reload();   // pick up an edited document without reloading the plugin
    }

    public void Draw()
    {
        if (!IsVisible) return;

        float scale = UiScale.Factor;
        ImGui.SetNextWindowSize(new Vector2(760 * scale, 560 * scale), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 360), new Vector2(1600, 1800));

        bool open = true;

        // Push/Pop must bracket Begin, and both must run even when Begin returns false, or the
        // style stack goes unbalanced for every window drawn afterwards.
        DialogTheme.Push();
        bool shown = ImGui.Begin("Challenges — Help###tc_help", ref open,
                                 ImGuiWindowFlags.NoSavedSettings);

        if (shown)
        {
            if (!string.IsNullOrEmpty(HelpLibrary.Error))
            {
                ImGui.TextColored(DialogTheme.Danger, "Help unavailable");
                ImGui.Spacing();
                ImGui.TextWrapped(HelpLibrary.Error);
            }
            else
            {
                DrawSearchRow();
                ImGui.Separator();
                DrawPanes();
            }
        }

        ImGui.End();
        DialogTheme.Pop();

        if (!open) IsVisible = false;
    }

    private void DrawSearchRow()
    {
        ImGui.SetNextItemWidth(-160 * UiScale.Factor);
        ImGui.InputTextWithHint("##help_search",
            "Search — describe it however you like…", ref _search, 128);

        ImGui.SameLine();
        if (ImGui.Button("Clear##help", new Vector2(70 * UiScale.Factor, 0))) _search = string.Empty;

        ImGui.SameLine();
        int count = HelpLibrary.Search(_search).Count;
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(_search)
            ? $"{HelpLibrary.Sections.Count} topics"
            : $"{count} match{(count == 1 ? "" : "es")}");
    }

    private void DrawPanes()
    {
        float scale     = UiScale.Factor;
        float indexW    = 240 * scale;
        var   results   = HelpLibrary.Search(_search);

        // ── Index ────────────────────────────────────────────────────────────
        if (ImGui.BeginChild("##help_index", new Vector2(indexW, 0), true))
        {
            if (results.Count == 0)
            {
                ImGui.TextDisabled("No topics match that.");
                ImGui.Spacing();
                ImGui.TextWrapped("Try a plainer word — the search also looks at extra terms "
                                + "attached to each topic that are not shown on screen.");
            }

            string lastCategory = string.Empty;

            foreach (var section in results)
            {
                // Category headers are suppressed while searching: results are ordered by
                // relevance rather than by document order, so grouping them would interleave
                // headers with rows that no longer sit under them.
                if (string.IsNullOrWhiteSpace(_search)
                    && !string.Equals(section.Category, lastCategory, StringComparison.Ordinal))
                {
                    lastCategory = section.Category;
                    ImGui.Spacing();
                    ImGui.TextColored(Accent, lastCategory.ToUpperInvariant());
                    ImGui.Separator();
                }

                bool isSelected = string.Equals(_selected, section.Title, StringComparison.Ordinal);

                if (ImGui.Selectable(section.Title + "##idx_" + section.Title, isSelected))
                {
                    _selected = section.Title;
                    _jumpTo   = section.Title;
                }

                // The preview earns its space while searching, where the title alone often is not
                // enough to tell which of four results is the one you meant.
                if (!string.IsNullOrWhiteSpace(_search) && !string.IsNullOrEmpty(section.Summary))
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Muted);
                    ImGui.TextWrapped("   " + section.Summary);
                    ImGui.PopStyleColor();
                    ImGui.Spacing();
                }
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // ── Document ─────────────────────────────────────────────────────────
        if (ImGui.BeginChild("##help_doc", new Vector2(0, 0), true))
        {
            string lastCategory = string.Empty;

            foreach (var section in results)
            {
                if (!string.Equals(section.Category, lastCategory, StringComparison.Ordinal))
                {
                    lastCategory = section.Category;
                    ImGui.Spacing();
                    ImGui.TextColored(Muted, lastCategory.ToUpperInvariant());
                    ImGui.Spacing();
                }

                // The jump happens as this section is drawn, which is the only moment ImGui knows
                // where it is. Consumed immediately so the pane is not re-pinned every frame,
                // which would fight the user for the scrollbar.
                bool isTarget = string.Equals(_jumpTo, section.Title, StringComparison.Ordinal);
                if (isTarget)
                {
                    ImGui.SetScrollHereY(0f);
                    _jumpTo = null;
                }

                ImGui.TextColored(Accent, section.Title);
                ImGui.Spacing();

                foreach (var block in section.Blocks)
                {
                    if (block.IsBullet)
                    {
                        ImGui.Indent(12f);
                        ImGui.TextWrapped("•  " + block.Text);
                        ImGui.Unindent(12f);
                    }
                    else
                    {
                        ImGui.TextWrapped(block.Text);
                    }

                    ImGui.Spacing();
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }

            if (results.Count == 0)
                ImGui.TextDisabled("Nothing to show.");
        }
        ImGui.EndChild();
    }
}
