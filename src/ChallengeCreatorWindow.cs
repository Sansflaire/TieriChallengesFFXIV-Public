#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The whole file sits inside <c>#if DEV_BUILD</c>, so the public build's
/// DLL contains none of it — verified by grepping the compiled binaries.
///
/// Raw ImGui on purpose: PanacheUI has no text input, slider-with-keyboard-entry, or combo, and
/// DESIGN_SYSTEM §10 anti-pattern 8 permits raw ImGui in standard popups and dev tooling. This
/// is an authoring tool, not the public surface.
/// </summary>
internal sealed class ChallengeCreatorWindow
{
    public bool IsVisible;

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly Action           _save;
    private readonly ChallengeTracker _tracker;

    /// <summary>
    /// The popup's queue, not a renderer. Preview therefore works regardless of which toast
    /// renderer is active, or whether PanacheUI loaded at all.
    /// </summary>
    private readonly ToastQueue _toastQueue;

    /// <summary>Ban authoring. Owned here because the Creator is the dev surface.</summary>
    private readonly BanAdmin _banAdmin =
        new(Plugin.PluginInterface.GetPluginConfigDirectory());

    // Draft being authored. InputText needs `ref string`, hence fields.
    private string        _title    = string.Empty;
    private string        _detail   = string.Empty;

    /// <summary>
    /// Optional. Unlike the description this does NOT gate Add — a challenge without a hint is a
    /// perfectly valid challenge, it just offers the player nothing when they ask for help.
    /// </summary>
    private string        _hint     = string.Empty;
    private int           _difficulty;
    private string        _category = string.Empty;
    private string        _newCategory = string.Empty;
    private bool          _creatingNewCategory;
    private ChallengeKind _kind = ChallengeKind.VisitAreas;
    private readonly List<ChallengeArea> _areas = new();

    private uint   _emoteId;
    private string _emoteName = string.Empty;
    private bool   _requireFacing;
    private float  _facingRadians;
    private float  _facingTolerance = 30f;

    private uint   _mountId;
    private string _mountName = string.Empty;

    private GearRequirement _gearMode = GearRequirement.FullOutfit;
    private uint   _outfitId;
    private string _outfitName = string.Empty;
    private uint   _gearItemId;
    private string _gearItemName = string.Empty;
    private bool   _wholeZone;
    private bool   _showProgress = true;

    /// <summary>Per-challenge opt-out of the find-it-yourself rule. See Configuration.AllowMapPin.</summary>
    private bool   _allowMapPin;

    private string _feedback = string.Empty;

    /// <summary>
    /// Null while authoring a new challenge; otherwise the Id being edited in place. Editing
    /// preserves the Id, which is what completion state is keyed by — regenerating it would
    /// silently reset the player's progress on that challenge.
    /// </summary>
    private string? _editingId;

    /// <summary>
    /// The draft's zone, held explicitly rather than read live at save time. Editing a Gridania
    /// challenge while standing in Limsa must NOT silently move it to Limsa.
    /// </summary>
    private ushort _territoryId;
    private string _territoryName = string.Empty;

    private bool _switchToCreate;

    /// <summary>In-world wireframe renderer. Owned here because it visualises this draft.</summary>
    public readonly AreaOverlay Overlay = new();

    /// <summary>Areas of the draft, for the overlay.</summary>
    public IReadOnlyList<ChallengeArea> DraftAreas => _areas;

    /// <summary>Which draft area is expanded in the editor — drawn highlighted in the world.</summary>
    public int SelectedAreaIndex { get; private set; } = -1;

    // Picker filters. Filtered lists are cached and only recomputed when the query changes —
    // the outfit sheet alone is ~1k rows and re-filtering it every frame would be wasteful.
    private string _emoteFilter = string.Empty;
    private string _mountFilter = string.Empty;
    private string _outfitFilter = string.Empty;
    private List<(uint Id, string Name)>? _emoteCache, _mountCache, _outfitCache;
    private string _emoteCacheKey = "\0", _mountCacheKey = "\0", _outfitCacheKey = "\0";

    // ── Composite (InArea) draft state ───────────────────────────────────────

    /// <summary>Draft's area mode. Only meaningful for <see cref="ChallengeKind.InArea"/>.</summary>
    private AreaMode _mode = AreaMode.Single;

    // ── Race draft state ─────────────────────────────────────────────────────
    private int  _raceFailSeconds;
    private bool _raceUseQuit;

    // ── Chain draft state ────────────────────────────────────────────────────

    /// <summary>
    /// Steps of the quest chain being authored. Non-empty makes the draft a Quest.
    ///
    /// <para>Each step owns a full copy of the composite editor's state, so a step is authored the
    /// same way a standalone challenge is. Rather than duplicate all of that, the editor loads ONE
    /// step at a time into the shared draft fields (<see cref="_areas"/>, <see cref="_conditions"/>,
    /// <see cref="_mode"/>) and writes it back when you switch away — see
    /// <see cref="CommitStepDraft"/>. That is why <see cref="_editingStep"/> exists.</para>
    /// </summary>
    private readonly List<ChainStep> _chainSteps = new();

    /// <summary>Index of the step currently loaded into the shared draft fields, or -1.</summary>
    private int _editingStep = -1;

    /// <summary>Progress resets on logout. Off by default — see Configuration.SessionOnly.</summary>
    private bool _sessionOnly;

    /// <summary>
    /// Conditions per draft area, parallel to <see cref="_areas"/>.
    ///
    /// <para>Parallel lists rather than a list of <see cref="AreaRequirement"/> so that
    /// <see cref="DrawAreaEditor"/>, <see cref="AddAreaAtPlayer"/>, <see cref="DraftAreas"/> and the
    /// in-world overlay all keep working on <c>_areas</c> unchanged. The two are kept the same
    /// length by <see cref="SyncConditionSlots"/>, which runs at the top of the composite editor —
    /// so a missed add site self-heals on the next frame instead of throwing.</para>
    /// </summary>
    private readonly List<List<ChallengeCondition>> _conditions = new();

    /// <summary>Per-area step budget, parallel to <see cref="_areas"/>. InOrder mode only.</summary>
    private readonly List<int> _within = new();

    /// <summary>
    /// Filter text and cached results for the pickers inside condition editors, keyed by a string
    /// built from the area and condition index. The shared <see cref="DrawFilteredPicker"/> takes
    /// <c>ref</c> fields, which cannot work for a list whose length changes at runtime.
    /// </summary>
    private readonly Dictionary<string, string> _condFilter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Key, List<(uint Id, string Name)> List)> _condCache =
        new(StringComparer.Ordinal);

    private const string NewCategorySentinel = "＋ New category…";

    // ── Existing tab: search + collapse state ────────────────────────────────

    /// <summary>
    /// Filter for the Existing list. Matches title, category, zone, description, hint and GUID,
    /// so pasting a GUID out of the main window's right-click menu finds its challenge.
    /// </summary>
    private string _existingSearch = string.Empty;

    /// <summary>
    /// One-frame instruction from the Expand all / Collapse all buttons: 1 opens, −1 closes,
    /// 0 leaves every header alone.
    /// </summary>
    /// <remarks>
    /// ImGui owns tree open/closed state, keyed by node ID, which is exactly what makes it survive
    /// the list being rebuilt every frame. The only way to override it in bulk is to call
    /// <c>SetNextItemOpen</c> on each node for one frame, so this is consumed at the end of the
    /// draw rather than being persistent state of our own.
    /// </remarks>
    private int _existingSetOpen;

    /// <summary>Placeholder for a challenge whose Category is blank, so it can still be grouped.</summary>
    private const string UncategorisedLabel = "(uncategorised)";

    public ChallengeCreatorWindow(Configuration config, CompletionStore store, Action save,
                                  ChallengeTracker tracker, ToastQueue toastQueue)
    {
        _config  = config;
        _store   = store;
        _save    = save;
        _tracker = tracker;
        _toastQueue = toastQueue;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        // A fresh draft binds to the zone you are standing in the moment the creator opens, so
        // GearInArea (which is zone-gated) is well-formed without having to place a volume.
        if (_territoryId == 0 && _editingId == null) CaptureZone();

        ImGui.SetNextWindowSize(new Vector2(620, 760), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Challenge Creator (dev)##tc_creator", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##tc_creator_tabs"))
        {
            var createFlags = _switchToCreate ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _switchToCreate = false;

            // No `ref bool` here, and that is the entire point.
            //
            // The p_open overload was used purely to reach the flags parameter, so that
            // _switchToCreate could select this tab after "Edit" is pressed on the Existing tab.
            // Passing p_open ALSO draws a close button on the tab, and ImGui writes false into the
            // bool when it is clicked. The field was called _tabAlwaysOpen and nothing ever set it
            // back to true, so one click permanently deleted the Create/Edit tab — the only way to
            // author anything — for the rest of the session, with no way back short of reloading
            // the plugin. Take the flags-only overload; there is nothing here to close.
            string createLabel = _editingId == null ? "Create" : "Edit";
            if (ImGui.BeginTabItem($"{createLabel}###tc_tab_create", createFlags))
            {
                DrawCreateTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"Existing ({_config.CustomChallenges.Count})"))
            {
                DrawExistingTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Publish"))
            {
                DrawPublishTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Bans"))
            {
                _banAdmin.Draw(_config);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private void DrawCreateTab()
    {
        if (_editingId != null)
        {
            ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f), "Editing an existing challenge.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel edit", new Vector2(110, 22)))
            {
                _editingId = null;
                ResetDraft();
                _feedback = "Edit cancelled.";
            }
        }

        DrawKindPicker();
        ImGui.Separator();

        DrawIdentity();
        DrawZoneRow();
        ImGui.Separator();

        // Only offered for kinds that have a quantity — a single-condition challenge has no
        // "2 of 4" to show, so a toggle for it would be a lie.
        if (ChallengeCatalog.HasStepProgress(_kind))
        {
            ImGui.Checkbox("Show progress (e.g. \"2/4\") in the challenge list", ref _showProgress);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On by default. Turn off if revealing how many steps remain\n"
                               + "would spoil the challenge.");
            ImGui.Separator();
        }

        // The catalogue's discoverability rule is zone name plus written hint — finding the exact
        // spot IS the challenge. This is the deliberate per-challenge exception, so it is off by
        // default and says plainly what turning it on gives away.
        ImGui.Checkbox("Allow map pin", ref _allowMapPin);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Off by default — finding the spot is normally part of the challenge.\n\n"
              + "On: the \"you are in this zone\" marker on the row becomes a button that drops the\n"
              + "game's map flag on this challenge's location. Only ever offered while the player is\n"
              + "standing in the zone, and it points at the NEXT thing they have to do — the current\n"
              + "step of a chain, the first objective they have not done, or a race's start line.");
        }

        if (_allowMapPin)
        {
            ImGui.TextColored(Warn,
                "This challenge's location will be revealed on the map. Use it where the doing is "
              + "the challenge, not the finding.");
        }

        ImGui.Separator();

        switch (_kind)
        {
            case ChallengeKind.VisitAreas:
            case ChallengeKind.VisitAreasInOrder:
                DrawAreaList(multiple: true);
                break;

            case ChallengeKind.EmoteAtArea:
                DrawEmoteSection();
                ImGui.Separator();
                DrawAreaList(multiple: false);
                break;

            case ChallengeKind.MountInArea:
                DrawMountSection();
                ImGui.Separator();
                DrawAreaList(multiple: false);
                break;

            case ChallengeKind.GearInArea:
                DrawGearSection();
                ImGui.Separator();
                ImGui.Checkbox("Whole zone (no area needed)", ref _wholeZone);
                if (!_wholeZone) DrawAreaList(multiple: true);
                break;

            case ChallengeKind.InArea:
                DrawCompositeSection();
                break;

            case ChallengeKind.RaceTimer:
                DrawRaceSection();
                break;
        }

        ImGui.Separator();
        DrawAddButton();
    }

    /// <summary>
    /// What the kind picker offers.
    ///
    /// <para><b>Only the composite kind is here.</b> Kinds 1–6 still evaluate, and every challenge
    /// already authored as one keeps working forever, but nothing new is authored that way —
    /// everything they could express is a condition on an area now, and several of them
    /// (emote + target, gear per stop) could not be expressed at all. Existing challenges of the
    /// legacy kinds still load into the editor and still show their own sections; see
    /// <see cref="LegacyKinds"/>.</para>
    /// </summary>
    private static readonly (ChallengeKind Kind, string Label, string Blurb)[] Kinds =
    {
        (ChallengeKind.InArea, "In area — conditions per place",
            "One or more areas, each with its own conditions: emote, mount, minion, outfit, gear "
          + "pieces, target, job, time of day, carried item, game state. This is the kind to use."),
        (ChallengeKind.RaceTimer, "Race — timed run between two points",
            "The player arms it by standing in the start area, presses Start, and finishes by "
          + "reaching the finish area. Best time is recorded and shown on the row."),
    };

    /// <summary>
    /// Retired kinds, shown only when an existing challenge of that kind is loaded for editing so
    /// the picker can display its name instead of "?".
    /// </summary>
    private static readonly (ChallengeKind Kind, string Label, string Blurb)[] LegacyKinds =
    {
        (ChallengeKind.VisitAreas,        "Visit all areas (any order) [legacy]",
            "Enter every area at least once within one login session."),
        (ChallengeKind.VisitAreasInOrder, "Visit all areas (in order) [legacy]",
            "Enter the areas in the listed order. Entering a later one early does nothing."),
        (ChallengeKind.EmoteAtArea,       "Emote at a location [legacy]",
            "Perform a chosen emote inside the area. Optionally while facing a captured direction."),
        (ChallengeKind.MountInArea,       "Specific mount in an area [legacy]",
            "Be riding a chosen mount while inside the area."),
        (ChallengeKind.GearInArea,        "Outfit / gear in a zone or area [legacy]",
            "Wear a complete Glamour Dresser outfit, or one specific item, in a zone or area."),
    };

    private void DrawKindPicker()
    {
        ImGui.TextUnformatted("Challenge type");

        string current = "?";
        string blurb   = string.Empty;
        bool   legacy  = false;

        foreach (var (k, label, b) in Kinds)
            if (k == _kind) { current = label; blurb = b; }

        foreach (var (k, label, b) in LegacyKinds)
            if (k == _kind) { current = label; blurb = b; legacy = true; }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##tc_kind", current))
        {
            foreach (var (k, label, b) in Kinds)
            {
                if (ImGui.Selectable(label, k == _kind)) _kind = k;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(b);
            }

            // A legacy challenge being edited keeps its own kind selectable, so an edit does not
            // silently convert it. Converting would raise its version floor and drop it out of the
            // list for every player still on an older build — see ChallengeKind.InArea.
            if (legacy)
            {
                ImGui.Separator();
                foreach (var (k, label, b) in LegacyKinds)
                {
                    if (k != _kind) continue;
                    if (ImGui.Selectable(label, true)) _kind = k;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(b);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled(blurb);

        if (legacy)
        {
            ImGui.TextColored(Warn,
                "This is a retired type. It still works and will keep working — but switching it to "
              + "\"In area\" republishes it as a newer challenge that older plugins cannot load.");
        }
    }

    private void DrawIdentity()
    {
        // includeEmpty: authoring is exactly the case where a category with nothing in it yet
        // still has to be selectable — otherwise creating one ahead of its content is impossible.
        var categories = ChallengeCatalog.Categories(_config, includeEmpty: true);
        if (string.IsNullOrEmpty(_category) && categories.Count > 0) _category = categories[0];

        ImGui.TextUnformatted("Category");
        ImGui.SetNextItemWidth(-1);
        string comboLabel = _creatingNewCategory ? NewCategorySentinel : _category;
        if (ImGui.BeginCombo("##tc_cat", comboLabel))
        {
            foreach (var cat in categories)
                if (ImGui.Selectable(cat, !_creatingNewCategory && cat == _category))
                {
                    _category = cat;
                    _creatingNewCategory = false;
                }

            ImGui.Separator();
            if (ImGui.Selectable(NewCategorySentinel, _creatingNewCategory)) _creatingNewCategory = true;
            ImGui.EndCombo();
        }

        if (_creatingNewCategory)
        {
            ImGui.SetNextItemWidth(-220);
            ImGui.InputText("##tc_newcat", ref _newCategory, 64);

            // Create it NOW, as its own thing, rather than only as a side effect of adding a
            // challenge to it. That is the whole difference: the category survives whether or
            // not this draft is ever saved.
            ImGui.SameLine();
            string trimmed = _newCategory.Trim();
            bool   exists  = ChallengeCatalog.Categories(_config, includeEmpty: true)
                                             .Exists(c => string.Equals(c, trimmed, StringComparison.Ordinal));

            bool canCreate = trimmed.Length > 0 && !exists;
            if (!canCreate) ImGui.BeginDisabled();
            if (ImGui.Button("Create category", new Vector2(140, 22)))
            {
                _config.CustomCategories.Add(trimmed);
                _config.DefinitionsChanged();
                _save();

                _category            = trimmed;
                _creatingNewCategory = false;
                _newCategory         = string.Empty;
                _feedback            = $"Created category \"{trimmed}\". Publish to share it.";
            }
            if (!canCreate) ImGui.EndDisabled();

            if (exists) ImGui.TextDisabled("That category already exists.");
        }

        // Name and description are both REQUIRED. Flagged inline so it is obvious why Add is
        // disabled rather than the button just being dead.
        ImGui.TextUnformatted("Name (required)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##tc_title", ref _title, 128);
        if (string.IsNullOrWhiteSpace(_title)) MissingLabel("A name is required.");

        ImGui.TextUnformatted("Description (required)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##tc_detail", ref _detail, 256);
        if (string.IsNullOrWhiteSpace(_detail)) MissingLabel("A description is required — it is the line shown in the completion popup.");

        // Optional by design: a hint is a courtesy, not a requirement. Leaving it empty makes the
        // player-facing control read "NO HINT" rather than offering a button that reveals nothing.
        ImGui.TextUnformatted("Hint (optional)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##tc_hint", ref _hint, 256);
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(_hint)
            ? "No hint — the challenge's Hint button will show as unavailable."
            : "Shown only when the player clicks Hint; it replaces the description line.");

        // Optional, and 0 is a real answer rather than a missing one: an unrated challenge shows
        // no meter at all and sorts to the bottom of Difficulty order. Nothing in the plugin can
        // measure this, so leaving it unset is more honest than defaulting it to 1.
        ImGui.Spacing();
        ImGui.TextUnformatted("Difficulty (optional)");
        ImGui.SetNextItemWidth(220);
        ImGui.SliderInt("##tc_difficulty", ref _difficulty, 0, 5,
                        _difficulty == 0 ? "unrated" : "%d / 5");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear##tc_diff_clear")) _difficulty = 0;
    }

    private static void MissingLabel(string text) =>
        ImGui.TextColored(new Vector4(0.90f, 0.42f, 0.38f, 1f), text);

    // ── Areas ────────────────────────────────────────────────────────────────

    private void DrawAreaList(bool multiple)
    {
        ImGui.TextUnformatted(multiple ? "Areas" : "Area");

        // The in-world wireframe is the whole reason precise placement is possible.
        ImGui.Checkbox("Draw volumes in world", ref Overlay.Enabled);
        ImGui.SameLine();
        ImGui.Checkbox("Also show saved challenges here", ref Overlay.ShowSaved);

        bool canAdd = multiple || _areas.Count == 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("＋ Add at my position", new Vector2(190, 26)))
            AddAreaAtPlayer();
        if (!canAdd) ImGui.EndDisabled();

        if (_areas.Count == 0)
        {
            ImGui.TextDisabled("No areas yet. Stand where you want the trigger and press the button.");
            return;
        }

        var lp = Plugin.ObjectTable.LocalPlayer;
        Vector3? playerPos = lp?.Position;

        int removeAt = -1;
        for (int i = 0; i < _areas.Count; i++)
        {
            ImGui.PushID(i);
            var a = _areas[i];

            string header = multiple
                ? $"{i + 1}. {a.Name}  ({a.Describe()})"
                : $"{a.Name}  ({a.Describe()})";

            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
            {
                SelectedAreaIndex = i;   // highlighted in the world overlay
                DrawAreaEditor(a, playerPos);
                if (ImGui.Button("Delete area", new Vector2(110, 22))) removeAt = i;
            }

            ImGui.PopID();
        }

        if (removeAt >= 0) _areas.RemoveAt(removeAt);
    }

    // ── Chain editor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Write the shared draft fields back into the step they were loaded from. Called before every
    /// switch away from a step, and before saving.
    ///
    /// <para>Without this, editing step 2 and then clicking step 3 would silently discard step 2's
    /// work — the shared fields would simply be overwritten.</para>
    /// </summary>
    private void CommitStepDraft()
    {
        if (_editingStep < 0 || _editingStep >= _chainSteps.Count) return;

        SyncConditionSlots();
        var step = _chainSteps[_editingStep];

        step.Mode = _mode;
        step.Requirements.Clear();

        for (int i = 0; i < _areas.Count; i++)
        {
            var req = new AreaRequirement
            {
                Area          = _areas[i].Clone(),
                Label         = _areas[i].Name,
                WithinSeconds = _mode == AreaMode.InOrder ? _within[i] : 0,
            };
            foreach (var c in _conditions[i]) req.Conditions.Add(c.Clone());
            step.Requirements.Add(req);

            if (_mode == AreaMode.Single) break;
        }

        // Immediately, while authoring — not only at save — so the step list shows the zone the
        // position is really in rather than the one it inherited from the challenge.
        ChallengeCatalog.RebindZonesToAreas(new CustomChallenge { ChainSteps = { step } });
    }

    /// <summary>Load a step into the shared draft fields, committing whatever was there first.</summary>
    private void LoadStepDraft(int index)
    {
        CommitStepDraft();

        if (index < 0 || index >= _chainSteps.Count) { _editingStep = -1; return; }

        var step = _chainSteps[index];
        _editingStep = index;
        _mode = step.Mode;

        _areas.Clear();
        _conditions.Clear();
        _within.Clear();

        foreach (var r in step.Requirements)
        {
            var area = r.Area?.Clone() ?? new ChallengeArea();
            if (!string.IsNullOrWhiteSpace(r.Label)) area.Name = r.Label;
            _areas.Add(area);

            var conds = new List<ChallengeCondition>();
            foreach (var c in r.Conditions) conds.Add(c.Clone());
            _conditions.Add(conds);

            _within.Add(r.WithinSeconds);
        }

        SyncConditionSlots();
    }

    private void DrawChainSection()
    {
        ImGui.TextUnformatted("Quest chain");
        ImGui.TextDisabled(
            "Steps are done in order. Each one replaces the last on the challenge row, and only "
          + "finishing the last step completes the challenge. Leave this empty for a normal "
          + "challenge or an adventure.");

        if (ImGui.Button("＋ Add step", new Vector2(130, 26)))
        {
            CommitStepDraft();
            _chainSteps.Add(new ChainStep
            {
                Id            = ChallengeCatalog.NewId(),
                Title         = $"Step {_chainSteps.Count + 1}",
                TerritoryId   = _territoryId,
                TerritoryName = _territoryName,
            });
            LoadStepDraft(_chainSteps.Count - 1);
        }

        if (_chainSteps.Count == 0)
        {
            ImGui.TextDisabled("No steps — this draft is not a chain.");
            return;
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{_chainSteps.Count} step(s)");

        int removeAt = -1;
        int moveUp   = -1;

        for (int i = 0; i < _chainSteps.Count; i++)
        {
            ImGui.PushID(3000 + i);
            var step = _chainSteps[i];

            bool editing = i == _editingStep;
            string label = string.IsNullOrWhiteSpace(step.Title) ? "(unnamed step)" : step.Title;
            string flag  = step.IsWellFormed() ? string.Empty : "  [incomplete]";

            if (ImGui.CollapsingHeader($"{i + 1}. {label}{flag}###chainstep{i}",
                                       editing ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
            {
                if (!editing)
                {
                    if (ImGui.Button("Edit this step's areas", new Vector2(190, 24)))
                        LoadStepDraft(i);
                    ImGui.TextDisabled($"{step.Requirements.Count} area(s), {step.Mode}");
                }

                string t = step.Title;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##steptitle", "step name", ref t, 128)) step.Title = t;

                string d = step.Detail;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##stepdetail", "what the player must do", ref d, 512))
                    step.Detail = d;

                string h = step.Hint;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##stephint", "hint (optional)", ref h, 512)) step.Hint = h;

                // A step's own zone is what re-files the chain as the player advances. Captured
                // explicitly, never read from the player at save time — same rule as the challenge's.
                ImGui.TextUnformatted($"Zone: {(step.TerritoryId == 0 ? "(chain's zone)" : step.TerritoryName)}");
                ImGui.SameLine();
                if (ImGui.Button("Set to my current zone##stepzone", new Vector2(190, 22)))
                {
                    step.TerritoryId   = (ushort)Plugin.ClientState.TerritoryType;
                    step.TerritoryName = PlayerStateReader.ZoneName(step.TerritoryId);
                }

                if (editing)
                {
                    ImGui.Separator();
                    ImGui.TextColored(Good, "Editing this step's areas below.");
                }

                ImGui.Separator();
                if (i > 0 && ImGui.Button("Move up", new Vector2(90, 22))) moveUp = i;
                ImGui.SameLine();
                if (ImGui.Button("Delete step", new Vector2(110, 22))) removeAt = i;
            }

            ImGui.PopID();
        }

        if (moveUp > 0)
        {
            CommitStepDraft();
            (_chainSteps[moveUp - 1], _chainSteps[moveUp]) = (_chainSteps[moveUp], _chainSteps[moveUp - 1]);

            // Steps are keyed by GUID, not position, so reordering carries a player's progress
            // with the step rather than shifting them onto a different one. The editing index
            // still has to follow the move so the shared draft keeps pointing at the same step.
            if (_editingStep == moveUp)          _editingStep = moveUp - 1;
            else if (_editingStep == moveUp - 1) _editingStep = moveUp;
        }

        if (removeAt >= 0)
        {
            _chainSteps.RemoveAt(removeAt);
            if (_editingStep == removeAt)     _editingStep = -1;
            else if (_editingStep > removeAt) _editingStep--;
        }

        // The shared area editor below belongs to whichever step is loaded. Saying so is the whole
        // difference between an obvious UI and a baffling one.
        ImGui.Separator();
        if (_editingStep >= 0 && _editingStep < _chainSteps.Count)
        {
            ImGui.TextColored(Good,
                $"The areas and conditions below belong to step {_editingStep + 1}: "
              + $"{_chainSteps[_editingStep].Title}");
        }
        else
        {
            ImGui.TextColored(Warn,
                "No step selected — open a step above and press \"Edit this step's areas\" first. "
              + "Areas placed now belong to the challenge itself, not to any step.");
        }
    }

    // ── Race editor ──────────────────────────────────────────────────────────

    /// <summary>Fixed slot order inside <see cref="_areas"/> while authoring a race.</summary>
    private const int RaceStartIdx  = 0;
    private const int RaceFinishIdx = 1;
    private const int RaceQuitIdx   = 2;

    private static readonly string[] RaceSlotNames = { "Start", "Finish", "Quit area" };

    /// <summary>
    /// A race always has its three volumes, so they are pre-created rather than added by the
    /// author.
    ///
    /// <para>They live in <see cref="_areas"/> at fixed indices so the whole existing apparatus —
    /// <see cref="DrawAreaEditor"/>, <see cref="DraftAreas"/>, the in-world wireframe overlay,
    /// <see cref="SelectedAreaIndex"/> — works on them unchanged. There is no Delete here for the
    /// same reason: a race missing its finish line is not a state worth being able to reach, and
    /// removing a slot would shift the indices of the ones after it.</para>
    /// </summary>
    private void EnsureRaceSlots()
    {
        while (_areas.Count < 3)
        {
            _areas.Add(new ChallengeArea
            {
                Name  = RaceSlotNames[_areas.Count],
                MapId = PlayerStateReader.CurrentMapId(),
            });
        }

        // A name is what identifies the slot in the world overlay, so keep them honest even if a
        // challenge was loaded from an older shape or the author renamed one.
        for (int i = 0; i < 3; i++)
            if (string.IsNullOrWhiteSpace(_areas[i].Name)) _areas[i].Name = RaceSlotNames[i];
    }

    private void DrawRaceSection()
    {
        EnsureRaceSlots();

        ImGui.Checkbox("Draw volumes in world", ref Overlay.Enabled);
        ImGui.SameLine();
        ImGui.Checkbox("Also show saved challenges here", ref Overlay.ShowSaved);

        ImGui.TextDisabled(
            "The player arms the race by standing in Start, presses Start!, and finishes by "
          + "reaching Finish. Re-entering Start restarts the clock.");

        ImGui.Separator();

        int fail = _raceFailSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragInt("Time limit, seconds (0 = untimed)##racefail", ref fail, 1f, 0, 3600))
            _raceFailSeconds = Math.Max(0, fail);

        if (_raceFailSeconds > 0)
            ImGui.TextDisabled($"Runs longer than {CompletionStore.FormatRaceTime(_raceFailSeconds)} fail.");
        else
            ImGui.TextDisabled("No time limit — the run only ends by finishing, leaving, or giving up.");

        ImGui.Separator();

        ImGui.Checkbox("End the run if the player leaves a bounding area", ref _raceUseQuit);
        if (!_raceUseQuit)
        {
            ImGui.TextDisabled(
                "Off: the run can only end by finishing, timing out, giving up, or leaving the zone.");
        }
        else
        {
            ImGui.TextColored(Warn,
                "Size this generously. The player cannot see it, so a run ending inside what looks "
              + "like the course reads as a bug.");
        }

        ImGui.Separator();

        var lp = Plugin.ObjectTable.LocalPlayer;
        Vector3? playerPos = lp?.Position;

        for (int i = 0; i < 3; i++)
        {
            if (i == RaceQuitIdx && !_raceUseQuit) continue;

            ImGui.PushID(500 + i);
            var a = _areas[i];

            string role = i switch
            {
                RaceStartIdx  => "Start line",
                RaceFinishIdx => "Finish line",
                _             => "Bounding area (stay inside)",
            };

            if (ImGui.CollapsingHeader($"{role} — {a.Describe()}###raceslot{i}",
                                       ImGuiTreeNodeFlags.DefaultOpen))
            {
                SelectedAreaIndex = i;
                DrawAreaEditor(a, playerPos);
            }

            ImGui.PopID();
        }

        // Overlapping start and finish would complete the race the instant it began. Cheap centre
        // test rather than a real volume intersection — close centres is the mistake that actually
        // happens (capturing both from the same standing position).
        float gap = Vector3.Distance(_areas[RaceStartIdx].Center, _areas[RaceFinishIdx].Center);
        if (gap < _areas[RaceStartIdx].EffectiveRadius + _areas[RaceFinishIdx].EffectiveRadius)
        {
            ImGui.TextColored(Warn,
                $"Start and finish are {gap:0.#}y apart and may overlap — the race could complete "
              + "the moment it starts.");
        }
    }

    // ── Composite (InArea) editor ────────────────────────────────────────────

    /// <summary>
    /// Keep the per-area condition and timing lists the same length as <see cref="_areas"/>.
    /// Called at the top of the composite editor so a drift is corrected before anything indexes
    /// into them — cheaper than trusting every add/remove site to stay in step forever.
    /// </summary>
    private void SyncConditionSlots()
    {
        while (_conditions.Count < _areas.Count) _conditions.Add(new List<ChallengeCondition>());
        while (_conditions.Count > _areas.Count) _conditions.RemoveAt(_conditions.Count - 1);

        while (_within.Count < _areas.Count) _within.Add(0);
        while (_within.Count > _areas.Count) _within.RemoveAt(_within.Count - 1);
    }

    private static readonly (AreaMode Mode, string Label, string Blurb)[] Modes =
    {
        (AreaMode.Single,   "Single area",
            "One place, one set of conditions."),
        (AreaMode.AnyOrder, "Multiple areas — any order",
            "Every area must be satisfied at least once, in whatever order, within one login session."),
        (AreaMode.InOrder,  "Multiple areas — in order",
            "Areas must be satisfied in the listed order. A later one reached early does nothing. "
          + "Each step after the first can carry its own time limit."),
    };

    private void DrawCompositeSection()
    {
        SyncConditionSlots();

        DrawChainSection();
        ImGui.Separator();

        ImGui.TextUnformatted("How many places?");

        string current = "?", blurb = string.Empty;
        foreach (var (m, label, b) in Modes)
            if (m == _mode) { current = label; blurb = b; }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##tc_mode", current))
        {
            foreach (var (m, label, b) in Modes)
            {
                if (ImGui.Selectable(label, m == _mode)) _mode = m;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(b);
            }
            ImGui.EndCombo();
        }
        if (!string.IsNullOrEmpty(blurb)) ImGui.TextDisabled(blurb);

        // Only meaningful once there is more than one thing to be part-way through.
        if (_mode != AreaMode.Single || _chainSteps.Count > 0)
        {
            ImGui.Checkbox("All in one login session (progress resets on logout)", ref _sessionOnly);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Off by default. An adventure or quest the player is told to take "
                               + "their time over must not lose its progress every logout.\n"
                               + "Turn ON only when doing it in one sitting is the point.");
        }

        // Switching back to Single with several areas already placed would silently orphan
        // everything past the first — say so rather than dropping them at save time.
        if (_mode == AreaMode.Single && _areas.Count > 1)
        {
            ImGui.TextColored(Warn,
                $"Single mode uses one area, but {_areas.Count} are placed. "
              + "Delete the extras or switch to a multiple mode.");
        }

        ImGui.Separator();
        DrawRequirementList();
    }

    /// <summary>
    /// The area list, with each area's condition set nested inside it. Deliberately a near-twin of
    /// <see cref="DrawAreaList"/> rather than a parameterised version of it: that method serves the
    /// five legacy kinds and is not worth destabilising for a kind with a different shape.
    /// </summary>
    private void DrawRequirementList()
    {
        ImGui.Checkbox("Draw volumes in world", ref Overlay.Enabled);
        ImGui.SameLine();
        ImGui.Checkbox("Also show saved challenges here", ref Overlay.ShowSaved);

        bool canAdd = _mode != AreaMode.Single || _areas.Count == 0;
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button("＋ Add area at my position", new Vector2(210, 26)))
        {
            AddAreaAtPlayer();
            SyncConditionSlots();
        }
        if (!canAdd) ImGui.EndDisabled();

        if (_areas.Count == 0)
        {
            ImGui.TextDisabled("No areas yet. Stand where you want the trigger and press the button.");
            return;
        }

        var lp = Plugin.ObjectTable.LocalPlayer;
        Vector3? playerPos = lp?.Position;

        int removeAt = -1;
        for (int i = 0; i < _areas.Count; i++)
        {
            ImGui.PushID(i);
            var a = _areas[i];
            var conds = _conditions[i];

            string summary = conds.Count == 0
                ? "just be here"
                : $"{conds.Count} condition{(conds.Count == 1 ? "" : "s")}";

            if (ImGui.CollapsingHeader($"{i + 1}. {a.Name} — {summary}###area{i}",
                                       ImGuiTreeNodeFlags.DefaultOpen))
            {
                SelectedAreaIndex = i;   // highlighted in the world overlay

                DrawAreaEditor(a, playerPos);

                // Step budget — the "within X seconds of Y" relation. Only offered where it has a
                // meaning: the first step has no previous step to be measured from.
                if (_mode == AreaMode.InOrder && i > 0)
                {
                    ImGui.Separator();
                    int secs = _within[i];
                    ImGui.SetNextItemWidth(160);
                    if (ImGui.DragInt("Seconds allowed since the previous area##within", ref secs, 1f, 0, 3600))
                        _within[i] = Math.Max(0, secs);

                    ImGui.TextDisabled(_within[i] == 0
                        ? "0 = untimed."
                        : $"Miss it by more than {_within[i]}s and the whole sequence restarts.");
                }

                ImGui.Separator();
                DrawConditionList(i, conds);

                ImGui.Separator();
                if (ImGui.Button("Delete area", new Vector2(110, 22))) removeAt = i;
            }

            ImGui.PopID();
        }

        if (removeAt >= 0)
        {
            _areas.RemoveAt(removeAt);
            _conditions.RemoveAt(removeAt);
            _within.RemoveAt(removeAt);
        }
    }

    /// <summary>Every condition attached to one area, plus the add button.</summary>
    private void DrawConditionList(int areaIndex, List<ChallengeCondition> conds)
    {
        ImGui.TextUnformatted("Conditions while inside this area");
        ImGui.TextDisabled("All of them must hold at the same time. No conditions = just be here.");

        if (ImGui.Button("＋ Add condition", new Vector2(150, 24)))
            conds.Add(new ChallengeCondition { Type = ConditionType.Emote });

        int removeAt = -1;
        for (int c = 0; c < conds.Count; c++)
        {
            ImGui.PushID(1000 + c);
            var cond = conds[c];

            // Indent + separator rather than a child window: this binding has no AutoResizeY, and a
            // fixed-height child would either clip the taller editors (gear pieces) or leave a
            // wasteland under the short ones.
            ImGui.Separator();
            ImGui.Indent(12f);
            {
                ImGui.SetNextItemWidth(220);
                if (ImGui.BeginCombo("##ctype", ConditionLabel(cond.Type)))
                {
                    foreach (ConditionType t in Enum.GetValues<ConditionType>())
                    {
                        if (ImGui.Selectable(ConditionLabel(t), t == cond.Type))
                        {
                            cond.Type = t;
                            SeedCondition(cond);
                        }
                    }
                    ImGui.EndCombo();
                }

                if (cond.Type != ConditionType.Presence)
                {
                    ImGui.SameLine();
                    bool neg = cond.Negate;
                    if (ImGui.Checkbox("NOT##neg", ref neg)) cond.Negate = neg;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Invert this condition — e.g. \"while NOT mounted\".");
                }

                ImGui.SameLine();
                if (ImGui.Button("Remove##cond", new Vector2(80, 22))) removeAt = c;

                DrawConditionFields(areaIndex, c, cond);

                // Live truth. The single most useful thing an authoring tool can show — placing a
                // condition is guesswork otherwise, exactly like the area containment readout.
                if (cond.IsWellFormed() && Plugin.ClientState.IsLoggedIn)
                {
                    bool now = ConditionEvaluator.HoldsNow(cond);
                    ImGui.TextColored(now ? Good : Warn,
                                      now ? "TRUE right now" : "false right now");
                }
                else if (!cond.IsWellFormed())
                {
                    ImGui.TextColored(Warn, "Incomplete — this condition needs a value.");
                }
            }
            ImGui.Unindent(12f);

            ImGui.PopID();
        }

        if (removeAt >= 0) conds.RemoveAt(removeAt);

        // A set that is nothing but modifiers can never stand up on its own — see
        // ChallengeCondition.IsWellFormed for why facing alone is not a requirement.
        if (conds.Count > 0)
        {
            bool anyReal = false;
            foreach (var c2 in conds) if (c2.Type != ConditionType.Facing) anyReal = true;
            if (!anyReal)
                ImGui.TextColored(Warn,
                    "Facing on its own cannot complete a challenge — add something for it to qualify.");
        }
    }

    /// <summary>Fresh defaults when the author switches a condition to a different type.</summary>
    private static void SeedCondition(ChallengeCondition c)
    {
        switch (c.Type)
        {
            case ConditionType.Facing:
                var lp = Plugin.ObjectTable.LocalPlayer;
                if (lp != null) c.FacingRadians = lp.Rotation;
                if (c.FacingToleranceDeg <= 0f) c.FacingToleranceDeg = 30f;
                break;

            case ConditionType.TimeOfDay:
                if (c.StartHour == c.EndHour) { c.StartHour = 18; c.EndHour = 6; }
                break;

            case ConditionType.HasItem:
                if (c.ItemCount <= 0) c.ItemCount = 1;
                break;
        }
    }

    private void DrawConditionFields(int areaIndex, int condIndex, ChallengeCondition c)
    {
        string key = $"{areaIndex}_{condIndex}";

        switch (c.Type)
        {
            case ConditionType.Presence:
                ImGui.TextDisabled("Nothing to configure — the area itself is the condition.");
                break;

            case ConditionType.Emote:
                ImGui.TextUnformatted(c.EmoteId == 0 ? "(no emote chosen)" : $"{c.EmoteName} (#{c.EmoteId})");
                if (ImGui.Button("Use my current emote", new Vector2(180, 22)))
                {
                    uint id = PlayerStateReader.CurrentEmoteId();
                    if (id != 0) { c.EmoteId = id; c.EmoteName = PlayerStateReader.EmoteName(id); }
                    else _feedback = "No emote is running right now.";
                }
                DrawCondPicker(key, PlayerStateReader.AllEmotes,
                               (id, n) => { c.EmoteId = id; c.EmoteName = n; });
                break;

            case ConditionType.Mount:
                ImGui.TextUnformatted(c.MountId == 0 ? "(no mount chosen)" : $"{c.MountName} (#{c.MountId})");
                if (ImGui.Button("Use my current mount", new Vector2(180, 22)))
                {
                    uint id = PlayerStateReader.CurrentMountId();
                    if (id != 0) { c.MountId = id; c.MountName = PlayerStateReader.MountName(id); }
                    else _feedback = "You are not mounted.";
                }
                DrawCondPicker(key, PlayerStateReader.AllMounts,
                               (id, n) => { c.MountId = id; c.MountName = n; });
                break;

            case ConditionType.Minion:
                ImGui.TextUnformatted(c.MinionId == 0 ? "(no minion chosen)" : $"{c.MinionName} (#{c.MinionId})");
                if (ImGui.Button("Use my current minion", new Vector2(180, 22)))
                {
                    uint id = PlayerStateReader.CurrentMinionId();
                    if (id != 0) { c.MinionId = id; c.MinionName = PlayerStateReader.MinionName(id); }
                    else _feedback = "You have no minion out.";
                }
                DrawCondPicker(key, PlayerStateReader.AllMinions,
                               (id, n) => { c.MinionId = id; c.MinionName = n; });
                break;

            case ConditionType.FullOutfit:
                ImGui.TextUnformatted(c.OutfitSetId == 0 ? "(no outfit chosen)" : $"{c.OutfitName} (#{c.OutfitSetId})");
                DrawCondPicker(key, PlayerStateReader.AllOutfits,
                               (id, n) => { c.OutfitSetId = id; c.OutfitName = n; });
                break;

            case ConditionType.GearPieces:
                DrawGearPieces(key, c);
                break;

            case ConditionType.Target:
                ImGui.TextUnformatted(c.TargetDataId == 0
                    ? "(no target chosen)"
                    : $"{c.TargetName} (#{c.TargetDataId})");
                if (ImGui.Button("Use my current target", new Vector2(180, 22)))
                {
                    uint id = PlayerStateReader.CurrentTargetDataId();
                    if (id != 0)
                    {
                        c.TargetDataId = id;
                        c.TargetName   = PlayerStateReader.CurrentTargetName();
                    }
                    else _feedback = "You are not targeting anything.";
                }
                ImGui.TextDisabled("Stored by NPC identity, so it matches that NPC anywhere it appears.");
                break;

            case ConditionType.Facing:
            {
                if (ImGui.Button("Capture my facing", new Vector2(150, 22)))
                {
                    var lp = Plugin.ObjectTable.LocalPlayer;
                    if (lp != null) c.FacingRadians = lp.Rotation;
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{Facing.ToDegrees(c.FacingRadians):0.#}°");

                float tol = c.FacingToleranceDeg;
                ImGui.SetNextItemWidth(200);
                if (ImGui.DragFloat("Tolerance (± deg)##ftol", ref tol, 1f, 1f, 180f))
                    c.FacingToleranceDeg = tol;

                ImGui.TextDisabled("A modifier — it qualifies the other conditions on this area.");
                break;
            }

            case ConditionType.GameState:
            {
                ImGui.SetNextItemWidth(220);
                if (ImGui.BeginCombo("##gs", ChallengeCondition.FlagLabel(c.Flag)))
                {
                    foreach (GameStateFlag f in Enum.GetValues<GameStateFlag>())
                        if (ImGui.Selectable(ChallengeCondition.FlagLabel(f), f == c.Flag)) c.Flag = f;
                    ImGui.EndCombo();
                }
                break;
            }

            case ConditionType.Job:
            {
                ImGui.TextUnformatted(c.JobId == 0 ? "(no job chosen)" : $"{c.JobName} (#{c.JobId})");
                if (ImGui.Button("Use my current job", new Vector2(160, 22)))
                {
                    uint id = PlayerStateReader.CurrentJobId();
                    if (id != 0) { c.JobId = id; c.JobName = PlayerStateReader.JobName(id); }
                }

                int lvl = c.MaxLevel;
                ImGui.SetNextItemWidth(160);
                if (ImGui.DragInt("Level ceiling (0 = any)##jl", ref lvl, 1f, 0, 100))
                    c.MaxLevel = Math.Clamp(lvl, 0, 100);

                DrawCondPicker(key, PlayerStateReader.AllJobs,
                               (id, n) => { c.JobId = id; c.JobName = n; });
                break;
            }

            case ConditionType.TimeOfDay:
            {
                int s = c.StartHour, e = c.EndHour;
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragInt("From (Eorzean hour)##ts", ref s, 0.2f, 0, 23)) c.StartHour = Math.Clamp(s, 0, 23);
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragInt("To (exclusive)##te", ref e, 0.2f, 0, 24)) c.EndHour = Math.Clamp(e, 0, 24);

                ImGui.TextDisabled($"Wraps past midnight. It is {PlayerStateReader.DescribeEorzeaTime()} now.");
                break;
            }

            case ConditionType.HasItem:
            {
                ImGui.TextUnformatted(c.ItemId == 0 ? "(no item chosen)" : $"{c.ItemName} (#{c.ItemId})");

                int n2 = c.ItemCount;
                ImGui.SetNextItemWidth(120);
                if (ImGui.DragInt("How many##hic", ref n2, 0.2f, 1, 999)) c.ItemCount = Math.Max(1, n2);

                if (c.ItemId != 0)
                    ImGui.TextDisabled($"You are carrying {Plugin.Inventory.Count(c.ItemId)}.");

                DrawItemSearch(key, (id, n) => { c.ItemId = id; c.ItemName = n; });
                break;
            }
        }
    }

    private void DrawGearPieces(string key, ChallengeCondition c)
    {
        c.Pieces ??= new List<GearPiece>();

        ImGui.TextDisabled("Glamour counts — this matches what the player is SEEN wearing.");

        if (ImGui.Button("＋ Add slot", new Vector2(110, 22)))
            c.Pieces.Add(new GearPiece());

        ImGui.SameLine();
        if (ImGui.Button("＋ Add everything I'm wearing", new Vector2(220, 22)))
        {
            var eq = PlayerStateReader.ReadEquipment();
            for (int i = 0; i < eq.Length; i++)
            {
                uint vis = eq[i].VisibleId;
                if (vis == 0) continue;
                c.Pieces.Add(new GearPiece
                {
                    Slot = i, ItemId = vis, ItemName = PlayerStateReader.ItemName(vis),
                });
            }
        }

        int removeAt = -1;
        for (int p = 0; p < c.Pieces.Count; p++)
        {
            ImGui.PushID(2000 + p);
            var piece = c.Pieces[p];

            int slot = Math.Clamp(piece.Slot, 0, PlayerStateReader.SlotNames.Length - 1);
            ImGui.SetNextItemWidth(140);
            if (ImGui.BeginCombo("##slot", PlayerStateReader.SlotNames[slot]))
            {
                for (int s = 0; s < PlayerStateReader.SlotNames.Length; s++)
                    if (ImGui.Selectable(PlayerStateReader.SlotNames[s], s == slot)) piece.Slot = s;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("Use what I'm wearing here", new Vector2(200, 22)))
            {
                var eq = PlayerStateReader.ReadEquipment();
                if (piece.Slot < eq.Length && eq[piece.Slot].VisibleId != 0)
                {
                    piece.ItemId   = eq[piece.Slot].VisibleId;
                    piece.ItemName = PlayerStateReader.ItemName(piece.ItemId);
                }
                else _feedback = "Nothing equipped in that slot.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Remove##piece", new Vector2(80, 22))) removeAt = p;

            ImGui.TextUnformatted(piece.ItemId == 0
                ? "   (no item chosen)"
                : $"   {piece.ItemName} (#{piece.ItemId})");

            DrawItemSearch($"{key}_p{p}", (id, n) => { piece.ItemId = id; piece.ItemName = n; });

            ImGui.PopID();
        }

        if (removeAt >= 0) c.Pieces.RemoveAt(removeAt);

        if (c.Pieces.Count > 1)
        {
            int need = c.RequiredCount;
            ImGui.SetNextItemWidth(160);
            if (ImGui.DragInt("How many must match (0 = all)##req", ref need, 0.2f, 0, c.Pieces.Count))
                c.RequiredCount = Math.Clamp(need, 0, c.Pieces.Count);

            ImGui.TextDisabled($"Requires {c.EffectiveRequiredCount} of {c.Pieces.Count}.");
        }
    }

    /// <summary>
    /// Filter + result list for a condition's picker. Same idea as
    /// <see cref="DrawFilteredPicker"/>, but keyed by string because a condition's position is not
    /// a field that can be passed by reference.
    /// </summary>
    private void DrawCondPicker(string key, Func<List<(uint Id, string Name)>> source,
                                Action<uint, string> onPick)
    {
        string filter = _condFilter.TryGetValue(key, out var f) ? f : string.Empty;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint($"##filter_{key}", "type to filter…", ref filter, 64))
            _condFilter[key] = filter;

        if (!_condCache.TryGetValue(key, out var cached)
            || !string.Equals(cached.Key, filter, StringComparison.Ordinal))
        {
            var all = source();
            List<(uint Id, string Name)> shown;

            if (string.IsNullOrWhiteSpace(filter))
            {
                shown = all;
            }
            else
            {
                shown = new List<(uint, string)>();
                foreach (var e in all)
                    if (e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) shown.Add(e);
            }

            cached = (filter, shown);
            _condCache[key] = cached;
        }

        if (ImGui.BeginChild($"##list_{key}", new Vector2(0, 130), true))
        {
            int count = 0;
            foreach (var (id, name) in cached.List)
            {
                if (count++ > 300)
                {
                    ImGui.TextDisabled($"… {cached.List.Count - 300} more, narrow the filter");
                    break;
                }
                if (ImGui.Selectable($"{name}##{key}_{id}")) onPick(id, name);
            }
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// Item picker. Separate from <see cref="DrawCondPicker"/> because the Item sheet is far too
    /// large to materialise — see <c>PlayerStateReader.SearchItems</c>. An empty query shows
    /// nothing rather than 45,000 rows.
    /// </summary>
    private void DrawItemSearch(string key, Action<uint, string> onPick)
    {
        string filter = _condFilter.TryGetValue(key, out var f) ? f : string.Empty;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint($"##isearch_{key}", "search items…", ref filter, 64))
            _condFilter[key] = filter;

        if (!_condCache.TryGetValue(key, out var cached)
            || !string.Equals(cached.Key, filter, StringComparison.Ordinal))
        {
            cached = (filter, PlayerStateReader.SearchItems(filter));
            _condCache[key] = cached;
        }

        if (cached.List.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(filter)) ImGui.TextDisabled("No matches.");
            return;
        }

        if (ImGui.BeginChild($"##ilist_{key}", new Vector2(0, 130), true))
        {
            foreach (var (id, name) in cached.List)
                if (ImGui.Selectable($"{name}##{key}_{id}")) onPick(id, name);
        }
        ImGui.EndChild();
    }

    private static string ConditionLabel(ConditionType t) => t switch
    {
        ConditionType.Presence   => "Just be here",
        ConditionType.Emote      => "Performing an emote",
        ConditionType.Mount      => "Riding a mount",
        ConditionType.Minion     => "Minion summoned",
        ConditionType.FullOutfit => "Wearing a full outfit",
        ConditionType.GearPieces => "Wearing specific gear piece(s)",
        ConditionType.Target     => "Targeting an NPC",
        ConditionType.Facing     => "Facing a direction",
        ConditionType.GameState  => "Game state (mounted, swimming…)",
        ConditionType.Job        => "Playing a job",
        ConditionType.TimeOfDay  => "Eorzean time of day",
        ConditionType.HasItem    => "Carrying an item",
        _                        => t.ToString(),
    };

    private static readonly Vector4 Good = new(0.50f, 0.84f, 0.66f, 1f);
    private static readonly Vector4 Warn = new(0.85f, 0.60f, 0.35f, 1f);

    /// <summary>Zone the draft is bound to, with an explicit re-capture rather than an implicit one.</summary>
    private void DrawZoneRow()
    {
        string shown = _territoryId == 0
            ? "(none captured)"
            : $"{(string.IsNullOrEmpty(_territoryName) ? "territory" : _territoryName)} (#{_territoryId})";

        ImGui.TextUnformatted($"Zone: {shown}");
        ImGui.SameLine();
        if (ImGui.Button("Set to my current zone", new Vector2(180, 22))) CaptureZone();

        if (_territoryId != 0 && _territoryId != (ushort)Plugin.ClientState.TerritoryType)
            ImGui.TextColored(new Vector4(0.85f, 0.60f, 0.35f, 1f),
                              "You are not in this challenge's zone — it will keep the zone shown above.");
    }

    private void CaptureZone()
    {
        try
        {
            _territoryId   = (ushort)Plugin.ClientState.TerritoryType;
            _territoryName = PlayerStateReader.ZoneName(_territoryId);
        }
        catch { }
    }

    private void AddAreaAtPlayer()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null)
        {
            _feedback = "Not logged in — cannot capture a position.";
            return;
        }

        var area = new ChallengeArea { Name = $"Area {_areas.Count + 1}" };
        area.SetCenter(lp.Position);

        // Which MAP this position is on, not just which territory. A housing district has a ward
        // map and a subdivision map with different coordinate offsets, and the sheet names only the
        // ward — so a subdivision position flagged against the sheet's map lands off the map edge.
        // Standing here is the one moment the answer is certainly right.
        area.MapId = PlayerStateReader.CurrentMapId();

        _areas.Add(area);

        // Capturing a position implies the zone, but only bind it the first time so editing an
        // existing challenge's areas cannot silently relocate it.
        if (_territoryId == 0) CaptureZone();
    }

    /// <summary>
    /// Position/size/scale controls plus a LIVE readout of whether the player is currently
    /// inside the volume. That readout is the whole point — placing a trigger precisely is
    /// guesswork otherwise.
    /// </summary>
    private void DrawAreaEditor(ChallengeArea a, Vector3? playerPos)
    {
        string name = a.Name;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name##area", ref name, 48)) a.Name = name;

        int shape = (int)a.Shape;
        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("Shape##area", ref shape, "Sphere\0Box\0")) a.Shape = (AreaShape)shape;

        // Position
        var pos = new Vector3(a.X, a.Y, a.Z);
        ImGui.SetNextItemWidth(280);
        if (ImGui.DragFloat3("Centre (X/Y/Z)##area", ref pos, 0.05f))
        {
            a.X = pos.X; a.Y = pos.Y; a.Z = pos.Z;
        }

        if (ImGui.Button("Move to me", new Vector2(100, 22)))
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp != null)
            {
                a.SetCenter(lp.Position);

                // Re-captured with the position: moving an area can carry it onto a different
                // sub-map of the same territory, and a stale map id there is the bug this field
                // exists to prevent.
                a.MapId = PlayerStateReader.CurrentMapId();
            }
        }

        // Dragging the centre by hand cannot know which sub-map it landed on, so offer the fix
        // rather than silently keeping a map id that may no longer match the coordinates.
        if (a.MapId == 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Set map from here##areamap", new Vector2(150, 22)))
                a.MapId = PlayerStateReader.CurrentMapId();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("No map recorded for this area. Map pins fall back to the zone's\n"
                               + "default map, which is wrong inside a housing subdivision.");
        }

        // Dimensions
        if (a.Shape == AreaShape.Sphere)
        {
            float r = a.Radius;
            ImGui.SetNextItemWidth(200);
            if (ImGui.DragFloat("Radius (yalms)##area", ref r, 0.05f, 0.1f, 200f)) a.Radius = r;
        }
        else
        {
            var size = new Vector3(a.SizeX, a.SizeY, a.SizeZ);
            ImGui.SetNextItemWidth(280);
            if (ImGui.DragFloat3("Size X/Y/Z##area", ref size, 0.05f, 0.1f, 400f))
            {
                a.SizeX = size.X; a.SizeY = size.Y; a.SizeZ = size.Z;
            }

            float yawDeg = Facing.ToDegrees(a.RotationY);
            ImGui.SetNextItemWidth(200);
            if (ImGui.DragFloat("Yaw (deg)##area", ref yawDeg, 0.5f, -180f, 180f))
                a.RotationY = Facing.ToRadians(yawDeg);

            ImGui.SameLine();
            if (ImGui.Button("Match my facing##area", new Vector2(150, 22)))
            {
                var lp = Plugin.ObjectTable.LocalPlayer;
                if (lp != null) a.RotationY = lp.Rotation;
            }
        }

        float scale = a.Scale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.DragFloat("Scale##area", ref scale, 0.01f, 0.05f, 20f)) a.Scale = scale;

        // A volume this small is hard to hit reliably even with swept detection, and impossible
        // to see. Worth saying at authoring time rather than leaving it to be debugged later.
        if (a.MinExtent < 1.0f)
        {
            ImGui.TextColored(new Vector4(0.85f, 0.60f, 0.35f, 1f),
                $"Small volume ({a.MinExtent:0.##}y). Players may struggle to hit it — 2-3y is a comfortable minimum.");
        }

        // Live containment feedback.
        if (playerPos.HasValue)
        {
            bool inside = a.Contains(playerPos.Value);
            float dist  = Vector3.Distance(playerPos.Value, a.Center);
            if (inside)
                ImGui.TextColored(new Vector4(0.50f, 0.84f, 0.66f, 1f), $"You are INSIDE  ({dist:0.##}y from centre)");
            else
                ImGui.TextColored(new Vector4(0.85f, 0.60f, 0.35f, 1f), $"You are outside ({dist:0.##}y from centre)");
        }
    }

    // ── Per-kind pickers ─────────────────────────────────────────────────────

    private void DrawEmoteSection()
    {
        ImGui.TextUnformatted("Emote");

        if (ImGui.Button("Use my current emote", new Vector2(180, 24)))
        {
            uint id = PlayerStateReader.CurrentEmoteId();
            if (id != 0) { _emoteId = id; _emoteName = PlayerStateReader.EmoteName(id); }
            else _feedback = "No emote is running right now.";
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(_emoteId == 0 ? "(none chosen)" : $"{_emoteName} (#{_emoteId})");

        DrawFilteredPicker("emote", ref _emoteFilter, ref _emoteCacheKey, ref _emoteCache,
                           PlayerStateReader.AllEmotes,
                           (id, name) => { _emoteId = id; _emoteName = name; });

        ImGui.Checkbox("Require a specific facing", ref _requireFacing);
        if (_requireFacing)
        {
            if (ImGui.Button("Capture my facing", new Vector2(150, 22)))
            {
                var lp = Plugin.ObjectTable.LocalPlayer;
                if (lp != null) _facingRadians = lp.Rotation;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{Facing.ToDegrees(_facingRadians):0.#}°");

            ImGui.SetNextItemWidth(200);
            ImGui.DragFloat("Tolerance (± deg)##facing", ref _facingTolerance, 1f, 1f, 180f);

            var lp2 = Plugin.ObjectTable.LocalPlayer;
            if (lp2 != null)
            {
                float delta = Facing.ToDegrees(Facing.AbsDelta(lp2.Rotation, _facingRadians));
                bool ok = delta <= _facingTolerance;
                ImGui.TextColored(ok ? new Vector4(0.50f, 0.84f, 0.66f, 1f)
                                     : new Vector4(0.85f, 0.60f, 0.35f, 1f),
                                  $"You are {delta:0.#}° off the captured facing.");
            }
        }
    }

    private void DrawMountSection()
    {
        ImGui.TextUnformatted("Mount");

        if (ImGui.Button("Use my current mount", new Vector2(180, 24)))
        {
            uint id = PlayerStateReader.CurrentMountId();
            if (id != 0) { _mountId = id; _mountName = PlayerStateReader.MountName(id); }
            else _feedback = "You are not mounted.";
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(_mountId == 0 ? "(none chosen)" : $"{_mountName} (#{_mountId})");

        DrawFilteredPicker("mount", ref _mountFilter, ref _mountCacheKey, ref _mountCache,
                           PlayerStateReader.AllMounts,
                           (id, name) => { _mountId = id; _mountName = name; });
    }

    private void DrawGearSection()
    {
        ImGui.TextUnformatted("Requirement");

        int mode = (int)_gearMode;
        ImGui.SetNextItemWidth(240);
        if (ImGui.Combo("##tc_gearmode", ref mode, "Full outfit\0One specific item\0"))
            _gearMode = (GearRequirement)mode;

        if (_gearMode == GearRequirement.FullOutfit)
        {
            ImGui.TextUnformatted(_outfitId == 0 ? "(no outfit chosen)" : $"{_outfitName} (#{_outfitId})");
            DrawFilteredPicker("outfit", ref _outfitFilter, ref _outfitCacheKey, ref _outfitCache,
                               PlayerStateReader.AllOutfits,
                               (id, name) => { _outfitId = id; _outfitName = name; });
        }
        else
        {
            ImGui.TextUnformatted(_gearItemId == 0 ? "(no item chosen)" : $"{_gearItemName} (#{_gearItemId})");
            ImGui.TextDisabled("Pick from what you are wearing now — glamour counts as what you wear.");

            var eq = PlayerStateReader.ReadEquipment();
            if (eq.Length < PlayerStateReader.EquipSlotCount)
            {
                ImGui.TextDisabled("Equipment unavailable (not logged in?).");
                return;
            }

            if (ImGui.BeginChild("##tc_equip", new Vector2(0, 190), true))
            {
                for (int i = 0; i < eq.Length; i++)
                {
                    uint visible = eq[i].VisibleId;
                    if (visible == 0) continue;

                    ImGui.PushID(i);
                    string nm = PlayerStateReader.ItemName(visible);
                    if (ImGui.Button("Use", new Vector2(48, 20)))
                    {
                        _gearItemId   = visible;
                        _gearItemName = nm;
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{PlayerStateReader.SlotNames[i]}: {nm}");
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// Filter box + scrollable result list, with the filtered result cached against the query
    /// string. Rebuilding a 1000-row filtered list every frame is the kind of thing that makes a
    /// dev tool feel broken.
    /// </summary>
    private void DrawFilteredPicker(
        string id,
        ref string filter,
        ref string cacheKey,
        ref List<(uint Id, string Name)>? cache,
        Func<List<(uint Id, string Name)>> source,
        Action<uint, string> onPick)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"##{id}_filter", "type to filter…", ref filter, 64);

        if (cache == null || !string.Equals(cacheKey, filter, StringComparison.Ordinal))
        {
            cacheKey = filter;
            var all  = source();
            if (string.IsNullOrWhiteSpace(filter))
            {
                cache = all;
            }
            else
            {
                var filtered = new List<(uint, string)>();
                foreach (var e in all)
                    if (e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(e);
                cache = filtered;
            }
        }

        if (ImGui.BeginChild($"##{id}_list", new Vector2(0, 150), true))
        {
            int shown = 0;
            foreach (var (rid, name) in cache!)
            {
                if (shown++ > 300) { ImGui.TextDisabled($"… {cache.Count - 300} more, narrow the filter"); break; }
                if (ImGui.Selectable($"{name}##{id}_{rid}")) onPick(rid, name);
            }
        }
        ImGui.EndChild();
    }

    // ── Add ──────────────────────────────────────────────────────────────────

    private void DrawAddButton()
    {
        string effectiveCategory = _creatingNewCategory ? _newCategory.Trim() : _category;

        var draft = BuildDraft(effectiveCategory);
        bool detailsOk = !string.IsNullOrWhiteSpace(_title) && !string.IsNullOrWhiteSpace(_detail);
        bool shapeOk   = draft.IsWellFormed();
        bool canAdd    = detailsOk && shapeOk && !string.IsNullOrWhiteSpace(effectiveCategory);

        if (!shapeOk) MissingLabel(WhyNotWellFormed(draft));

        bool editing = _editingId != null;

        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button(editing ? "Save changes" : "Add Challenge", new Vector2(170, 30)))
        {
            if (editing)
            {
                // Update in place. The Id is preserved deliberately — completion state is keyed
                // by it, so a new Id would wipe the player's progress on this challenge.
                int idx = _config.CustomChallenges.FindIndex(
                    c => string.Equals(c.Id, _editingId, StringComparison.Ordinal));

                if (idx >= 0)
                {
                    // Id AND sort number both carry over — identity and ordering are independent,
                    // and neither should change just because the wording did.
                    draft.Id        = _editingId!;
                    draft.SortOrder = _config.CustomChallenges[idx].SortOrder;

                    // Recomputed from the challenge's CONTENT, never from "today's build". This
                    // used to stamp the current version on every save, so renaming a published
                    // challenge withheld it from everyone still on an older plugin.
                    draft.MinPluginVersion = ChallengeCatalog.RequiredFor(draft).ToString();
                    _config.CustomChallenges[idx] = draft;
                    _feedback = $"Saved changes to \"{draft.Title}\".";
                }
                else
                {
                    _feedback = "That challenge no longer exists — nothing saved.";
                }

                _editingId = null;
            }
            else
            {
                // Fresh permanent GUID. Sort number is separate and freely editable later.
                draft.Id        = ChallengeCatalog.NewId();
                draft.SortOrder = ChallengeCatalog.NextSortOrder(_config);
                _config.CustomChallenges.Add(draft);
                _feedback = $"Added \"{draft.Title}\" to {draft.Category}.";
            }

            _config.SelectedCategory = draft.Category;
            _config.DefinitionsChanged();
            _save();
            _tracker.Invalidate();

            ResetDraft();
        }
        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        
        if (ImGui.Button("Preview popup", new Vector2(130, 30)))
        {
            // Preview the popup for THIS draft. Unfilled fields fall back to red placeholders
            // rather than showing invented sample content.
            int nextNumber = ChallengeCatalog.Combined(_config).Count + 1;
            _toastQueue.ShowPreview(draft, nextNumber);
        }
        

        if (!string.IsNullOrEmpty(_feedback))
            ImGui.TextColored(new Vector4(0.50f, 0.84f, 0.66f, 1f), _feedback);
    }

    private CustomChallenge BuildDraft(string category)
    {
        var draft = new CustomChallenge
        {
            // What this challenge's CONTENT requires — not the build that authored it. Older
            // plugins refuse only a kind they cannot evaluate; see ChallengeCatalog.RequiredFor.
            // Placeholder — recomputed from the finished draft at the bottom of this method, once
            // the chain steps are on it. RequiredFor needs the CONTENT, not just the kind: a chain
            // carries Kind = InArea and would otherwise be stamped as loadable by builds that
            // cannot evaluate it.
            MinPluginVersion = ChallengeCatalog.RequiredFor(_kind).ToString(),
            Category      = category,
            Title         = _title.Trim(),
            Detail        = _detail.Trim(),
            Hint          = _hint.Trim(),
            Difficulty    = _difficulty,
            Kind          = _kind,
            TerritoryId   = _territoryId,
            TerritoryName = _territoryName,
            EmoteId       = _emoteId,
            EmoteName     = _emoteName,
            RequireFacing = _requireFacing,
            FacingRadians = _facingRadians,
            FacingToleranceDeg = _facingTolerance,
            MountId       = _mountId,
            MountName     = _mountName,
            GearMode      = _gearMode,
            OutfitSetId   = _outfitId,
            OutfitName    = _outfitName,
            GearItemId    = _gearItemId,
            GearItemName  = _gearItemName,
            WholeZone     = _wholeZone,
            ShowProgress  = _showProgress,
            AllowMapPin   = _allowMapPin,
            Mode          = _mode,
            SessionOnly   = _sessionOnly,
        };

        // The chain, if any. Committed first so the step currently loaded into the shared draft
        // fields is written back rather than discarded.
        if (_chainSteps.Count > 0)
        {
            CommitStepDraft();
            foreach (var s in _chainSteps) draft.ChainSteps.Add(s.Clone());
        }

        // Not for a chain. `_areas` is the SHARED editor buffer and currently holds whichever step
        // happens to be loaded, so copying it here shipped a stray duplicate of that one step's
        // volume at the top level — exactly the nonsense the Requirements guard below was written to
        // prevent, in the one field it did not cover. Harmless to the tracker, which dispatches a
        // chain ahead of Kind, but it is meaningless data in a published file and it made the
        // challenge look as though it had content of its own.
        if (_chainSteps.Count == 0)
            foreach (var a in _areas) draft.Areas.Add(a.Clone());

        // Composite stops are assembled from the parallel draft lists. Deep-copied for the same
        // reason LoadDraft deep-copies areas: the draft must stay independent of what is stored,
        // or every keystroke would edit the saved challenge live with no way to cancel.
        // A chain hangs its content on its STEPS. The shared area fields currently hold whichever
        // step is loaded, and copying them into the challenge's own Requirements as well would
        // duplicate one step's areas at the top level — harmless to the tracker, which dispatches
        // chains ahead of Kind, but it would ship nonsense in the published file.
        if (_kind == ChallengeKind.InArea && _chainSteps.Count == 0)
        {
            SyncConditionSlots();

            for (int i = 0; i < _areas.Count; i++)
            {
                var req = new AreaRequirement
                {
                    Area          = _areas[i].Clone(),
                    Label         = _areas[i].Name,
                    WithinSeconds = _mode == AreaMode.InOrder ? _within[i] : 0,
                };

                foreach (var c in _conditions[i]) req.Conditions.Add(c.Clone());
                draft.Requirements.Add(req);

                // Single means exactly one stop; anything further would be saved and never
                // evaluated, so stop rather than write a challenge that lies about itself.
                if (_mode == AreaMode.Single) break;
            }
        }

        if (_kind == ChallengeKind.RaceTimer)
        {
            EnsureRaceSlots();

            // Cloned out of the fixed draft slots into their named roles. Deep copies for the same
            // reason as everywhere else here: the draft must stay independent of what is stored.
            draft.RaceStart       = _areas[RaceStartIdx].Clone();
            draft.RaceFinish      = _areas[RaceFinishIdx].Clone();
            draft.RaceUseQuitArea = _raceUseQuit;
            draft.RaceQuit        = _raceUseQuit ? _areas[RaceQuitIdx].Clone() : null;
            draft.RaceFailSeconds = _raceFailSeconds;

            // The role properties are the truth for a race; Areas would otherwise ship three
            // duplicate volumes with no meaning attached to their order.
            draft.Areas.Clear();
        }

        // Last, once every area is on the draft: settle the zone against where the areas were
        // actually captured. A chain step inherits the challenge's zone when it is created, so a
        // step placed in a different zone would otherwise ship with a territory it was never in —
        // and a challenge whose territory disagrees with its own coordinates can never fire.
        ChallengeCatalog.RebindZonesToAreas(draft);

        // Now that the draft is fully populated. See the placeholder note above.
        draft.MinPluginVersion = ChallengeCatalog.RequiredFor(draft).ToString();

        return draft;
    }

    private static string WhyNotWellFormed(CustomChallenge d) => d.Kind switch
    {
        ChallengeKind.VisitAreas        => "Add at least one area.",
        ChallengeKind.VisitAreasInOrder => "Add at least one area.",
        ChallengeKind.EmoteAtArea       => d.EmoteId == 0 ? "Choose an emote." : "Add the area.",
        ChallengeKind.MountInArea       => d.MountId == 0 ? "Choose a mount."  : "Add the area.",
        ChallengeKind.GearInArea        => d.TerritoryId == 0 ? "Log in so the zone can be captured."
                                         : d.GearMode == GearRequirement.FullOutfit && d.OutfitSetId == 0 ? "Choose an outfit."
                                         : d.GearMode == GearRequirement.SingleItem && d.GearItemId == 0 ? "Choose an item."
                                         : "Add an area, or tick Whole zone.",
        ChallengeKind.InArea            => WhyCompositeIncomplete(d),
        ChallengeKind.RaceTimer         => d.TerritoryId == 0 ? "Log in so the zone can be captured."
                                         : d.RaceStart  == null ? "Place the start line."
                                         : d.RaceFinish == null ? "Place the finish line."
                                         : "Place the bounding area, or turn it off.",
        _                               => "Incomplete.",
    };

    private static string WhyCompositeIncomplete(CustomChallenge d)
    {
        if (d.TerritoryId == 0) return "Log in so the zone can be captured.";

        if (d.IsChain)
        {
            for (int i = 0; i < d.ChainSteps.Count; i++)
            {
                var s = d.ChainSteps[i];
                if (s.IsWellFormed()) continue;

                if (s.Requirements.Count == 0)
                    return $"Step {i + 1} has no areas — select it and add one.";
                if (s.Mode == AreaMode.Single && s.Requirements.Count != 1)
                    return $"Step {i + 1} is Single mode but has {s.Requirements.Count} areas.";

                foreach (var r in s.Requirements)
                    foreach (var c in r.Conditions)
                        if (!c.IsWellFormed())
                            return $"Step {i + 1}: \"{ConditionLabel(c.Type)}\" still needs a value.";

                return $"Step {i + 1}: facing on its own cannot complete a step.";
            }
            return "Incomplete.";
        }

        if (d.Requirements.Count == 0)   return "Add at least one area.";

        if (d.Mode == AreaMode.Single && d.Requirements.Count != 1)
            return "Single mode allows exactly one area — delete the extras or switch mode.";

        for (int i = 0; i < d.Requirements.Count; i++)
        {
            var r = d.Requirements[i];
            if (r.IsWellFormed()) continue;

            foreach (var c in r.Conditions)
                if (!c.IsWellFormed())
                    return $"Area {i + 1}: \"{ConditionLabel(c.Type)}\" still needs a value.";

            return $"Area {i + 1}: facing on its own cannot complete a challenge.";
        }

        return "Incomplete.";
    }

    /// <summary>Load an existing challenge into the draft for in-place editing.</summary>
    private void LoadDraft(CustomChallenge c)
    {
        _editingId = c.Id;

        _kind     = c.Kind;
        _category = c.Category;
        _creatingNewCategory = false;
        _newCategory = string.Empty;

        _title  = c.Title  ?? string.Empty;
        _detail = c.Detail ?? string.Empty;
        _hint   = c.Hint   ?? string.Empty;
        _difficulty = Math.Clamp(c.Difficulty, 0, 5);

        _territoryId   = c.TerritoryId;
        _territoryName = c.TerritoryName ?? string.Empty;

        // Deep-copy the areas so editing is cancellable — mutating the stored objects directly
        // would apply every drag of a slider immediately, with no way back.
        _areas.Clear();
        _conditions.Clear();
        _within.Clear();
        _chainSteps.Clear();
        _editingStep = -1;

        _mode        = c.Mode;
        _sessionOnly = c.SessionOnly;

        if (c.IsChain)
        {
            foreach (var s in c.ChainSteps) _chainSteps.Add(s.Clone());

            // Open the first step so the shared area editor below is pointing at something rather
            // than at nothing, which reads as "my areas vanished".
            if (_chainSteps.Count > 0) LoadStepDraft(0);
        }
        else if (c.Kind == ChallengeKind.InArea)
        {
            // Composite challenges keep their areas in Requirements, so unpack them back into the
            // parallel draft lists the editor works on. Same deep-copy rule for the conditions.
            foreach (var r in c.Requirements ?? new List<AreaRequirement>())
            {
                var area = r.Area?.Clone() ?? new ChallengeArea();
                if (!string.IsNullOrWhiteSpace(r.Label)) area.Name = r.Label;
                _areas.Add(area);

                var conds = new List<ChallengeCondition>();
                foreach (var cd in r.Conditions ?? new List<ChallengeCondition>()) conds.Add(cd.Clone());
                _conditions.Add(conds);

                _within.Add(r.WithinSeconds);
            }
        }
        else if (c.Kind == ChallengeKind.RaceTimer)
        {
            // Unpack the named roles back into the fixed draft slots the editor works on.
            _areas.Add(c.RaceStart?.Clone()  ?? new ChallengeArea { Name = RaceSlotNames[0] });
            _areas.Add(c.RaceFinish?.Clone() ?? new ChallengeArea { Name = RaceSlotNames[1] });
            _areas.Add(c.RaceQuit?.Clone()   ?? new ChallengeArea { Name = RaceSlotNames[2] });

            _raceUseQuit     = c.RaceUseQuitArea;
            _raceFailSeconds = c.RaceFailSeconds;
        }
        else
        {
            foreach (var a in c.Areas) _areas.Add(a.Clone());
        }

        SyncConditionSlots();

        _emoteId         = c.EmoteId;
        _emoteName       = c.EmoteName ?? string.Empty;
        _requireFacing   = c.RequireFacing;
        _facingRadians   = c.FacingRadians;
        _facingTolerance = c.FacingToleranceDeg;

        _mountId   = c.MountId;
        _mountName = c.MountName ?? string.Empty;

        _gearMode     = c.GearMode;
        _outfitId     = c.OutfitSetId;
        _outfitName   = c.OutfitName ?? string.Empty;
        _gearItemId   = c.GearItemId;
        _gearItemName = c.GearItemName ?? string.Empty;
        _wholeZone    = c.WholeZone;
        _showProgress = c.ShowProgress;
        _allowMapPin  = c.AllowMapPin;

        _feedback       = $"Editing \"{(string.IsNullOrWhiteSpace(c.Title) ? "(unnamed)" : c.Title)}\".";
        _switchToCreate = true;
    }

    private void ResetDraft()
    {
        _title  = string.Empty;
        _detail = string.Empty;
        _hint   = string.Empty;
        _difficulty = 0;
        _areas.Clear();
        _conditions.Clear();
        _within.Clear();
        _condFilter.Clear();
        _condCache.Clear();
        _mode = AreaMode.Single;
        _raceFailSeconds = 0;
        _raceUseQuit     = false;
        _chainSteps.Clear();
        _editingStep = -1;
        _sessionOnly = false;
        _emoteId = 0; _emoteName = string.Empty;
        _mountId = 0; _mountName = string.Empty;
        _outfitId = 0; _outfitName = string.Empty;
        _gearItemId = 0; _gearItemName = string.Empty;
        _requireFacing = false;
        _wholeZone = false;
        _showProgress = true;
        _allowMapPin  = false;

        // A fresh draft binds to wherever you are standing; an edit brought its own zone.
        CaptureZone();

        if (_creatingNewCategory)
        {
            _category = _newCategory.Trim();
            _newCategory = string.Empty;
            _creatingNewCategory = false;
        }
    }

    // ── Existing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The authored catalogue, grouped by category and collapsed by default.
    /// </summary>
    /// <remarks>
    /// <para>This list used to print every field of every challenge unconditionally — roughly eight
    /// lines each — so at twenty challenges it was several screens of scrolling with no way to see
    /// the shape of the catalogue. Now a challenge is one line until it is opened, and categories
    /// collapse too, so the default view is a table of contents.</para>
    ///
    /// <para>Open/closed state belongs to ImGui, keyed by node ID. That is deliberate: it survives
    /// the per-frame rebuild for free, and it means an open challenge stays open across a save,
    /// an edit, or a switch to another tab and back.</para>
    /// </remarks>
    private void DrawExistingTab()
    {
        DrawCategoryManager();
        DrawUnadoptedOfficial();

        if (_config.CustomChallenges.Count == 0)
        {
            ImGui.TextDisabled("None yet. Everything in the list is built-in.");
            return;
        }

        DrawExistingSearchBar();

        // Real indices are carried alongside, because deletion has to index the config list and
        // the grouped view no longer walks it in order.
        var groups   = GroupExisting(out int matched);
        int removeAt = -1;

        if (!string.IsNullOrWhiteSpace(_existingSearch))
        {
            ImGui.TextDisabled($"{matched} of {_config.CustomChallenges.Count} shown");
            ImGui.Spacing();
        }

        if (groups.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Nothing matches \"{_existingSearch}\".");
            return;
        }

        foreach (var group in groups)
        {
            // A search should show its results, not make you open boxes to find them. With no
            // search this passes 0 and ImGui keeps whatever the user last set.
            if (_existingSetOpen != 0)                 ImGui.SetNextItemOpen(_existingSetOpen > 0);
            else if (!string.IsNullOrEmpty(_existingSearch)) ImGui.SetNextItemOpen(true);

            int flagged = 0;
            foreach (var (c, _) in group.Items)
                if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Detail)) flagged++;

            // The count is on the header so the shape of the catalogue is readable while closed.
            string header = $"{group.Name}  ({group.Items.Count})";
            if (flagged > 0) header += $"  ·  {flagged} incomplete";

            // ### keeps the ImGui ID stable while the visible label changes with the counts —
            // without it, every header would forget its open state as soon as a count moved.
            if (!ImGui.CollapsingHeader($"{header}###tc_exgrp_{group.Name}")) continue;

            ImGui.Indent(10f);
            foreach (var (c, index) in group.Items)
                if (DrawExistingRow(c, index)) removeAt = index;
            ImGui.Unindent(10f);
        }

        _existingSetOpen = 0;   // one-frame instruction, consumed

        if (removeAt >= 0)
        {
            var gone = _config.CustomChallenges[removeAt];
            _config.CustomChallenges.RemoveAt(removeAt);
            // Deliberately NOT removed from the completion stores. The permanent ledger is
            // append-only by design, and if this challenge is ever re-added under the same GUID
            // its original completion date is still there.
            _config.DefinitionsChanged();
            _save();
            _tracker.Invalidate();
            _feedback = $"Deleted \"{gone.Title}\".";
        }
    }

    private void DrawExistingSearchBar()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##tc_existing_search", "Search name, category, zone, hint, GUID…",
                                ref _existingSearch, 128);

        ImGui.SameLine();
        if (ImGui.Button("Clear##tc_existing_clear")) _existingSearch = string.Empty;

        ImGui.SameLine();
        if (ImGui.Button("Expand all"))   _existingSetOpen =  1;
        ImGui.SameLine();
        if (ImGui.Button("Collapse all")) _existingSetOpen = -1;

        ImGui.Spacing();
    }

    /// <summary>
    /// One collapsed challenge. Returns true if its Delete was pressed this frame.
    /// </summary>
    /// <remarks>
    /// The closed line carries only what is needed to FIND a challenge — its number, its name, and
    /// a mark if something is wrong with it. Everything that was previously always on screen moved
    /// inside. A defect flag stays visible while closed on purpose: a broken challenge you have to
    /// open to notice is one you will not notice.
    /// </remarks>
    private bool DrawExistingRow(CustomChallenge c, int index)
    {
        bool missingDetails = string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Detail);
        var  publish        = PublishState(c);

        string title = string.IsNullOrWhiteSpace(c.Title) ? "(unnamed)" : c.Title;
        string label = $"#{c.SortOrder}  {title}";

        // Same meter the main window draws, and absent for the same reason when unrated — an
        // all-hollow row would read as "rated zero" rather than "not rated".
        string meter = ChallengeDef.DifficultyMeterFor(c.Difficulty);
        if (meter.Length > 0) label += $"   {meter}";

        if (missingDetails)                     label += "   [!]";
        else if (publish == PublishStatus.Edited) label += "   *";

        if (_existingSetOpen != 0) ImGui.SetNextItemOpen(_existingSetOpen > 0);

        // Keyed by GUID, not by list position: an index-keyed node would hand its open state to a
        // different challenge the moment one above it was deleted or the sort order changed.
        bool open = ImGui.TreeNodeEx($"{label}###tc_ex_{c.Id}",
                                     ImGuiTreeNodeFlags.SpanAvailWidth);

        if (missingDetails && ImGui.IsItemHovered())
            ImGui.SetTooltip("Missing a name or a description — this cannot be published.");

        if (!open) return false;

        bool delete = false;
        ImGui.PushID(c.Id);

        if (missingDetails) MissingLabel("Missing details");

        ImGui.TextDisabled($"{KindLabel(c.Kind)} · {c.TerritoryName} · "
                         + $"#{c.SortOrder} · {c.StopCount} area(s) · "
                         + $"{(_store.IsComplete(c.Id) ? "COMPLETE" : "incomplete")}");

        // Composite challenges carry their meaning in the conditions, not in the kind label, so
        // spell the stops out — otherwise the list says "In area" for every one of them and there
        // is no way to tell two apart without opening the editor.
        if (c.Kind == ChallengeKind.InArea && c.Requirements is { Count: > 0 })
        {
            string modeLabel = c.Mode switch
            {
                AreaMode.AnyOrder => "any order",
                AreaMode.InOrder  => "in order",
                _                 => "single",
            };
            ImGui.TextDisabled($"mode: {modeLabel}{(c.SessionOnly ? " · one session only" : string.Empty)}");

            for (int i = 0; i < c.Requirements.Count; i++)
            {
                var r = c.Requirements[i];
                string timing = c.Mode == AreaMode.InOrder && r.WithinSeconds > 0
                    ? $"  (within {r.WithinSeconds}s)"
                    : string.Empty;
                ImGui.TextDisabled($"   {i + 1}. {r.Describe()}{timing}");
            }
        }

        if (c.IsChain)
        {
            ImGui.TextDisabled($"QUEST · {c.ChainSteps.Count} step(s)");
            for (int i = 0; i < c.ChainSteps.Count; i++)
            {
                var s = c.ChainSteps[i];
                string zone = s.TerritoryId == 0 ? c.TerritoryName : s.TerritoryName;
                string flag = s.IsWellFormed() ? string.Empty : "  [incomplete]";
                ImGui.TextDisabled(
                    $"   {i + 1}. {(string.IsNullOrWhiteSpace(s.Title) ? "(unnamed)" : s.Title)} "
                  + $"· {zone} · {s.Requirements.Count} area(s){flag}");
            }
        }

        if (c.Kind == ChallengeKind.RaceTimer)
        {
            string limit = c.RaceFailSeconds > 0
                ? CompletionStore.FormatRaceTime(c.RaceFailSeconds)
                : "untimed";
            string bounded = c.RaceUseQuitArea ? "bounded" : "unbounded";
            ImGui.TextDisabled($"limit: {limit} · {bounded}");

            double? best = _store.BestRaceTime(c.Id);
            if (best.HasValue)
                ImGui.TextDisabled($"your best: {CompletionStore.FormatRaceTime(best.Value)}");
        }

        // Spelled out here rather than left to the pips alone: difficulty is optional, so
        // "not set" is a real authoring state worth naming while editing.
        if (meter.Length > 0)
            ImGui.TextDisabled($"difficulty {meter}  ({c.Difficulty}/5)");
        else
            ImGui.TextDisabled("difficulty not set");

        if (c.AllowMapPin) ImGui.TextDisabled("map pin: allowed");

        ImGui.TextDisabled($"guid {c.Id}");

        // The main window shows the PUBLISHED copy of a synced challenge — official wins on
        // a GUID collision — so a local edit looks like it did nothing until it is published
        // and re-synced. Say so here, where the edit was made.
        switch (publish)
        {
            case PublishStatus.Edited:
                ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f),
                    "edited locally — publish to replace the live copy");
                break;
            case PublishStatus.Matches:
                ImGui.TextDisabled("published, and identical to the live copy");
                break;
            case PublishStatus.Unpublished:
                ImGui.TextDisabled("not published yet");
                break;
        }

        if (!string.IsNullOrWhiteSpace(c.Detail)) ImGui.TextDisabled(c.Detail);

        // Not flagged red when absent — a missing hint is a choice, not a defect.
        if (!string.IsNullOrWhiteSpace(c.Hint))
            ImGui.TextColored(new Vector4(0.66f, 0.79f, 0.94f, 1f), $"Hint: {c.Hint}");
        else
            ImGui.TextDisabled("no hint");

        if (ImGui.Button("Edit", new Vector2(80, 22))) LoadDraft(c);
        ImGui.SameLine();
        if (ImGui.Button("Delete", new Vector2(80, 22))) delete = true;

        ImGui.PopID();
        ImGui.TreePop();
        ImGui.Spacing();
        return delete;
    }

    private sealed class ExistingGroup
    {
        public string Name = string.Empty;
        public readonly List<(CustomChallenge Challenge, int Index)> Items = new();
    }

    /// <summary>
    /// Bucket the authored challenges by category, in the catalogue's own category order, keeping
    /// each one's index into <see cref="Configuration.CustomChallenges"/> so Delete still works.
    /// </summary>
    /// <remarks>
    /// Categories are ordered by <see cref="ChallengeCatalog.Categories"/> rather than
    /// alphabetically, so this list reads in the same order as the main window's master pane.
    /// Anything whose category is blank, or names a category that no longer exists, still gets a
    /// group — losing a challenge from this list because its category was deleted would make it
    /// uneditable and invisible at the same time.
    /// </remarks>
    private List<ExistingGroup> GroupExisting(out int matched)
    {
        var order = ChallengeCatalog.Categories(_config, includeEmpty: true);
        var byName = new Dictionary<string, ExistingGroup>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExistingGroup>();

        ExistingGroup For(string name)
        {
            if (byName.TryGetValue(name, out var g)) return g;
            g = new ExistingGroup { Name = name };
            byName[name] = g;
            result.Add(g);
            return g;
        }

        // Seed in catalogue order first so the groups appear in that order regardless of the
        // order challenges happen to sit in the config list.
        foreach (var name in order) For(name);

        matched = 0;
        for (int i = 0; i < _config.CustomChallenges.Count; i++)
        {
            var c = _config.CustomChallenges[i];
            if (!MatchesExistingSearch(c)) continue;

            matched++;
            string cat = string.IsNullOrWhiteSpace(c.Category) ? UncategorisedLabel : c.Category;
            For(cat).Items.Add((c, i));
        }

        // Sorted the way the main window sorts, so "#3" here is "#3" there.
        foreach (var g in result)
            g.Items.Sort(static (a, b) =>
            {
                int bySort = a.Challenge.SortOrder.CompareTo(b.Challenge.SortOrder);
                return bySort != 0
                    ? bySort
                    : string.Compare(a.Challenge.Title, b.Challenge.Title, StringComparison.CurrentCultureIgnoreCase);
            });

        result.RemoveAll(g => g.Items.Count == 0);
        return result;
    }

    /// <summary>
    /// Case-insensitive substring match across every field that could plausibly identify a
    /// challenge, GUID included — pasting one from the main window's Copy GUID lands here.
    /// </summary>
    private bool MatchesExistingSearch(CustomChallenge c)
    {
        string q = _existingSearch.Trim();
        if (q.Length == 0) return true;

        return Contains(c.Title) || Contains(c.Category) || Contains(c.TerritoryName)
            || Contains(c.Detail) || Contains(c.Hint) || Contains(c.Id)
            || Contains(KindLabel(c.Kind));

        bool Contains(string? field) =>
            !string.IsNullOrEmpty(field) &&
            field.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private enum PublishStatus
    {
        /// <summary>No challenge with this GUID exists in the synced catalogue.</summary>
        Unpublished,

        /// <summary>Published, and the local copy serialises identically to the live one.</summary>
        Matches,

        /// <summary>Published, but the local copy has since been changed.</summary>
        Edited,
    }

    /// <summary>
    /// Compare a local challenge with its published counterpart.
    ///
    /// <para><b>Matched by GUID and nothing else.</b> Every other field — name, description, hint,
    /// category, zone, kind, areas — is content, freely replaceable. Changing all of them at once
    /// still edits the same challenge, because identity was never carried by any of them. That is
    /// the entire reason completion is keyed by GUID.</para>
    /// </summary>
    private static PublishStatus PublishState(CustomChallenge local)
    {
        var official = ChallengeCatalog.Official;
        if (official == null || string.IsNullOrWhiteSpace(local.Id)) return PublishStatus.Unpublished;

        foreach (var live in official.Challenges)
        {
            if (!string.Equals(live.Id, local.Id, StringComparison.OrdinalIgnoreCase)) continue;

            // Serialised comparison rather than a field-by-field one: a new field added later
            // would silently fall out of a hand-written comparison and stop reporting edits.
            string a = Newtonsoft.Json.JsonConvert.SerializeObject(local);
            string b = Newtonsoft.Json.JsonConvert.SerializeObject(live);
            return string.Equals(a, b, StringComparison.Ordinal)
                ? PublishStatus.Matches
                : PublishStatus.Edited;
        }

        return PublishStatus.Unpublished;
    }

    /// <summary>
    /// Published challenges with no local copy — and the way to get one back.
    ///
    /// <para><b>Why this is needed.</b> Editing works off <c>Configuration.CustomChallenges</c>,
    /// while synced challenges live in their own catalogue and are never merged into it. Normally
    /// both exist, because you authored the thing before publishing it. But on a machine that
    /// never authored it, or after the local config is lost, a published challenge becomes
    /// uneditable — and, worse, publishing from that machine would drop it from the master list
    /// entirely. Adopting copies it back into the local list under the SAME GUID, which is what
    /// makes the round trip work at all.</para>
    /// </summary>
    private void DrawUnadoptedOfficial()
    {
        var official = ChallengeCatalog.Official;
        if (official == null || official.Count == 0) return;

        var missing = new List<CustomChallenge>();
        foreach (var o in official.Challenges)
        {
            if (string.IsNullOrWhiteSpace(o.Id)) continue;
            if (_config.CustomChallenges.Exists(c => string.Equals(c.Id, o.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            missing.Add(o);
        }

        if (missing.Count == 0) return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.90f, 0.42f, 0.38f, 1f));
        bool open = ImGui.CollapsingHeader($"Published but not on this machine ({missing.Count})##tc_adopt");
        ImGui.PopStyleColor();
        if (!open) return;

        ImGui.TextWrapped(
            "These are live in the public catalogue, but this install has no editable copy. "
          + "Publishing from here would REMOVE them for every user — the removal guard on the "
          + "Publish tab will stop that. Adopt one to edit it; it keeps its GUID, so everyone's "
          + "completion survives.");
        ImGui.Spacing();

        int adoptIdx = -1;
        for (int i = 0; i < missing.Count; i++)
        {
            var o = missing[i];
            ImGui.PushID($"tc_adopt_{i}");

            if (ImGui.Button("Adopt", new Vector2(70, 20))) adoptIdx = i;
            ImGui.SameLine();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(o.Title) ? "(unnamed)" : o.Title);
            ImGui.SameLine();
            ImGui.TextDisabled($"· {o.Category} · guid {o.Id}");

            ImGui.PopID();
        }

        if (adoptIdx >= 0)
        {
            // Deep copy. Binding the catalogue's own object would let an edit mutate the synced
            // copy in memory, so the UI would show changes that were never published.
            var src = missing[adoptIdx];
            var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<CustomChallenge>(
                Newtonsoft.Json.JsonConvert.SerializeObject(src));

            if (copy != null)
            {
                copy.Id = src.Id;              // identity is never regenerated — that is the point
                _config.CustomChallenges.Add(copy);
                _config.DefinitionsChanged();
                _save();
                _feedback = $"Adopted \"{copy.Title}\" for editing. Its GUID is unchanged.";
            }
        }

        ImGui.Separator();
    }

    /// <summary>
    /// Create, order and delete categories as things in their own right.
    ///
    /// <para>Order here is the order players see, which is why moving a row is a first-class
    /// action rather than something implied by challenge sort numbers.</para>
    /// </summary>
    private void DrawCategoryManager()
    {
        if (!ImGui.CollapsingHeader("Categories"))
            return;

        ImGui.TextDisabled(
            "Categories publish with the catalogue, so adding one needs a Publish — not a new "
          + "plugin release. Users pick them up on their next Sync.");
        ImGui.Spacing();

        var all = ChallengeCatalog.Categories(_config, includeEmpty: true);
        if (all.Count == 0)
        {
            ImGui.TextDisabled("None yet. Create one from the Category box on the Create tab.");
            ImGui.Separator();
            return;
        }

        int move = 0, moveIdx = -1, removeIdx = -1;

        for (int i = 0; i < all.Count; i++)
        {
            string name = all[i];
            ImGui.PushID($"tc_cat_mgr_{i}");

            // Only locally created categories can be reordered or deleted here. A published one
            // is owned by the repo — the same rule that stops a local challenge redefining an
            // official one. Editing it locally would silently diverge from what users have.
            int  localIdx = _config.CustomCategories.FindIndex(c => string.Equals(c, name, StringComparison.Ordinal));
            bool isLocal  = localIdx >= 0;
            int  count    = ChallengeCatalog.InCategory(_config, name).Count;

            if (!isLocal) ImGui.BeginDisabled();
            if (ImGui.Button("^", new Vector2(24, 20))) { move = -1; moveIdx = localIdx; }
            ImGui.SameLine();
            if (ImGui.Button("v", new Vector2(24, 20))) { move = +1; moveIdx = localIdx; }
            if (!isLocal) ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.TextUnformatted(name);

            ImGui.SameLine();
            ImGui.TextDisabled(count == 1 ? "· 1 challenge" : $"· {count} challenges");

            if (!isLocal)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f), "· published");
            }

            // Deleting a category that still holds challenges would orphan them into the
            // fall-back path, where they reappear under the same name anyway. Refuse instead of
            // doing something that looks destructive but isn't.
            if (isLocal)
            {
                ImGui.SameLine();
                if (count > 0) ImGui.BeginDisabled();
                if (ImGui.Button("Delete", new Vector2(64, 20))) removeIdx = localIdx;
                if (count > 0) ImGui.EndDisabled();

                if (count > 0 && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Move or delete its challenges first.");
            }

            ImGui.PopID();
        }

        if (moveIdx >= 0)
        {
            int target = moveIdx + move;
            if (target >= 0 && target < _config.CustomCategories.Count)
            {
                (_config.CustomCategories[moveIdx], _config.CustomCategories[target]) =
                    (_config.CustomCategories[target], _config.CustomCategories[moveIdx]);
                _config.DefinitionsChanged();
                _save();
            }
        }

        if (removeIdx >= 0)
        {
            string gone = _config.CustomCategories[removeIdx];
            _config.CustomCategories.RemoveAt(removeIdx);
            _config.DefinitionsChanged();
            _save();
            _feedback = $"Deleted category \"{gone}\".";
        }

        ImGui.Separator();
    }

    // ── Publish ──────────────────────────────────────────────────────────────

    private string _exportPath = ChallengeExporter.DefaultRepoPath;

    /// <summary>
    /// Deliberately NOT persisted, and reset every time the Creator opens. Removing published
    /// content should be an explicit decision each time, never a setting left on from last week.
    /// </summary>
    private bool _allowRemovals;
    private volatile string _exportStatus = string.Empty;
    private volatile bool   _exportOk;
    private volatile string _publishLog = string.Empty;

    /// <summary>
    /// Writes the authored challenges out as the files the public repo serves. Committing and
    /// pushing them is what publishes them; every user's next sync then picks them up.
    /// </summary>
    private void DrawPublishTab()
    {
        ImGui.TextWrapped(
            "Export your challenges as the JSON files the public repo serves: one file per "
          + "challenge plus a master.json index. Commit and push them, and every user's next "
          + "Sync downloads them.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f),
            "GUIDs are carried through unchanged — that is what makes a challenge mean the same "
          + "thing on every install.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Public repo checkout");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##tc_exportpath", ref _exportPath, 512);

        ImGui.Spacing();

        int publishable = 0, incomplete = 0;
        foreach (var c in _config.CustomChallenges)
        {
            if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Detail) || !c.IsWellFormed())
                incomplete++;
            else
                publishable++;
        }

        ImGui.TextUnformatted($"{publishable} ready to publish");
        if (incomplete > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.90f, 0.42f, 0.38f, 1f),
                $"· {incomplete} skipped (missing name/description, or not well-formed)");
        }

        // The catalogue's master list is regenerated from the outgoing set alone, so anything
        // already published but absent here disappears for everyone. That is a legitimate way to
        // retire a challenge and a disastrous accident otherwise, so it takes a deliberate tick.
        ImGui.Checkbox("Allow removing already-published challenges##tc_allowrm", ref _allowRemovals);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Off: publishing stops and names anything that would be removed.\n"
              + "On: challenges missing from this export are deleted from the live catalogue.\n\n"
              + "Editing a published challenge does NOT need this — it is matched by GUID and\n"
              + "replaced in place.");

        ImGui.Spacing();

        // One button for the whole thing: clone if needed, export, commit, push.
        bool busy = GitPublisher.IsRunning;
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button(busy ? "Publishing…" : "Publish to GitHub", new Vector2(200, 30)))
        {
            _exportStatus = "Publishing…";
            _exportOk     = true;
            _publishLog   = string.Empty;

            string path = _exportPath.Trim();
            var snapshot = new List<CustomChallenge>(_config.CustomChallenges);

            // Snapshotted on the UI thread with the challenges — the publish runs off-thread, and
            // reading config collections from there would race with any edit made meanwhile.
            var categorySnapshot = ChallengeCatalog.Categories(_config, includeEmpty: true);
            bool allowRemovals   = _allowRemovals;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await GitPublisher.PublishAsync(snapshot, path, categorySnapshot, allowRemovals);
                _exportOk     = r.Ok;
                _exportStatus = r.Summary;
                _publishLog   = r.Log;
            });
        }
        if (busy) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Export only", new Vector2(140, 30)))
        {
            var report = ChallengeExporter.Export(_config.CustomChallenges, _exportPath.Trim(),
                                                  ChallengeCatalog.Categories(_config, includeEmpty: true),
                                                  _allowRemovals);
            _exportOk     = report.Ok;
            _exportStatus = report.Message;
            _publishLog   = string.Empty;
        }

        if (!string.IsNullOrEmpty(_exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(string.Empty);
            ImGui.TextColored(_exportOk ? new Vector4(0.50f, 0.84f, 0.66f, 1f)
                                        : new Vector4(0.90f, 0.42f, 0.38f, 1f),
                              _exportStatus);
        }

        if (!string.IsNullOrEmpty(_publishLog))
        {
            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Git output"))
            {
                if (ImGui.BeginChild("##tc_publish_log", new Vector2(0, 180), true))
                    ImGui.TextUnformatted(_publishLog);
                ImGui.EndChild();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Publish clones the repo if needed, re-exports, commits as Sansflaire, and pushes.");
    }

    public static string KindLabel(ChallengeKind kind) => kind switch
    {
        ChallengeKind.Manual            => "No detector",
        ChallengeKind.VisitAreas        => "Visit areas",
        ChallengeKind.VisitAreasInOrder => "Visit areas in order",
        ChallengeKind.EmoteAtArea       => "Emote at location",
        ChallengeKind.MountInArea       => "Mount in area",
        ChallengeKind.GearInArea        => "Outfit / gear",
        ChallengeKind.InArea            => "In area",
        ChallengeKind.RaceTimer         => "Race",
        _                               => kind.ToString(),
    };
}
#endif
