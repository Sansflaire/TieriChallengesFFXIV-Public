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
    private bool _tabAlwaysOpen = true;

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

    private const string NewCategorySentinel = "＋ New category…";

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

            string createLabel = _editingId == null ? "Create" : "Edit";
            if (ImGui.BeginTabItem($"{createLabel}###tc_tab_create", ref _tabAlwaysOpen, createFlags))
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
        }

        ImGui.Separator();
        DrawAddButton();
    }

    private static readonly (ChallengeKind Kind, string Label, string Blurb)[] Kinds =
    {
        (ChallengeKind.VisitAreas,        "Visit all areas (any order)",
            "Enter every area at least once within one login session."),
        (ChallengeKind.VisitAreasInOrder, "Visit all areas (in order)",
            "Enter the areas in the listed order. Entering a later one early does nothing."),
        (ChallengeKind.EmoteAtArea,       "Emote at a location",
            "Perform a chosen emote inside the area. Optionally while facing a captured direction."),
        (ChallengeKind.MountInArea,       "Specific mount in an area",
            "Be riding a chosen mount while inside the area."),
        (ChallengeKind.GearInArea,        "Outfit / gear in a zone or area",
            "Wear a complete Glamour Dresser outfit, or one specific item, in a zone or area."),
    };

    private void DrawKindPicker()
    {
        ImGui.TextUnformatted("Challenge type");

        string current = "?";
        string blurb   = string.Empty;
        foreach (var (k, label, b) in Kinds)
            if (k == _kind) { current = label; blurb = b; }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##tc_kind", current))
        {
            foreach (var (k, label, b) in Kinds)
            {
                if (ImGui.Selectable(label, k == _kind)) _kind = k;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(b);
            }
            ImGui.EndCombo();
        }

        ImGui.TextDisabled(blurb);
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
            if (lp != null) a.SetCenter(lp.Position);
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
                    draft.MinPluginVersion = ChallengeCatalog.RequiredFor(draft.Kind).ToString();
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
        };

        foreach (var a in _areas) draft.Areas.Add(a.Clone());
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
        _                               => "Incomplete.",
    };

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
        foreach (var a in c.Areas) _areas.Add(a.Clone());

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
        _emoteId = 0; _emoteName = string.Empty;
        _mountId = 0; _mountName = string.Empty;
        _outfitId = 0; _outfitName = string.Empty;
        _gearItemId = 0; _gearItemName = string.Empty;
        _requireFacing = false;
        _wholeZone = false;
        _showProgress = true;

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

    private void DrawExistingTab()
    {
        DrawCategoryManager();
        DrawUnadoptedOfficial();

        if (_config.CustomChallenges.Count == 0)
        {
            ImGui.TextDisabled("None yet. Everything in the list is built-in.");
            return;
        }

        int removeAt = -1;

        for (int i = 0; i < _config.CustomChallenges.Count; i++)
        {
            var c = _config.CustomChallenges[i];
            ImGui.PushID(i);

            bool missingDetails = string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Detail);

            string label = string.IsNullOrWhiteSpace(c.Title) ? "(unnamed)" : c.Title;
            ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f), c.Category);
            ImGui.SameLine();
            ImGui.TextUnformatted(label);

            if (missingDetails) MissingLabel("Missing details");

            ImGui.TextDisabled($"   {KindLabel(c.Kind)} · {c.TerritoryName} · "
                             + $"#{c.SortOrder} · {c.Areas.Count} area(s) · "
                             + $"{(_store.IsComplete(c.Id) ? "COMPLETE" : "incomplete")}");
            ImGui.TextDisabled($"   guid {c.Id}");

            // The main window shows the PUBLISHED copy of a synced challenge — official wins on
            // a GUID collision — so a local edit looks like it did nothing until it is published
            // and re-synced. Say so here, where the edit was made.
            switch (PublishState(c))
            {
                case PublishStatus.Edited:
                    ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f),
                        "   edited locally — publish to replace the live copy");
                    break;
                case PublishStatus.Matches:
                    ImGui.TextDisabled("   published, and identical to the live copy");
                    break;
                case PublishStatus.Unpublished:
                    ImGui.TextDisabled("   not published yet");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(c.Detail)) ImGui.TextDisabled($"   {c.Detail}");

            // Not flagged red when absent — a missing hint is a choice, not a defect.
            if (!string.IsNullOrWhiteSpace(c.Hint))
                ImGui.TextColored(new Vector4(0.66f, 0.79f, 0.94f, 1f), $"   Hint: {c.Hint}");
            else
                ImGui.TextDisabled("   no hint");

            if (ImGui.Button("Edit", new Vector2(80, 22))) LoadDraft(c);
            ImGui.SameLine();
            if (ImGui.Button("Delete", new Vector2(80, 22))) removeAt = i;

            ImGui.PopID();
            ImGui.Separator();
        }

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
        _                               => kind.ToString(),
    };
}
#endif
