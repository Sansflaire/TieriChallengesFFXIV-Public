#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Developer-only.</b> Drives <see cref="LiveProbe"/> from a checklist so a live-game question
/// can be answered without a back-and-forth conversation: Trist records one labelled action per
/// row, hits Write Report, and the JSON lands on disk for Claude to read afterwards.
///
/// <para>Raw ImGui deliberately. Dev-only surfaces are explicitly exempt from the
/// match-the-main-window rule in CLAUDE.md §3, exactly as <c>ChallengeCreatorWindow</c> is.</para>
/// </summary>
internal sealed class LiveProbeWindow
{
    public bool IsVisible;

    /// <summary>
    /// One thing to do in-game. <see cref="Captured"/> is how many events arrived while this row
    /// was the active label — it is the row's own evidence that the action registered.
    /// </summary>
    private sealed class Task
    {
        public string Label       = string.Empty;
        public string Instruction = string.Empty;
        public bool   Critical;
        public int    Captured;
    }

    /// <summary>
    /// The three Critical rows are the whole of Q13: if gather, craft and buy produce DIFFERENT
    /// condition-flag sets, provenance is real and "gather 20 copper ore" cannot be satisfied by
    /// buying it. If they produce the SAME set, the design needs rethinking and it is much better
    /// to learn that now than after the generator is built on the assumption.
    /// </summary>
    private readonly List<Task> _tasks = new()
    {
        new Task
        {
            Label = "gather", Critical = true,
            Instruction = "Mine or harvest ONE item from a gathering node. Any node, any item.",
        },
        new Task
        {
            Label = "craft", Critical = true,
            Instruction = "Synthesize ONE item. Any recipe, any class. Let it finish.",
        },
        new Task
        {
            Label = "buy", Critical = true,
            Instruction = "Buy ONE item from any gil vendor NPC.",
        },
        new Task
        {
            Label = "mob-loot",
            Instruction = "Kill one enemy that drops an item, and let the loot enter your bags.",
        },
        new Task
        {
            Label = "retainer",
            Instruction = "Withdraw ONE item from a retainer's inventory.",
        },
        new Task
        {
            Label = "market",
            Instruction = "Buy ONE item from the market board.",
        },
    };

    private int    _active = -1;
    private string _lastReportPath = string.Empty;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(620, 560), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Live Probe (dev)##tc_probe", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try
        {
            DrawIntro();
            ImGui.Separator();
            DrawTasks();
            ImGui.Separator();
            DrawSummary();
            ImGui.Separator();
            DrawReportControls();
        }
        catch (Exception ex)
        {
            // A dev tool throwing into the draw loop would take the game down. Never.
            Diag.Error($"[Probe] draw failed: {ex.Message}");
        }

        ImGui.End();
    }

    private void DrawIntro()
    {
        ImGui.TextWrapped(
            "Answers OPEN_QUESTIONS Q13: does ItemAdded + ICondition reveal HOW an item arrived?");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Press Record on a row, do that ONE action in game, press Stop. Repeat for each row. " +
            "Then press Write Report. Order does not matter and you can redo a row.");

        ImGui.Spacing();
        ImGui.TextDisabled($"Events held: {LiveProbe.EventCount}");

        if (LiveProbe.Recording)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
                $"  ● RECORDING \"{LiveProbe.CurrentLabel}\"");
        }
    }

    private void DrawTasks()
    {
        for (int i = 0; i < _tasks.Count; i++)
        {
            var t = _tasks[i];
            bool isActive = _active == i && LiveProbe.Recording;

            ImGui.PushID(i);

            if (isActive)
            {
                if (ImGui.Button("Stop", new Vector2(70, 0)))
                {
                    t.Captured = LiveProbe.EventCount - _countAtStart;
                    LiveProbe.Stop();
                    _active = -1;
                }
            }
            else
            {
                if (ImGui.Button("Record", new Vector2(70, 0)))
                {
                    _countAtStart = LiveProbe.EventCount;
                    _active = i;
                    LiveProbe.Start(t.Label);
                }
            }

            ImGui.SameLine();

            // The row's own status: captured events are the evidence the action registered.
            if (t.Captured > 0)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"[{t.Captured}]");
            else if (t.Critical)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "[ ! ]");
            else
                ImGui.TextDisabled("[   ]");

            ImGui.SameLine();
            ImGui.Text(t.Label);

            ImGui.Indent(96);
            ImGui.TextWrapped(t.Instruction);
            ImGui.Unindent(96);

            ImGui.PopID();
            ImGui.Spacing();
        }
    }

    private int _countAtStart;

    private void DrawSummary()
    {
        if (!ImGui.CollapsingHeader("Condition flags seen, by action")) return;

        ImGui.TextWrapped(
            "If gather / craft / buy show different flag sets here, provenance works.");
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_probe_summary", new Vector2(0, 170), true);
        ImGui.TextUnformatted(LiveProbe.SummariseByLabel());
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawReportControls()
    {
        if (ImGui.Button("Write Report", new Vector2(140, 0)))
        {
            if (LiveProbe.Recording) LiveProbe.Stop();
            _lastReportPath = LiveProbe.WriteReport();
        }

        ImGui.SameLine();

        if (ImGui.Button("Sheet census only", new Vector2(150, 0)))
            _lastReportPath = LiveProbe.WriteReport();

        ImGui.SameLine();

        if (ImGui.Button("Clear events", new Vector2(110, 0)))
        {
            LiveProbe.ClearEvents();
            foreach (var t in _tasks) t.Captured = 0;
        }

        if (string.IsNullOrEmpty(_lastReportPath)) return;

        ImGui.Spacing();
        ImGui.TextWrapped($"Written to: {_lastReportPath}");

        if (ImGui.Button("Open folder"))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = _lastReportPath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Diag.Error($"[Probe] could not open folder: {ex.Message}");
            }
        }
    }
}
#endif
