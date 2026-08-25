using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using Lumina.Excel;
using Lumina.Excel.Sheets;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

// Background images are handed to Panache as SKBitmap — Style.ImageBitmap is Skia-typed, so a
// renderer that draws one necessarily references SkiaSharp. Safe HERE and only here: this file
// already cannot load without PanacheUI, which cannot load without SkiaSharp. FallbackWindow
// must never gain this using.
using SkiaSharp;

// Lumina.Excel.Sheets also declares an `Action` row type, which collides with System.Action.
using Action = System.Action;

namespace TieriChallengesFFXIV;

/// <summary>
/// The main window: master–detail per DESIGN_SYSTEM §5.1.
///
/// Everything visible is a PanacheUI node rendered to a Skia surface and blitted through a
/// single <c>ImGui.Image</c> — ImGui is only the host. The one exception is the reset
/// confirmation, which is a standard ImGui modal; DESIGN_SYSTEM §10 anti-pattern 8 explicitly
/// permits raw ImGui widgets inside tooltips and standard popups.
///
/// Interaction uses <c>Node.OnClick</c>, which <see cref="PanacheSurface.Render"/> drives via
/// InteractionManager. The tree is rebuilt every frame, so handlers are re-subscribed to fresh
/// Node objects each frame — that is correct and does not leak.
///
/// The mandatory hover cue of DESIGN_SYSTEM §7.2 is declared on the node —
/// <c>Style.HoverBackgroundColor</c> / <c>HoverBorderColor</c> / <c>HoverColor</c> — and
/// cross-faded by SkiaRenderer from the node's own animation state. It used to be a
/// <c>_hoverId</c> / <c>_hoverNext</c> pair repainted from this class, back when the renderer
/// ignored <c>Anim.IsHovered</c>; that cost a frame of lag and a handler per interactive node.
///
/// One consequence worth knowing before adding a control: <b>hover does not reach a
/// <c>PointerEvents.None</c> child</b>, so the node that PAINTS the cue must be the node the
/// pointer actually hits. Where a cue used to live on an inert child, the Id and the click
/// moved down onto that child (see <c>ModeTab</c>); where a glyph brightened on hover inside a
/// button, that component was dropped in favour of the button box's own fill (see
/// <c>IconButton</c>). A glyph tint cannot be hover-driven at all — there is no
/// <c>HoverImageTint</c>.
/// </summary>
internal sealed class MainWindow : IDisposable
{
    public bool IsVisible;

    /// <summary>How long a revealed challenge keeps its highlight after the button is clicked.</summary>
    private const double FocusHighlightSeconds = 10.0;

    /// <summary>Challenge the progress notification asked to reveal, and when its cue expires.</summary>
    private string?  _focusId;
    private DateTime _focusUntil = DateTime.MinValue;

    /// <summary>
    /// Bumped on every reveal and folded into the challenge list's scroll Id.
    ///
    /// <para>PanacheUI keys a scroll offset by Id, so changing the Id hands the list a fresh
    /// container that starts at the top. That is the only programmatic scroll available here:
    /// <c>InteractionManager</c> exposes <c>ResetScroll</c> but no scroll-to-offset, and the
    /// manager instance is owned internally by <c>PanacheSurface</c> anyway.</para>
    /// </summary>
    private int _focusNonce;

    /// <summary>
    /// Reveal a challenge: select its category so the right list is showing, send that list back
    /// to the top, and highlight the row for a few seconds. Called by the progress notification's
    /// Show button.
    /// </summary>
    public void FocusChallenge(string challengeId, string category)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && !string.Equals(category, _config.SelectedCategory, StringComparison.Ordinal))
        {
            _config.SelectedCategory = category;
            _save();
        }

        // Selecting the category reveals nothing while the pane is grouped by zone — that pane is
        // keyed by territory, so the reveal has to move the zone selection too.
        if (_config.Grouping == GroupMode.Zones)
        {
            ZoneIndex.Reveal(_config, ZoneIndex.TerritoryOf(_config, challengeId));
            _save();
        }

        _focusId    = challengeId;
        _focusUntil = DateTime.UtcNow.AddSeconds(FocusHighlightSeconds);
        _focusNonce++;
    }

    /// <summary>
    /// Show categories that have no challenges in them. Authoring needs them — a category created
    /// ahead of its content would otherwise be invisible in the very list it was added to. Players
    /// do not: an empty category is a dead end, and the public build has no way to fill it.
    /// </summary>
    private bool ShowEmptyCategories
    {
#if DEV_BUILD
        get => !_config.PublicPreview;
#else
        get => false;
#endif
    }

    /// <summary>Dev builds see every challenge unspoiled while authoring — public-preview mode
    /// is the one exception, since its whole point is to show exactly what a player would see.</summary>
    private bool DevBypassesSpoilers
    {
#if DEV_BUILD
        get => !_config.PublicPreview;
#else
        get => false;
#endif
    }

    /// <summary>
    /// The zone/expansion list the master pane actually walks. Normally the reachable-and-
    /// authored set; in dev builds, with <see cref="Configuration.DevShowAllContent"/> on, the
    /// full game census instead — Trist's "what still needs a challenge" tracker.
    /// </summary>
    private IReadOnlyList<ZoneIndex.Expansion> ZoneExpansions()
    {
#if DEV_BUILD
        if (!_config.PublicPreview && _config.DevShowAllContent)
            return ZoneIndex.AllGameContent(_config);
#endif
        return ZoneIndex.Expansions(_config);
    }

    private bool IsFocused(string id) =>
        _focusId != null
        && DateTime.UtcNow < _focusUntil
        && string.Equals(id, _focusId, StringComparison.Ordinal);

#if DEV_BUILD
    /// <summary>Wired by Plugin. Exists only in dev builds — the field is compiled out of the public DLL.</summary>
    public Action? OnOpenCreator;

    /// <summary>Wired by Plugin. Dev builds only, same as <see cref="OnOpenCreator"/>.</summary>
    public Action? OnOpenSoundTest;

#endif

    /// <summary>
    /// Wired by Plugin. Opens the live-state popup.
    ///
    /// <para>Outside the DEV_BUILD block, unlike the two above — Info is a public feature. Placed
    /// inside it at first, which compiled fine in Debug and failed the Release build outright.
    /// That is precisely what building both flavours on every change is for.</para>
    /// </summary>
    public Action? OnOpenStatus;

    // ── Palette ──────────────────────────────────────────────────────────────
    // One accent (DESIGN_SYSTEM §1.2) + the shared semantic status palette (§3.3).
    private static readonly PColor Accent   = PColor.FromHex("#E3B341");
    private static readonly PColor StatusOk = PColor.FromHex("#7FD6A9");  // progress / done
    private static readonly PColor Danger   = PColor.FromHex("#E57B72");  // destructive
    private static readonly PColor Neutral  = PColor.FromHex("#8B8794");  // unknown / pending
    private static readonly PColor TextHi   = PColor.White.WithOpacity(0.92f); // never pure #FFF

    // Hints get their own hue rather than reusing the gold accent: a revealed hint has to read
    // as "this is not the description you were looking at" at a glance, from across the row.
    private static readonly PColor HintAccent = PColor.FromHex("#8FB8E8");
    private static readonly PColor HintText   = PColor.FromHex("#A9C9F0").WithOpacity(0.95f);

    /// <summary>
    /// The multiplier behind Settings → UI Scale, handed to <see cref="PanacheSurface.Scale"/>.
    ///
    /// <para>Every layout number in this file is unscaled. The surface lays out against
    /// <c>Width / Scale</c> and scales the CANVAS before painting, so text rasterises at its
    /// effective size instead of being resampled, and it divides the pointer by the same factor so
    /// clicks land where they look. Nothing here has to know the scale exists — which is the point,
    /// because the previous approach (a <c>*Base</c> constant plus a multiplying property for every
    /// size) silently left any newly added token stuck at step 1.</para>
    ///
    /// <para>Step 1 is exactly 1.0, so the default look is bit-for-bit what it was before scaling
    /// existed. Steps rather than a slider — see <see cref="Configuration.UiScale"/>.</para>
    /// </summary>
    private static float ScaleFactor => UiScale.Factor;

    // ── Layout tokens (DESIGN_SYSTEM §2, §9.5 — no magic numbers in draw methods) ──
    private const float AccentBarW       = 3f;
    private const float SelBarW          = 2f;
    private const int   RowH_Master      = 46;
    private const int   RowH_Challenge   = 46;   // deviation from the 26px detail row: our rows
                                                     // are two-line AND clickable, which is the 46px
                                                     // "I might click one" case in §2's taxonomy.
    private const int   NarrowBreakpoint = 500;
    private const float PadPaneX         = 14f;
    private const float PadPaneY         = 10f;
    private const float ProgressH        = 3f;
    private const float CounterW         = 52f;
    private const int   DetailHeaderH    = 76;
    private const int   MasterLabelH     = 26;
    /// <summary>Master pane header row. One of these for Categories, two for Zones (mode + filter).</summary>
    private const int   MasterHeaderH    = 32;
    private const int   RowH_Expansion   = 28;


    // Menu bar. MenuBarTop/Left mirror the header's padding — the dropdown is positioned against
    // the ROOT, so it cannot inherit them and has to be told where the bar actually starts.
    private const int   MenuBarH         = 22;
    private const float MenuBarTop       = 11f + 24f + 3f;   // header padding + title row + margin
    private const float MenuBarLeft      = AccentBarW + 16f; // accent bar + header left padding
    private const float MenuTitlePadX    = 10f;
    private const int   MenuItemH        = 24;
    private const float MenuPanelMinW    = 150f;
    private const float MenuItemPadX     = 12f;
    // Font sizes are tokens because MenuTitleWidth / MenuItemWidth measure against them. A literal
    // here and a different literal at the draw site would place every dropdown slightly wrong.
    private const float MenuTitleFontSize = 12f;
    private const float MenuItemFontSize  = 11.5f;


    // Tab strip at the top of the master pane.
    private const int   TabStripH        = 30;
    private const float TabStripTopPad   = 6f;
    private const float TabStripPadX     = 8f;
    private const float TabPadX          = 14f;
    private const float TabRadius        = 5f;
    /// <summary>The §2 "scan a list" row. Zones get it because the list is ~150 long, not ~6.</summary>
    private const int   RowH_Zone        = 26;
    private const float PillGap          = 6f;   // still used by the hint/status pill rows


    // Hint display. A revealed hint replaces the description line and may need more than one
    // line, so the row grows rather than truncating what the player just asked to see.
    //
    // How that growth is worked out changed completely on 2026-08-25. It used to be a hand-rolled
    // word wrapper fed by two guesses — an average glyph width, and a flat "width the pills take"
    // constant that had to be re-tuned by hand every time a row icon changed size (250 → 205 →
    // 230). Both are gone. The row is now Fit-height with a MinHeight floor and the hint node is
    // TextOverflow.Wrap, so PanacheUI measures the real text against the width the row's actual
    // children actually leave — see LayoutEngine.HorizontalFillWidth, which exists for exactly
    // this shape. Nothing here can go stale when an icon is resized, because nothing here knows
    // an icon's size any more.
    /// <summary>
    /// Cap on a revealed hint's lines; the renderer ellipsises the last one. Without a cap, one
    /// wordy hint could push every other challenge off the screen.
    /// </summary>
    private const int HintMaxLines = 3;

    /// <summary>
    /// The second line of a challenge row — description, completion date, or hint. One token
    /// because the hint and the line it replaces must rasterise identically; two numbers that
    /// happened to agree would drift the first time either was touched.
    /// </summary>
    private const float SubFontSize = 10f;

    // Icon sizing. Every bundled PanacheUI icon is square, so one number sizes each use.
    /// <summary>Corner controls (lock, close): the button box.</summary>
    private const float ChromeBtn    = 22f;
    /// <summary>
    /// Corner controls: the glyph inside the box. The difference becomes padding, so raising this
    /// grows the icon WITHOUT growing the button.
    ///
    /// <para>Was 11 (a half-size glyph) and read as far smaller than that, because the bundled
    /// PNGs carry their own internal margin — the padlock artwork only fills about 70% of its
    /// frame, so an "11px icon" drew an ~8px padlock in a 22px box. At 18 the frame nearly fills
    /// the button and the padlock lands around 13px, which is what the control needed to be
    /// legible. Only 2px of inset is left, which is the whole point: the box did not change.</para>
    /// </summary>
    private const float ChromeGlyph  = 18f;
    // The three row icons were all doubled from their first sizes (15 / 13 / 12). They sit in a
    // 46px row, so even at double they clear the row with 8-10px to spare — and the same internal
    // margin that made ChromeGlyph read small applies here, so the drawn mark is smaller than the
    // number suggests.
    /// <summary>Per-row completion checkbox in the detail list.</summary>
    private const float StatusIconSz = 30f;
    /// <summary>"You are in this zone" marker on a challenge row.</summary>
    private const float HereIconSz   = 26f;

    /// <summary>
    /// The hint button's round box, and the glyph inside it. The box grew with the glyph — unlike
    /// the corner chrome, where there was slack to spend, this button was already only 8px larger
    /// than its icon, so doubling the icon had nowhere to go but outward.
    /// </summary>
    private const float HintBtn      = 28f;
    private const float HintGlyph    = 24f;
    /// <summary>Leading icon in a dropdown menu item.</summary>
    private const float MenuIconSz   = 13f;
    /// <summary>Gap from a menu item's icon to its label.</summary>
    private const float MenuIconGap  = 8f;
    /// <summary>
    /// Category-complete flourish, left of the category name. Was 13, which read as smaller than
    /// the 12.5px bold title it sits beside rather than as a mark on it.
    ///
    /// <para>About 20 is the ceiling before <see cref="RowH_Master"/> has to grow: the row's
    /// vertical budget is 46 − 7 top padding − 5 gap − ~12 for the progress/counter line.</para>
    /// </summary>
    private const float CatIconSz    = 18f;

    /// <summary>
    /// Height of the category-name band. Fixed, and identical whether or not the badge is
    /// present — with a Fit height the taller badge would set the row's height and drop the
    /// title of every FINISHED category ~1.5px below the unfinished ones, which reads as
    /// broken alignment while scanning the list. Matching the icon exactly also means centring
    /// it is exact rather than nearly.
    /// </summary>
    private const float CatNameRowH  = CatIconSz;
    /// <summary>One pip in the five-slot difficulty meter.</summary>
    private const float StarSz       = 11f;
    private const float StarGap      = 2f;


    // Both of the estimate-a-width helpers that used to live here are gone: the challenge row's
    // hint wrapper (layout measures it now) and the star block's contribution to it. PanacheUI
    // grew real text measurement on 2026-08-24, so nothing in this file guesses at text extents
    // any more — see MenuTitleWidth / MenuItemWidth for the menu bar's side of the same change.

    // Header height is now exactly its content, not a fraction of the window.
    //
    // It used to be max(HeaderMinH, height/4) — a rule written when the header also carried the
    // live game-state readout and a wrapping row of action pills. Both are gone (state moved
    // behind Info, the pills became the menu bar), so the quarter rule was reserving a band of
    // empty space that grew with the window and pushed the challenge list down for nothing.
    //
    // The dropdowns do NOT need to fit inside it: BuildTree appends the open menu panel to the
    // ROOT last, specifically so it draws over the panes and escapes their clipping. A menu is
    // free to hang far below the header, which is what lets the header be this tight.
    //
    // Content, top to bottom: 11 padding + title row + 4 gap + menu bar + 4 gap + progress label
    // + bar + 10 padding. Dev builds add nothing — their diagnostics live in StatusWindow.
    private const int HeaderContentH = 100;

    /// <summary>Extra band for the "needs a newer plugin" warning, which is not always present.</summary>
    private const int HeaderWarnH = 17;

    private int HeaderH => HeaderContentH
                         + (ChallengeCatalog.IncompatibleCount > 0 ? HeaderWarnH : 0);

    /// <summary>
    /// The bundled PanacheUI icons this window uses, named by the role each plays HERE.
    ///
    /// <para><see cref="PanacheUI.Icons.PanacheIcons"/> is deliberately ID-only and warns against
    /// building a name lookup against it. That warning is about inventing canonical names for the
    /// framework's set — these are this plugin's role assignments, which is precisely the thing
    /// that would otherwise be an unexplained integer in a draw method. If an ID's artwork
    /// changes meaning upstream, this table is the single place to correct it.</para>
    ///
    /// <para>Every assignment below was read off the actual PNG, not guessed from the number.
    /// Two are easy to misremember and were confirmed individually: <c>28</c> is an <i>info</i>
    /// circle (the question-mark circle is <c>9</c>) and <c>30</c> is an info hexagon (the empty
    /// checkbox is <c>36</c>).</para>
    /// </summary>
    private static class Ico
    {
        public const int Close        = 5;    // X
        public const int Locked       = 3;    // closed padlock
        public const int Unlocked     = 4;    // open padlock
        public const int Incomplete   = 36;   // empty rounded square
        public const int Complete     = 15;   // checked box
        public const int HereNow      = 19;   // map pin with range rings
        public const int Hint         = 9;    // ? in a circle
        public const int CategoryDone = 32;   // circled check with sparkles
        public const int Info         = 28;   // i in a circle
        public const int Sync         = 51;   // circular arrows
        public const int Restore      = 52;   // dashed circular arrow
        public const int Appearance   = 60;   // sliders
        public const int Renderer     = 71;   // half-filled circle
        public const int Reset        = 46;   // prohibition
        public const int Suggest      = 47;   // lightbulb
        public const int Creator      = 62;   // page + pencil

        /// <summary>
        /// Stand-in for UI Scale: concentric rings, read as "sizes". The set has no magnifier or
        /// resize glyph yet — that one is on the outstanding icon list. Swap this the moment it
        /// lands rather than leaving a near-miss in place.
        /// </summary>
        public const int Scale        = 25;

        /// <summary>
        /// Difficulty pips. NOT stars — the bundled set has no five-point star, filled or empty
        /// (0049 is a sparkle cluster and 0058 is a medal, neither of which tiles into a rating
        /// row). These are the filled/hollow circle pair, which reads correctly as "2 of 5" while
        /// being honestly not what was asked for. When a star pair lands, changing these two
        /// numbers is the entire migration.
        /// </summary>
        public const int StarFull     = 24;   // filled dot in a ring
        public const int StarEmpty    = 37;   // hollow circle

        /// <summary>
        /// No icon assigned yet. The menu row still reserves the column so labels stay aligned
        /// with the items that do have one — a ragged left edge reads as a bug, not as "pending".
        /// </summary>
        public const int None         = 0;
    }

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ITextureProvider _texProvider;
    private readonly Action           _save;
    private readonly ChallengeTracker _tracker;
    private readonly ChallengeSyncService _sync;
    private readonly DateTime         _start = DateTime.UtcNow;

    private PanacheSurface? _surface;

    /// <summary>
    /// Id of the node under the pointer this frame, or null.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a hover cue.</b> Every hover cue in this window is declared on the node
    /// (<c>Style.HoverBackgroundColor</c> / <c>HoverBorderColor</c> / <c>HoverColor</c>) and
    /// cross-faded by the renderer, so the paired <c>_hoverId</c> field this used to sit beside
    /// is gone along with every <c>OnMouseEnter</c> that existed only to feed it.</para>
    ///
    /// <para>What survives is the two gestures Panache's own events do not reach from here:
    /// right-click-to-teleport on a zone row, and the dev-only right-click Copy GUID on a
    /// challenge title. Both are read in <c>DrawWindow</c> at the ImGui level, which is why this
    /// is "this frame" and not "last frame" — see the read-order note there.</para>
    ///
    /// <para>Only <c>ZoneRow</c> and <c>ChallengeRow</c>'s title still write it. If both gestures
    /// ever move onto <c>Node.OnRightClick</c> (which does now exist — see §8A), this goes too.</para>
    /// </remarks>
    private string? _hoverNext;

    /// <summary>
    /// Challenges whose hint is currently revealed, by GUID. Deliberately NOT persisted: a hint
    /// is something you ask for in the moment, and a hint left open across sessions would spoil
    /// the challenge every time the window is opened.
    /// </summary>
    private readonly HashSet<string> _hintShown = new(StringComparer.Ordinal);

    /// <summary>
    /// Which menu is dropped down, by title, or null. Applied one frame behind
    /// <see cref="_openMenuNext"/> for the same reason hover is: the tree is rebuilt before the
    /// click that changes it is dispatched, so mutating it mid-walk would half-apply.
    /// </summary>
#if DEV_BUILD
    /// <summary>
    /// Challenge GUID whose right-click menu is open, and where to draw it. Dev-only: the menu
    /// holds developer actions and does not exist in the public build.
    ///
    /// <para>The position is captured at right-click time rather than followed live, so the menu
    /// stays where it was opened instead of chasing the cursor.</para>
    /// </summary>
    private string? _ctxChallengeId;
    private Vector2 _ctxAt;
#endif

    private string? _openMenu;
    private string? _openMenuNext;

    /// <summary>
    /// The decoded background image, and the path it came from. Held rather than re-decoded per
    /// frame — this is a full-size bitmap, and decoding one every frame would be ruinous.
    /// </summary>
    private SKBitmap? _bgSource;
    private string    _bgSourcePath = string.Empty;

    /// <summary>
    /// The source cropped to the window's aspect ratio, cached by that aspect.
    ///
    /// <para>SkiaRenderer draws ImageBitmap stretched to fill the node rect, so handing it the raw
    /// image distorts anything whose proportions differ from the window's. Centre-cropping first
    /// gives the "cover" behaviour a background wants, and keying the cache on aspect means it is
    /// recomputed when the window is reshaped, not while it is merely resized.</para>
    /// </summary>
    private SKBitmap? _bgCropped;
    private float     _bgCroppedAspect = -1f;

    /// <summary>
    /// The modals live in <see cref="Dialogs"/>, not here. They are shared with the plain-ImGui
    /// fallback renderer, and — critically — that class touches no PanacheUI type, so the reset
    /// and suggestion dialogs keep working when this renderer cannot load at all.
    /// </summary>
    private readonly Dialogs _dialogs;

    public MainWindow(Configuration config, CompletionStore store, ITextureProvider texProvider,
                      Action save, ChallengeTracker tracker, Dialogs dialogs,
                      ChallengeSyncService sync)
    {
        _config      = config;
        _store       = store;
        _texProvider = texProvider;
        _save        = save;
        _tracker     = tracker;
        _dialogs     = dialogs;
        _sync        = sync;
    }

    public void Dispose()
    {
        // Drop any capture pointing into this tree before the nodes go away.
        InteractionManager.ReleaseCapture();
        _surface?.Dispose();
        _surface = null;

        // Full-size decoded bitmaps — leaking one per plugin reload would add up fast.
        _bgCropped?.Dispose();
        _bgSource?.Dispose();
        _bgCropped = null;
        _bgSource  = null;
    }

    /// <summary>
    /// Replenish current data from the permanent ledger, restoring each challenge's ORIGINAL
    /// completion date rather than stamping today. Only ever adds, so it is safe to run anytime.
    /// </summary>
    public void RestoreFromPermanent()
    {
        int restored = _store.RestoreFromPermanent();
        _config.StateVersion++;
        _tracker.Invalidate();

        Plugin.ChatGui.Print(restored > 0
            ? $"[Challenges] Restored {restored} completion(s) from permanent storage."
            : "[Challenges] Nothing to restore — current data already matches permanent storage.");
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>Modals are drawn by Plugin, once, regardless of which renderer is active.</summary>
    public void Draw()
    {
        if (IsVisible) DrawWindow();
    }

    private void DrawWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(_config.WindowWidth, _config.WindowHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(360, 320), new Vector2(1600, 1800));

        // NoTitleBar is mandatory — the Panache header IS the window chrome (DESIGN_SYSTEM §1.1).
        // Without a title bar ImGui still lets the body be dragged by default, which is what the
        // lock pill exists to stop.
        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse;
        if (_config.WindowLocked) flags |= ImGuiWindowFlags.NoMove;

        if (!ImGui.Begin("##tierichallenges_main", ref IsVisible, flags))
        {
            ImGui.End();
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        int w = Math.Max(360, (int)avail.X);
        int h = Math.Max(320, (int)avail.Y);

        _config.WindowWidth  = w;
        _config.WindowHeight = h;

        if (_surface == null) _surface = new PanacheSurface(_texProvider, w, h);
        else                  _surface.Resize(w, h);

        // UI scale is the surface's job now. It lays out against Width/Scale and rasterises the
        // canvas at the scaled size, so glyphs are drawn at their effective size rather than
        // resampled — and it divides the pointer by the same factor, so clicks stay where they
        // look. This replaced ~40 hand-scaled layout tokens and 26 wrapped font sizes in this
        // file; everything below is back to plain unscaled numbers.
        _surface.Scale = ScaleFactor;

        _openMenuNext = _openMenu;
        var root = BuildTree((int)_surface.LogicalWidth, (int)_surface.LogicalHeight);

        var origin     = ImGui.GetCursorScreenPos();
        var mouse      = ImGui.GetMousePos();
        var localMouse = new Vector2(mouse.X - origin.X, mouse.Y - origin.Y);

        bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool mouseClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                       && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                              | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                              | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        // PanacheUI has no right-click concept at all — Node.OnClick and InteractionManager only
        // ever look at the left button. Reading it here at the ImGui level, cross-referenced
        // against whichever row _hoverNext resolves to THIS frame (below, after Render), is the
        // documented workaround rather than a Panache change — see the 2026-08-24 conversation
        // with Trist on adding real OnRightClick to Panache later.
        bool rightClick = ImGui.IsMouseClicked(ImGuiMouseButton.Right)
                        && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                               | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                               | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        // A modal owns the mouse; swallow clicks so the surface behind it stays inert.
        if (_dialogs.AnyOpen) { mouseClick = false; mouseDown = false; rightClick = false; }

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;
        float dt   = ImGui.GetIO().DeltaTime;

        var (tex, _) = _surface.Render(root, time, localMouse, mouseDown, mouseClick,
                                       ImGui.GetIO().MouseWheel, dt, forceRedraw: false);

        if (tex.HasValue)
            ImGui.Image(tex.Value, new Vector2(w, h));

        // _hoverNext was updated during the Render call just above, so it reflects what is under
        // the cursor THIS frame rather than last frame's row. It is maintained by an
        // enter/leave pair (see ZoneRow) instead of the enter-only handler it used to have —
        // that alone never cleared, so moving off the last zone row you touched and right-
        // clicking empty space would teleport you to it.
        if (rightClick) HandleZoneRightClick(_hoverNext);

#if DEV_BUILD
        if (rightClick && !_config.PublicPreview)
        {
            // Same single-field read as the zone handler above, and for the same reason: this is
            // what the pointer is over NOW. The previous-frame copy that used to back the hover
            // cue is gone, so there is no stale value left to reach for by mistake.
            if (_hoverNext != null && _hoverNext.StartsWith("chal:", StringComparison.Ordinal))
            {
                _ctxChallengeId = _hoverNext["chal:".Length..];
                // Logical coords: the surface lays out at Width/Scale, so a raw pixel position
                // would place the menu further from the cursor the higher the UI scale.
                _ctxAt = localMouse / ScaleFactor;
            }
            else
            {
                _ctxChallengeId = null;   // right-click anywhere else dismisses it
            }
        }

        // A left click anywhere closes it, matching every other context menu on the desktop.
        if (mouseClick) _ctxChallengeId = null;
#endif

        // Right-click has no built-in Panache affordance to hint at it — a plain ImGui tooltip,
        // triggered off the same _hoverNext read, is the cheapest way to make a hidden gesture
        // discoverable without waiting on real Panache hover-tooltip support.
        if (!string.IsNullOrEmpty(_hoverNext) && _hoverNext.StartsWith("zone_", StringComparison.Ordinal)
            && uint.TryParse(_hoverNext.AsSpan(5), out uint hoveredZone) && hoveredZone != ZoneIndex.AnyZone)
        {
            ImGui.SetTooltip($"Right-click to teleport to {ZoneIndex.DisplayName(_config, hoveredZone)}");
        }

        _openMenu = _openMenuNext;

        ImGui.End();
    }

    /// <summary>
    /// Right-click-to-teleport. <paramref name="hoveredId"/> is whatever <c>_hoverNext</c>
    /// resolved to this frame; only zone rows (<c>"zone_&lt;territoryId&gt;"</c>) act on it, so
    /// right-clicking a challenge row, a menu, a pill — anything else — is a harmless no-op.
    /// </summary>
    private void HandleZoneRightClick(string? hoveredId)
    {
        if (string.IsNullOrEmpty(hoveredId) || !hoveredId.StartsWith("zone_", StringComparison.Ordinal))
            return;
        if (!uint.TryParse(hoveredId.AsSpan(5), out uint territoryId) || territoryId == ZoneIndex.AnyZone)
            return;

        switch (AttunementService.TryTeleport(territoryId))
        {
            case AttunementService.TeleportOutcome.Dispatched:
                // No plugin sound here on purpose — the game's own teleport cast already has one,
                // and this cue set is for challenge events, not for an unrelated navigation action.
                break;

            case AttunementService.TeleportOutcome.NoAetheryteInZone:
                FlyTextService.ShowError("No Aetheryte", $"None exists in {ZoneIndex.DisplayName(_config, territoryId)}");
                break;

            case AttunementService.TeleportOutcome.NotAttuned:
                FlyTextService.ShowError("Not Attuned", $"Visit an aetheryte in {ZoneIndex.DisplayName(_config, territoryId)} first");
                break;

            case AttunementService.TeleportOutcome.Failed:
                FlyTextService.ShowError("Teleport Failed", "Try again");
                break;
        }
    }

    // ── Tree ─────────────────────────────────────────────────────────────────

    private Node BuildTree(int w, int h)
    {
        var root = PUI.RootNode(w, h);

        // Background image sits on the root, behind everything. The panels above it are painted
        // translucent (see Surface) so it reads through them.
        var bg = ResolveBackground(w, h);
        if (bg != null)
        {
            root.WithStyle(s =>
            {
                s.ImageBitmap = bg;
                s.ImageTint   = PColor.White.WithOpacity(Math.Clamp(_config.BackgroundImageOpacity, 0.05f, 1f));
            });
        }

        int headerH = HeaderH;
        int bodyH   = Math.Max(60, h - headerH - 1);

        bool narrow = w < NarrowBreakpoint;
        int masterW = narrow ? w : Math.Max(160, w / 3);
        int detailW = narrow ? 0 : Math.Max(0, w - masterW - 1);

        root.AppendChild(BuildHeader(w, headerH));
        root.AppendChild(Hairline(PColor.White.WithOpacity(0.06f)));
        root.AppendChild(BuildBody(masterW, detailW, bodyH, narrow));

#if DEV_BUILD
        BuildChallengeContextMenu(root);
#endif

        // Last, so it draws over the panes and escapes their ClipContent.
        AppendMenuOverlay(root, w, h);

        return root;
    }

    /// <summary>
    /// Panel colour, made translucent when a background image is showing.
    ///
    /// <para>Every panel background goes through this. Without it the image would be completely
    /// hidden behind opaque panels — visible only in whatever margin the layout happened to leave,
    /// which is not a background at all.</para>
    /// </summary>
    private PColor Surface(PColor c) =>
        _bgCropped == null ? c : c.WithOpacity(Math.Clamp(_config.PanelOpacity, 0.10f, 1f));

    /// <summary>
    /// Decode the configured image if needed, and centre-crop it to the window's aspect.
    /// Returns null when no image is set or the file cannot be read.
    /// </summary>
    private SKBitmap? ResolveBackground(int w, int h)
    {
        string path = _config.BackgroundImagePath ?? string.Empty;

        if (!string.Equals(path, _bgSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            _bgSource?.Dispose();
            _bgCropped?.Dispose();
            _bgSource        = null;
            _bgCropped       = null;
            _bgCroppedAspect = -1f;
            _bgSourcePath    = path;

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    // A corrupt or unsupported file must not take the window down with it: decode
                    // failure just means no background.
                    if (System.IO.File.Exists(path)) _bgSource = SKBitmap.Decode(path);
                    if (_bgSource == null)
                        Plugin.Log.Warning($"[Appearance] could not decode background image: {path}");
                    else
                        Plugin.Log.Information($"[Appearance] background loaded: {_bgSource.Width}x{_bgSource.Height}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "[Appearance] background image failed to load");
                    _bgSource = null;
                }
            }
        }

        if (_bgSource == null || w <= 0 || h <= 0) return null;

        float want = w / (float)h;
        if (_bgCropped != null && MathF.Abs(_bgCroppedAspect - want) < 0.01f) return _bgCropped;

        try
        {
            int srcW = _bgSource.Width, srcH = _bgSource.Height;
            if (srcW <= 0 || srcH <= 0) return null;

            // Widest/tallest centred rectangle of the source matching the window's aspect.
            int cropW = srcW, cropH = (int)MathF.Round(srcW / want);
            if (cropH > srcH)
            {
                cropH = srcH;
                cropW = (int)MathF.Round(srcH * want);
            }

            cropW = Math.Clamp(cropW, 1, srcW);
            cropH = Math.Clamp(cropH, 1, srcH);

            var rect    = new SKRectI((srcW - cropW) / 2, (srcH - cropH) / 2, 0, 0);
            rect.Right  = rect.Left + cropW;
            rect.Bottom = rect.Top  + cropH;

            var cropped = new SKBitmap(cropW, cropH);
            if (!_bgSource.ExtractSubset(cropped, rect))
            {
                cropped.Dispose();
                return _bgSource;   // crop failed; a stretched image beats none
            }

            _bgCropped?.Dispose();
            _bgCropped       = cropped;
            _bgCroppedAspect = want;
            return _bgCropped;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Appearance] background crop failed");
            return _bgSource;
        }
    }

    // ── Header (top quarter) ─────────────────────────────────────────────────

    private Node BuildHeader(int w, int headerH)
    {
        var strip = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = headerH;
        });

        // 3px left accent bar at 70%, full strip height (DESIGN_SYSTEM §4).
        strip.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width = AccentBarW;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = Accent.WithOpacity(0.70f);
            s.PointerEvents   = PointerEvents.None;
        }));

        var body = new Node().WithStyle(s =>
        {
            s.Flow                  = Flow.Vertical;
            s.WidthMode             = SizeMode.Fill;
            s.HeightMode            = SizeMode.Fill;
            s.BackgroundColor       = Surface(Theme.Base);
            s.BackgroundGradientEnd = Surface(Theme.Panel);   // must end at Panel so it bleeds into the body
            s.Padding               = new EdgeSize(11, 16, 10, 14);
            s.Gap                   = 4;
        });

        // ── Row 1: the title, on its own line ────────────────────────────────
        //
        // The title used to share a row with the action pills, with WidthMode.Fill. That is a
        // trap: PlaceHorizontal gives Fill children `contentW - fixedWTotal`, so once the pills
        // outgrew the window the title collapsed to ZERO WIDTH and vanished entirely while the
        // pills still overflowed off the right edge. Giving the title its own row means it can
        // never be squeezed out by anything.
        var titleLine = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
        });

        titleLine.AppendChild(new Node().WithText("FFXIV Miscellaneous Challenges").WithStyle(s =>
        {
            s.WidthMode        = SizeMode.Fill;
            s.HeightMode       = SizeMode.Fit;
            s.FontSize         = 17f;
            s.Bold             = true;
            s.Color            = Accent;
            s.TextOutlineColor = PColor.Black.WithOpacity(0.70f);
            s.TextOutlineSize  = 1.2f;
            s.TextOverflow     = TextOverflow.Ellipsis;
            s.PointerEvents    = PointerEvents.None;
        }));

        titleLine.AppendChild(new Node().WithText(PluginVersion.Display).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fit;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(5, 0, 0, 0);
            s.FontSize      = 10f;
            s.Color         = Theme.TextSubtle;
            s.PointerEvents = PointerEvents.None;
        }));

        // Lock, immediately left of Close. Padlock icons rather than a letter: the old control was
        // the letter "L" in both states, which named the control but never its setting. Closed vs
        // open padlock is the state, readable without clicking.
        //
        // The ASCII-only rule these two used to follow was a font-coverage worry — PanacheUI's
        // bundled icon set sidesteps it entirely, since these are PNGs, not glyphs, and cannot
        // depend on what the renderer's typeface happens to contain.
        titleLine.AppendChild(IconToggle("btn_lock", Ico.Locked, Ico.Unlocked, _config.WindowLocked, () =>
        {
            _config.WindowLocked = !_config.WindowLocked;
            _save();
        }));

        // Close, top-right, and the ONLY close control — NoTitleBar is mandatory for a Panache
        // window (DESIGN_SYSTEM §1.1), so ImGui draws no close box and the chrome must supply one.
        titleLine.AppendChild(CloseButton("btn_close_x", () => IsVisible = false));

        body.AppendChild(titleLine);

        // ── Row 2: the menu bar ──────────────────────────────────────────────
        //
        // Replaces the old wall of outlined pills. Those had no grouping, wrapped onto two or
        // three rows at ordinary widths, and gave equal visual weight to "Sync" and "Reset".
        // A menu bar is what every desktop program does with the same problem.
        body.AppendChild(BuildMenuBar());

        // Challenges withheld because they need a newer plugin than this one.
        if (ChallengeCatalog.IncompatibleCount > 0)
        {
            body.AppendChild(new Node()
                .WithText($"{ChallengeCatalog.IncompatibleCount} challenge(s) need plugin "
                        + $"v{ChallengeCatalog.HighestRequired} or newer — update to see them.")
                .WithStyle(s =>
                {
                    s.WidthMode     = SizeMode.Fill;
                    s.HeightMode    = SizeMode.Fit;
                    s.FontSize      = 10.5f;
                    s.Bold          = true;
                    s.Color         = Danger;
                    s.TextOverflow  = TextOverflow.Ellipsis;
                    s.PointerEvents = PointerEvents.None;
                }));
        }

        // Live game state used to sit here as two to six permanent rows. It is behind the Info
        // button now — see StatusWindow. Reclaiming those rows also stopped the outfit walk from
        // running on a timer for as long as this window was open.

        // Flexible gap pushes the overall progress block to the bottom of the header.
        body.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fill;
            s.PointerEvents = PointerEvents.None;
        }));

        // Overall progress across EVERY challenge in every category.
        var (done, total) = ChallengeCatalog.OverallProgress(_config, _store);
        float frac = ChallengeCatalog.Percent(done, total);
        bool  all  = total > 0 && done == total;

        var totalsRow = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Horizontal;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Gap           = 8;
            s.PointerEvents = PointerEvents.None;
        });

        totalsRow.AppendChild(new Node().WithText("ALL CHALLENGES").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 9.5f;
            s.Bold       = true;
            s.Color      = Accent.WithOpacity(0.65f);
        }));

        totalsRow.AppendChild(new Node().WithText($"{done} of {total}  ·  {frac * 100f:0}%").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fit;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 11.5f;
            s.Bold       = true;
            s.Color      = all ? StatusOk : TextHi;
        }));

        body.AppendChild(totalsRow);

        // Header content width = w - accent bar - horizontal padding.
        float barW = Math.Max(1f, w - AccentBarW - 30f);
        body.AppendChild(ProgressBar(barW, frac, all ? StatusOk : StatusOk.WithOpacity(0.85f)));

        strip.AppendChild(body);
        return strip;
    }

    // ── Body: master | separator | detail ────────────────────────────────────

    private Node BuildBody(int masterW, int detailW, int bodyH, bool narrow)
    {
        var body = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = bodyH;
        });

        var categories = ChallengeCatalog.Categories(_config, includeEmpty: ShowEmptyCategories);
        string selected = ResolveSelection(categories);

        // Tallied once and handed down. Per-row lookups would walk the whole catalogue for each
        // of ~150 zone rows; see ZoneIndex.Counts for why that shape was rejected.
        var counts = _config.Grouping == GroupMode.Zones
            ? ZoneIndex.Tally(_config, _store)
            : null;

        body.AppendChild(BuildMaster(masterW, categories, selected, counts));

        // Below the narrow breakpoint we collapse to master-only rather than squishing the
        // detail pane (DESIGN_SYSTEM §5.1).
        if (narrow) return body;

        body.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width = 1;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = PColor.White.WithOpacity(0.07f);
            s.PointerEvents   = PointerEvents.None;
        }));

        if (_config.Grouping == GroupMode.Zones)
        {
            uint zone = ResolveZoneSelection();
            body.AppendChild(BuildDetail(
                detailW,
                zone == uint.MaxValue ? string.Empty : ZoneIndex.DisplayName(_config, zone),
                zone == uint.MaxValue ? new List<ChallengeDef>() : ZoneIndex.InZone(_config, zone),
                zone == uint.MaxValue
                    ? ("Nothing selected.", "Pick a zone on the left.")
                    : ("No challenges here yet.", "Nothing has been published for this zone.")));
        }
        else
        {
            body.AppendChild(BuildDetail(
                detailW, selected, ChallengeCatalog.InCategory(_config, selected),
                string.IsNullOrEmpty(selected)
                    ? ("Nothing selected.", "Pick a category on the left.")
                    : ("This category is empty.", "Nothing has been added to it yet.")));
        }

        return body;
    }

    /// <summary>
    /// The selected zone, or <see cref="uint.MaxValue"/> when nothing is selected yet. Selection
    /// is stored as a territory id — a stable game identifier, never a list position (§6.1).
    /// A stored zone that no longer exists in the index falls back to unselected rather than
    /// showing a blank detail pane against a name nothing can supply.
    /// </summary>
    private uint ResolveZoneSelection()
    {
        if (_config.SelectedTerritory < 0) return uint.MaxValue;

        uint want = (uint)_config.SelectedTerritory;
        if (want == ZoneIndex.AnyZone) return want;

        foreach (var expansion in ZoneExpansions())
            foreach (var zone in expansion.Zones)
                if (zone.TerritoryId == want) return want;

        return uint.MaxValue;
    }

    /// <summary>
    /// Selection is by category NAME, never by list index (DESIGN_SYSTEM §6.1). If the stored
    /// selection no longer exists — a custom category was deleted, say — fall back to the first
    /// category rather than leaving the detail pane blank (§8.1).
    /// </summary>
    private string ResolveSelection(List<string> categories)
    {
        if (categories.Count == 0) return string.Empty;
        foreach (var c in categories)
            if (string.Equals(c, _config.SelectedCategory, StringComparison.Ordinal)) return c;
        return categories[0];
    }

    private Node BuildMaster(int masterW, List<string> categories, string selected, ZoneIndex.Counts? counts)
    {
        bool zones = _config.Grouping == GroupMode.Zones;

        var pane = new Node().WithStyle(s =>
        {
            s.Flow            = Flow.Vertical;
            s.WidthMode       = SizeMode.Fixed; s.Width = masterW;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = Surface(Theme.Panel);
        });

        pane.AppendChild(BuildGroupToggle(zones));

        // Separate scroll ids per mode: the offset is keyed by Id, and a category list scrolled
        // halfway would otherwise hand its offset to a 150-row zone list and vice versa.
        var scroll = new Node().WithId(zones ? "zone_scroll" : "cat_scroll").WithStyle(s =>
        {
            s.Flow        = Flow.Vertical;
            s.WidthMode   = SizeMode.Fill;
            s.HeightMode  = SizeMode.Fill;
            s.OverflowY   = OverflowMode.Scroll;
            s.ClipContent = true;
            s.Gap         = 0;
        });

        if (zones)
        {
            BuildZoneList(scroll, masterW, counts!);
        }
        else if (categories.Count == 0)
        {
            // An empty catalogue is the NORMAL state before the first sync — there are no
            // built-ins any more. The copy must not imply something is broken, and it must name
            // the action that fixes it (DESIGN_SYSTEM §8.2).
            scroll.AppendChild(EmptyNote("No challenges yet.", EmptyCatalogueHint()));
        }
        else
        {
            foreach (var cat in categories)
                scroll.AppendChild(CategoryRow(masterW, cat, cat == selected));
        }

        pane.AppendChild(scroll);
        return pane;
    }

    /// <summary>
    /// The Category / Zone tab strip, plus the empty-zone filter when it applies.
    ///
    /// <para>Real tabs, not pills: the selected one carries the pane's own background and rounded
    /// top corners so it reads as the front edge of the pane below, while the unselected one is
    /// cut off from it by a divider running along the bottom of the strip. That divider is a
    /// child node rather than a border because Panache's BorderWidth is uniform — there is no
    /// per-side border, and a bottom-only line is the entire effect.</para>
    /// </summary>
    private Node BuildGroupToggle(bool zones)
    {
        var header = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = zones
                ? TabStripH + MasterHeaderH + (ShowDevZoneToggle ? MasterHeaderH : 0)
                : TabStripH;
        });

        var strip = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = TabStripH;
            s.Padding    = new EdgeSize(TabStripTopPad, TabStripPadX, 0, TabStripPadX);
            s.Gap        = 2;
        });

        strip.AppendChild(ModeTab(GroupMode.Categories, "Category", !zones));
        strip.AppendChild(ModeTab(GroupMode.Zones,      "Zone",      zones));

        // Tail of the divider, from the last tab to the right edge. Without it the line would
        // stop where the tabs do and the pane would look unfinished.
        var tail = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fill;
            s.PointerEvents = PointerEvents.None;
        });
        tail.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fill;
        }));
        tail.AppendChild(TabDivider(true));
        strip.AppendChild(tail);

        header.AppendChild(strip);

        if (zones)
        {
            var filterRow = new Node().WithStyle(s =>
            {
                s.Flow       = Flow.Horizontal;
                s.WidthMode  = SizeMode.Fill;
                s.HeightMode = SizeMode.Fixed; s.Height = MasterHeaderH;
                s.Padding    = new EdgeSize(6, PadPaneX, 0, PadPaneX);
            });

            bool only = _config.ZonesWithChallengesOnly;
            string id = "zones_filter";

            var toggle = new Node()
                .WithId(id)
                .WithText(only ? "Showing zones with challenges" : "Showing all zones")
                .WithStyle(s =>
                {
                    s.WidthMode    = SizeMode.Fill;
                    s.HeightMode   = SizeMode.Fit;
                    s.FontSize     = 10f;
                    s.Color        = only ? StatusOk : Theme.TextSubtle;
                    s.HoverColor   = TextHi;
                    s.TextOverflow = TextOverflow.Ellipsis;
                });

            toggle.OnClick += _ =>
            {
                _config.ZonesWithChallengesOnly = !_config.ZonesWithChallengesOnly;
                _save();
            };

            filterRow.AppendChild(toggle);
            header.AppendChild(filterRow);

#if DEV_BUILD
            if (ShowDevZoneToggle) header.AppendChild(BuildDevCensusToggle());
#endif
        }

        return header;
    }

    /// <summary>Dev builds only, and never in public-preview mode — same gate every dev affordance uses.</summary>
    private bool ShowDevZoneToggle
    {
#if DEV_BUILD
        get => !_config.PublicPreview;
#else
        get => false;
#endif
    }

#if DEV_BUILD
    /// <summary>
    /// Widen or narrow the Zone tab between the normal reachable-and-authored set and the full
    /// game census. A second row rather than folding into the existing filter pill — the two
    /// controls answer different questions ("hide empties" vs. "which universe of zones am I
    /// even looking at") and conflating them would make either one alone unreadable.
    /// </summary>
    private Node BuildDevCensusToggle()
    {
        var row = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = MasterHeaderH;
            s.Padding    = new EdgeSize(0, PadPaneX, 6, PadPaneX);
        });

        bool all = _config.DevShowAllContent;
        string id = "dev_census";

        var toggle = new Node()
            .WithId(id)
            .WithText(all ? "DEV: showing every zone + duty" : "DEV: reachable/authored only")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 10f;
                s.Bold         = all;
                s.Color        = all ? Danger : Theme.TextSubtle;
                s.HoverColor   = TextHi;
                s.TextOverflow = TextOverflow.Ellipsis;
            });

        toggle.OnClick += _ =>
        {
            _config.DevShowAllContent = !_config.DevShowAllContent;
            _save();
        };

        row.AppendChild(toggle);
        return row;
    }
#endif

    /// <summary>
    /// One tab. Selected: the pane's background, rounded top corners, and NO divider beneath, so
    /// it merges with the pane. Unselected: no fill and a divider cutting it off from the pane.
    /// </summary>
    private Node ModeTab(GroupMode mode, string label, bool active)
    {
        string id = "group_" + mode;

        var tab = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fit;
            s.HeightMode = SizeMode.Fill;
        });

        // The Id, the click and the hover all live on the LABEL, not on this wrapper. A
        // renderer-painted hover only ever reads the hovered node's own animation state, and
        // hover does not reach a PointerEvents.None child — so the node that draws the cue has
        // to be the node the pointer actually hits. The divider below is decorative and loses
        // nothing by being outside the hit area.
        var face = new Node().WithId(id).WithText(label).WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fit;
            s.HeightMode      = SizeMode.Fill;
            s.Padding         = new EdgeSize(0, TabPadX);
            s.FontSize        = 11.5f;
            s.Bold            = active;
            s.TextAlign       = TextAlign.Center;
            s.Color           = active ? Accent : Theme.TextMuted;

            // Selected tab carries the pane colour, so the seam below it disappears.
            s.BackgroundColor = active ? Surface(Theme.Panel) : PColor.Transparent;

            // The active tab is already at its emphasised colour; washing it further on hover
            // would say "clickable" about the one tab that does nothing when clicked.
            if (!active)
            {
                s.HoverColor           = TextHi;
                s.HoverBackgroundColor = PColor.White.WithOpacity(0.04f);
            }

            s.BorderRadiusTopLeft  = TabRadius;
            s.BorderRadiusTopRight = TabRadius;
        });

        face.OnClick += _ =>
        {
            if (_config.Grouping == mode) return;
            _config.Grouping = mode;
            _save();
        };

        tab.AppendChild(face);
        tab.AppendChild(TabDivider(!active));

        return tab;
    }

    /// <summary>The 1px line along the bottom of the strip. Transparent under the selected tab.</summary>
    private static Node TabDivider(bool visible) => new Node().WithStyle(s =>
    {
        s.WidthMode       = SizeMode.Fill;
        s.HeightMode      = SizeMode.Fixed; s.Height = 1;
        s.BackgroundColor = visible ? PColor.White.WithOpacity(0.16f) : PColor.Transparent;
        s.PointerEvents   = PointerEvents.None;
    });

    /// <summary>
    /// Expansions, each with its zones alphabetically beneath it — the shape of the in-game
    /// Teleport window, which is where the request came from.
    ///
    /// <para>Zones with nothing in them are listed but dimmed, so the full list is browsable
    /// while the populated zones still stand out. The filter pill above hides them entirely for
    /// anyone who would rather have the short version.</para>
    /// </summary>
    private void BuildZoneList(Node scroll, int masterW, ZoneIndex.Counts counts)
    {
        var expansions = ZoneExpansions();

        if (expansions.Count == 0)
        {
            scroll.AppendChild(EmptyNote("No zones loaded.",
                "The game's zone list could not be read. Categories still works."));
            return;
        }

        bool onlyPopulated = _config.ZonesWithChallengesOnly;
        int  shown = 0;

        foreach (var expansion in expansions)
        {
            var (exDone, exTotal) = counts.Of(expansion);
            if (onlyPopulated && exTotal == 0) continue;

            bool collapsed = _config.CollapsedExpansions.Contains(expansion.Id);
            scroll.AppendChild(ExpansionRow(expansion, exDone, exTotal, collapsed));
            shown++;

            if (collapsed) continue;

            // The catch-all group has no zones of its own — its single row IS the bucket.
            if (expansion.Zones.Count == 0)
            {
                scroll.AppendChild(ZoneRow(masterW, ZoneIndex.AnyZone, counts.Zone(ZoneIndex.AnyZone)));
                continue;
            }

            foreach (var zone in expansion.Zones)
            {
                var tally = counts.Zone(zone.TerritoryId);
                if (onlyPopulated && tally.Total == 0) continue;
                scroll.AppendChild(ZoneRow(masterW, zone.TerritoryId, tally));
            }
        }

        if (shown == 0)
        {
            scroll.AppendChild(EmptyNote("No zones have challenges yet.",
                "Switch the filter above back to all zones to browse the full list."));
        }
    }

    /// <summary>
    /// An expansion header. Clickable to collapse — with ~150 zones in the list, collapsing is
    /// the difference between a browsable index and an endless scroll.
    /// </summary>
    private Node ExpansionRow(ZoneIndex.Expansion expansion, int done, int total, bool collapsed)
    {
        string rowId = $"exp_{expansion.Id}";

        var row = new Node().WithId(rowId).WithStyle(s =>
        {
            s.Flow                 = Flow.Horizontal;
            s.WidthMode            = SizeMode.Fill;
            s.HeightMode           = SizeMode.Fixed; s.Height = RowH_Expansion;
            s.Padding              = new EdgeSize(0, PadPaneX);
            s.Gap                  = 6;
            s.BackgroundColor      = Theme.Panel2;
            s.HoverBackgroundColor = Theme.Panel2.WithOpacity(0.95f);
        });

        row.OnClick += _ =>
        {
            if (!_config.CollapsedExpansions.Remove(expansion.Id))
                _config.CollapsedExpansions.Add(expansion.Id);
            _save();
        };

        row.AppendChild(new Node().WithText(collapsed ? "+" : "−").WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fixed; s.Width = 12;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(7, 0, 0, 0);
            s.FontSize      = 11f;
            s.Bold          = true;
            s.Color         = Accent.WithOpacity(0.80f);
            s.PointerEvents = PointerEvents.None;
        }));

        row.AppendChild(new Node().WithText(expansion.Name).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(7, 0, 0, 0);
            s.FontSize      = 10.5f;
            s.Bold          = true;
            s.Color         = Accent.WithOpacity(0.90f);
            s.TextOverflow  = TextOverflow.Ellipsis;
            s.PointerEvents = PointerEvents.None;
        }));

        row.AppendChild(new Node().WithText(total > 0 ? $"{done}/{total}" : string.Empty).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fixed; s.Width = CounterW;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(7, 0, 0, 0);
            s.FontSize      = 9.5f;
            s.TextAlign     = TextAlign.Right;
            s.Color         = total > 0 && done == total ? StatusOk : Theme.TextSubtle;
            s.PointerEvents = PointerEvents.None;
        }));

        return row;
    }

    /// <summary>
    /// One zone. Compact by necessity: this list is an order of magnitude longer than the
    /// category list, so it uses the 26px "scan a list" row of DESIGN_SYSTEM §2 rather than the
    /// 46px card the categories get.
    /// </summary>
    private Node ZoneRow(int masterW, uint territoryId, (int Done, int Total) tally)
    {
        bool selected  = _config.SelectedTerritory >= 0 && (uint)_config.SelectedTerritory == territoryId;
        bool spoilered = AttunementService.IsZoneSpoilered(_config, territoryId);
        bool empty     = tally.Total == 0 || spoilered;   // counter would leak "something is here"

        // Not "the actual name, dimmed" — the name itself is the spoiler. Still selectable; the
        // detail pane masks the challenges inside the same way (see ChallengeRow). Routed through
        // ZoneIndex.DisplayName rather than duplicating the check, so every masking decision in
        // the plugin comes from one place.
        string displayName = ZoneIndex.DisplayName(_config, territoryId);

        string rowId = $"zone_{territoryId}";

        var row = new Node().WithId(rowId).WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH_Zone;
            s.BackgroundColor = selected ? Accent.WithOpacity(0.09f) : PColor.Transparent;

            // Selected rows keep their accent wash rather than picking up the white one —
            // hovering the row you are already on should not restyle it (§7.2).
            if (!selected) s.HoverBackgroundColor = PColor.White.WithOpacity(0.03f);
        });

        // Not a hover cue — the renderer paints that now — but the answer to "which zone is under
        // the cursor" for right-click-to-teleport and its tooltip, neither of which Panache's own
        // events cover from here. Paired with a leave handler: enter fires only on the transition,
        // so an enter-only handler would hold the last row touched forever and let a right-click
        // on empty space teleport you somewhere you were no longer pointing at.
        row.OnMouseEnter += _ => _hoverNext = rowId;
        row.OnMouseLeave += _ => { if (_hoverNext == rowId) _hoverNext = null; };
        row.OnClick      += _ => { _config.SelectedTerritory = (int)territoryId; _save(); };

        row.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width = SelBarW;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = selected ? Accent : PColor.Transparent;
            s.PointerEvents   = PointerEvents.None;
        }));

        // Indented under its expansion header so the outline reads as an outline.
        row.AppendChild(new Node().WithText(displayName).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(6, 0, 0, 0);
            s.Padding       = new EdgeSize(0, 6, 0, 18);
            s.FontSize      = 11f;
            s.Bold          = selected;
            s.Italic        = spoilered;
            s.Color         = selected   ? TextHi
                            : spoilered  ? Theme.TextSubtle.WithOpacity(0.70f)
                            : empty      ? Theme.TextSubtle.WithOpacity(0.55f)
                                         : Theme.TextMuted;
            s.TextOverflow  = TextOverflow.Ellipsis;
            s.PointerEvents = PointerEvents.None;
        }));

        // Empty zones show no counter at all. "0/0" on 140 rows is noise, and the dimmed name
        // already says everything the counter would.
        row.AppendChild(new Node().WithText(empty ? string.Empty : $"{tally.Done}/{tally.Total}").WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fixed; s.Width = CounterW;
            s.HeightMode    = SizeMode.Fit;
            s.Margin        = new EdgeSize(6, 0, 0, 0);
            s.Padding       = new EdgeSize(0, PadPaneX, 0, 0);
            s.FontSize      = 9.5f;
            s.TextAlign     = TextAlign.Right;
            s.Color         = tally.Total > 0 && tally.Done == tally.Total ? StatusOk : Theme.TextSubtle;
            s.PointerEvents = PointerEvents.None;
        }));

        return row;
    }

    /// <summary>
    /// Why the list is empty and what to do about it. Distinguishes "you have never synced" from
    /// "you synced and the catalogue really is empty", because those need different actions and
    /// conflating them makes a working plugin look broken.
    /// </summary>
    private string EmptyCatalogueHint()
    {
        bool neverSynced = _config.LastSyncUtc == DateTime.MinValue;

#if DEV_BUILD
        if (!_config.PublicPreview)
            return neverSynced
                ? "Press Sync to download the published catalogue, or open the Challenge Creator to author one."
                : $"Synced {CompletionStore.FormatDate(_config.LastSyncUtc)} — nothing published yet. "
                + "Use the Creator's Publish tab to add some.";
#endif

        return neverSynced
            ? "Press Sync above to download the challenge list."
            : $"Synced {CompletionStore.FormatDate(_config.LastSyncUtc)} — no challenges are published yet. "
            + "New ones are added over time; press Sync again later.";
    }

    private Node CategoryRow(int masterW, string category, bool selected)
    {
        var (done, total) = ChallengeCatalog.CategoryProgress(_config, _store, category);
        float frac = ChallengeCatalog.Percent(done, total);

        string rowId = $"cat_{category}";

        var row = new Node().WithId(rowId).WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = RowH_Master;
            // Selection: accent @ 9% fill. Hover on non-selected: white @ 3% (§6.1, §7.2).
            s.BackgroundColor = selected ? Accent.WithOpacity(0.09f) : PColor.Transparent;
            if (!selected) s.HoverBackgroundColor = PColor.White.WithOpacity(0.03f);
        });

        var captured = category;
        row.OnClick += _ => { _config.SelectedCategory = captured; _save(); };

        // 2px selection bar at full accent opacity.
        row.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width = SelBarW;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = selected ? Accent : PColor.Transparent;
            s.PointerEvents   = PointerEvents.None;
        }));

        var inner = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fill;
            s.Padding       = new EdgeSize(7, 10, 0, 10);
            s.Gap           = 5;
            s.PointerEvents = PointerEvents.None;
        });

        // A finished category earns a flourish to the left of its name. The "12/12" counter below
        // already states the fact, but a number has to be read and compared; the mark is just seen
        // while scanning the list.
        bool allDone = total > 0 && done == total;

        var nameRow = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Horizontal;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fixed; s.Height = CatNameRowH;
            s.Gap           = 6;
            // Centres the badge against the title instead of both hanging from the top. This is
            // the AlignItems case the challenge row cannot use: that row grows when a hint opens,
            // this one is a fixed band, so centring here means what it says.
            s.AlignItems    = AlignItems.Center;
            s.PointerEvents = PointerEvents.None;
        });

        if (allDone)
        {
            // No centring margin — AlignItems above does it, and correctly at any size. The 1px
            // nudge that used to be here was tuned for a 13px icon and silently wrong for any other.
            nameRow.AppendChild(PUI.Icon(Ico.CategoryDone, CatIconSz, StatusOk));
        }

        nameRow.AppendChild(new Node().WithText(category).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Bold         = true;
            s.Color        = selected ? TextHi : allDone ? StatusOk.WithOpacity(0.85f) : Theme.TextMuted;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        inner.AppendChild(nameRow);

        var metaRow = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
        });

        // Track width is real arithmetic: pane width, minus the selection bar, minus this
        // node's horizontal padding, minus the counter column and its gap.
        float trackW = Math.Max(8f, masterW - SelBarW - 20f - CounterW - 8f);
        metaRow.AppendChild(ProgressBar(trackW, frac, StatusOk, topMargin: 5f));

        metaRow.AppendChild(new Node().WithText($"{done}/{total}").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fixed; s.Width = CounterW;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 10f;
            s.TextAlign  = TextAlign.Right;
            s.Color      = done == total && total > 0 ? StatusOk : Theme.TextSubtle;
        }));

        inner.AppendChild(metaRow);
        row.AppendChild(inner);
        return row;
    }

    // ── Menu bar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One entry in a dropdown. Sorted by <see cref="Label"/> before drawing.
    ///
    /// <para><see cref="IconId"/> may be <see cref="Ico.None"/>, which reserves the icon column
    /// without drawing anything. Every item pays for the column whether or not it uses it, so
    /// labels stay on one left edge — a dropdown where some rows are indented and others are not
    /// reads as a rendering fault rather than as "that one has no icon yet".</para>
    /// </summary>
    private readonly record struct MenuItem(string Label, Action OnClick, PColor Color, int IconId);

    /// <summary>A menu title and what drops out of it.</summary>
    private sealed record MenuDef(string Title, List<MenuItem> Items);

    /// <summary>
    /// The menus, built fresh each frame so their contents track live state (Sync shows
    /// "Syncing…", Restore only appears when there is something to restore).
    ///
    /// <para>Items are sorted alphabetically inside each menu — a stable position beats a
    /// hand-tuned order nobody can predict once a menu has more than about four entries.</para>
    /// </summary>
    /// <summary>
    /// One sort choice, ticked when it is the active one.
    ///
    /// <para>Choosing either plain order also records it as the Difficulty tiebreaker. That is
    /// what makes switching to Difficulty read as a refinement of what you were already looking
    /// at rather than a reshuffle: within each star band the list keeps the order it just had.
    /// Difficulty deliberately does not record itself — it cannot be its own tiebreaker.</para>
    /// </summary>
    private MenuItem SortItem(string label, ChallengeSort mode)
    {
        bool active = _config.SortMode == mode;

        return new MenuItem(label, () =>
        {
            _config.SortMode = mode;
            if (mode != ChallengeSort.Difficulty) _config.SecondarySort = mode;
            _save();
        }, active ? StatusOk : TextHi, active ? Ico.Complete : Ico.None);
    }

    private List<MenuDef> BuildMenus()
    {
        var menus = new List<MenuDef>();

        // ── View ─────────────────────────────────────────────────────────────
        var view = new List<MenuItem>
        {
            new("Appearance…", () => _dialogs.RequestAppearance(), TextHi, Ico.Appearance),
            new("Switch to plain renderer", () =>
            {
                _config.UsePanacheUI = false;
                _save();
                Diag.Info("[Panache] renderer switched OFF by user.");
            }, TextHi, Ico.Renderer),
        };
        menus.Add(new MenuDef("View", view));

        // ── Update ───────────────────────────────────────────────────────────
        var update = new List<MenuItem>
        {
            new(_sync.IsRunning ? "Syncing…" : "Sync now", () =>
            {
                if (_sync.IsRunning) return;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var r = await _sync.SyncAsync();
                    Plugin.ChatGui.Print("[Challenges] " + r.Message);
                    _tracker.Invalidate();
                });
            }, Accent, Ico.Sync),
        };

        // Only offered when the permanent ledger actually holds something the current data is
        // missing — a menu item that can do nothing is worse than no item.
        int recoverable = _store.PermanentCount - _store.CurrentCount;
        if (recoverable > 0)
            update.Add(new MenuItem($"Restore {recoverable} completion(s)", RestoreFromPermanent,
                                    StatusOk, Ico.Restore));

        menus.Add(new MenuDef("Update", update));

        // ── Settings ─────────────────────────────────────────────────────────
        menus.Add(new MenuDef("Settings", new List<MenuItem>
        {
            SortItem("Sort: Creation order", ChallengeSort.Created),
            SortItem("Sort: A → Z",          ChallengeSort.Alphabetical),
            SortItem("Sort: Difficulty",     ChallengeSort.Difficulty),
            new("UI Scale…", () => _dialogs.RequestUiScale(), TextHi, Ico.Scale),
        }));

#if DEV_BUILD
        // Public-preview mode hides every developer affordance so the dev plugin renders exactly
        // as the public build does — including this whole menu. /tchallenges preview comes back.
        if (!_config.PublicPreview)
        {
            // Dev items carry no icons by Trist's call — the developer surface does not need to
            // look finished. Ico.None still reserves the column so this menu lines up with the
            // public ones sitting beside it in the same bar.
            menus.Add(new MenuDef("Developer", new List<MenuItem>
            {
                new("Challenge Creator", () => OnOpenCreator?.Invoke(), Accent, Ico.Creator),
                new("Preview public build", () =>
                {
                    _config.PublicPreview = true;
                    _save();
                    Plugin.ChatGui.Print("[Challenges] Public preview ON — /tchallenges preview to exit.");
                }, Neutral, Ico.None),
                new("Sound test", () => OnOpenSoundTest?.Invoke(), Accent, Ico.None),
            }));
        }
#endif

        // ── Help ─────────────────────────────────────────────────────────────
        var help = new List<MenuItem>
        {
            new("Info", () => OnOpenStatus?.Invoke(), TextHi, Ico.Info),
            new("Reset progress…", _dialogs.RequestReset, Danger, Ico.Reset),
        };

        // Only when an endpoint was baked in at build time.
        if (SuggestionService.IsConfigured)
        {
            // No bug icon exists in the bundled set yet — Ico.None keeps this row's label aligned
            // with "Suggest a feature…" directly under it rather than shifting it left.
            help.Add(new MenuItem("Report a bug…", _dialogs.RequestBugReport, Danger, Ico.None));
            help.Add(new MenuItem("Suggest a feature…", _dialogs.RequestSuggestion, StatusOk, Ico.Suggest));
        }

        menus.Add(new MenuDef("Help", help));

        foreach (var m in menus)
            m.Items.Sort(static (a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));

        return menus;
    }

    /// <summary>
    /// The clickable menu titles. Plain text, no outlines — the open one is marked by colour and
    /// a background wash, exactly as a desktop menu bar does.
    /// </summary>
    private Node BuildMenuBar()
    {
        var bar = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fixed; s.Height = MenuBarH;
            s.Margin     = new EdgeSize(3, 0, 0, 0);
            s.Gap        = 2;
        });

        foreach (var menu in BuildMenus())
        {
            string id   = "menu_" + menu.Title;
            bool   open = _openMenu == menu.Title;

            var title = new Node().WithId(id).WithText(menu.Title).WithStyle(s =>
            {
                s.WidthMode       = SizeMode.Fit;
                s.HeightMode      = SizeMode.Fill;
                s.Padding         = new EdgeSize(0, MenuTitlePadX);
                s.FontSize        = MenuTitleFontSize;
                s.Bold            = open;
                s.TextAlign       = TextAlign.Center;
                s.BorderRadius    = 4f;

                // An open title is already at the hover appearance and beyond, so it declares no
                // hover colours at all — otherwise moving the pointer onto the menu you just
                // opened would visibly wash it a second time.
                s.Color           = open ? Accent : TextHi;
                s.BackgroundColor = open ? Accent.WithOpacity(0.16f) : PColor.Transparent;

                if (!open)
                {
                    s.HoverColor           = Accent;
                    s.HoverBackgroundColor = PColor.White.WithOpacity(0.05f);
                }
            });

            var captured = menu.Title;
            title.OnMouseEnter += _ =>
            {
                // Behaviour, not appearance: hovering while a menu is already open switches to
                // this one, which is how every desktop menu bar behaves. Hovering with none open
                // does nothing. The visual cue is the renderer's job above.
                if (_openMenu != null) _openMenuNext = captured;
            };
            title.OnClick += _ => _openMenuNext = _openMenu == captured ? null : captured;

            bar.AppendChild(title);
        }

        return bar;
    }

    /// <summary>
    /// The open dropdown, plus a full-window scrim that closes it.
    ///
    /// <para>Both are absolutely positioned and appended to the ROOT, last. Anywhere else and the
    /// panes' <c>ClipContent</c> would cut the dropdown off at the header boundary; appending
    /// last puts it above everything in draw order.</para>
    /// </summary>
    private void AppendMenuOverlay(Node root, int w, int h)
    {
        if (_openMenu == null) return;

        var menus = BuildMenus();

        // Left edge of the open title, accumulated across the titles before it. Measured exactly
        // now (see MenuTitleWidth) rather than estimated, so the dropdown lines up with its title
        // instead of landing a few pixels off. The +2 is the bar's Gap — see BuildMenuBar.
        //
        // Every title counted here is by definition NOT the open one (the loop stops at it), so
        // none of them are bold.
        float x = MenuBarLeft;
        MenuDef? target = null;
        foreach (var menu in menus)
        {
            if (menu.Title == _openMenu) { target = menu; break; }
            x += MenuTitleWidth(menu.Title, open: false) + 2f;
        }

        if (target == null)
        {
            // The open menu no longer exists — Developer disappears the moment public preview is
            // switched on, and it can be switched on FROM that menu.
            _openMenuNext = null;
            return;
        }

        // Scrim first, so it sits under the dropdown but over the window. Clicking anywhere that
        // is not an item dismisses, which is the behaviour every menu has.
        var scrim = new Node().WithId("menu_scrim").WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = 0;
            s.Top             = 0;
            s.WidthMode       = SizeMode.Fixed; s.Width  = w;
            s.HeightMode      = SizeMode.Fixed; s.Height = h;
            s.BackgroundColor = PColor.Transparent;
        });
        scrim.OnClick += _ => _openMenuNext = null;
        root.AppendChild(scrim);

        float panelW = MenuPanelMinW;
        foreach (var item in target.Items)
            panelW = MathF.Max(panelW, MenuItemWidth(item.Label));

        // Keep the panel on screen when a menu near the right edge is opened.
        float left = MathF.Min(x, MathF.Max(0f, w - panelW - 4f));
        float top  = MenuBarTop + MenuBarH + 2f;

        var panel = new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = left;
            s.Top             = top;
            s.Flow            = Flow.Vertical;
            s.WidthMode       = SizeMode.Fixed; s.Width = panelW;
            s.HeightMode      = SizeMode.Fit;
            s.Padding         = new EdgeSize(4, 0);
            s.BackgroundColor = Theme.Panel2;
            s.BorderColor     = Accent.WithOpacity(0.35f);
            s.BorderWidth     = 1;
            s.BorderRadius    = 5f;
            s.ShadowBlur      = 12f;
            s.ShadowColor     = PColor.Black.WithOpacity(0.55f);
        });

        foreach (var item in target.Items)
        {
            string itemId = $"mi_{target.Title}_{item.Label}";

            // The row is a horizontal container now rather than a text node, so the label moved
            // into a child of its own. That child keeps HeightMode.Fill because the renderer
            // centres text inside whatever box it is handed — a Fit-height label would sit on the
            // top edge of the row instead of level with its icon.
            //
            // The highlight bar is the entire hover cue. The label and icon used to flip to TextHi
            // as well, which threw away the only signal that "Reset progress" is destructive and
            // "Sync" is not — item.Color is semantic, and hover is not a reason to discard it.
            var row = new Node().WithId(itemId).WithStyle(s =>
            {
                s.Flow                 = Flow.Horizontal;
                s.WidthMode            = SizeMode.Fill;
                s.HeightMode           = SizeMode.Fixed; s.Height = MenuItemH;
                s.Padding              = new EdgeSize(0, MenuItemPadX);
                s.Gap                  = MenuIconGap;
                s.AlignItems           = AlignItems.Center;
                s.BackgroundColor      = PColor.Transparent;
                s.HoverBackgroundColor = Accent.WithOpacity(0.22f);
            });

            if (item.IconId != Ico.None)
            {
                // No centring margin: AlignItems.Center above does it, and unlike a
                // (MenuItemH - MenuIconSz) / 2 margin it stays correct if either size changes.
                row.AppendChild(PUI.Icon(item.IconId, MenuIconSz, item.Color));
            }
            else
            {
                // Empty spacer of exactly the icon's footprint — see MenuItem.IconId for why an
                // unillustrated row still pays for the column.
                row.AppendChild(new Node().WithStyle(s =>
                {
                    s.WidthMode     = SizeMode.Fixed; s.Width  = MenuIconSz;
                    s.HeightMode    = SizeMode.Fixed; s.Height = MenuIconSz;
                    s.PointerEvents = PointerEvents.None;
                }));
            }

            row.AppendChild(new Node().WithText(item.Label).WithStyle(s =>
            {
                s.WidthMode     = SizeMode.Fill;
                s.HeightMode    = SizeMode.Fill;
                s.FontSize      = MenuItemFontSize;
                s.Color         = item.Color;
                s.TextOverflow  = TextOverflow.Ellipsis;
                s.PointerEvents = PointerEvents.None;
            }));

            var action = item.OnClick;
            row.OnClick += _ =>
            {
                _openMenuNext = null;
                action();
            };

            panel.AppendChild(row);
        }

        root.AppendChild(panel);
    }

#if DEV_BUILD
    /// <summary>
    /// Right-click menu on a challenge title. Developer builds only.
    ///
    /// <para>This replaced an always-visible GUID pill on every row. The pill worked, but it put
    /// authoring plumbing permanently in the middle of a list meant for reading challenges — a
    /// right-click menu costs nothing until it is wanted.</para>
    ///
    /// <para>Deliberately mirrors the menu-bar dropdown rather than introducing a second popup
    /// idiom: absolutely positioned against the ROOT, appended last so it escapes the panes'
    /// clipping.</para>
    /// </summary>
    private void BuildChallengeContextMenu(Node root)
    {
        if (_ctxChallengeId == null || _config.PublicPreview) return;

        string id = _ctxChallengeId;
        const float w = 150f;

        var panel = new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = MathF.Max(0f, _ctxAt.X);
            s.Top             = MathF.Max(0f, _ctxAt.Y);
            s.Flow            = Flow.Vertical;
            s.WidthMode       = SizeMode.Fixed; s.Width = w;
            s.HeightMode      = SizeMode.Fit;
            s.Padding         = new EdgeSize(4, 0);
            s.BackgroundColor = Theme.Panel2;
            s.BorderColor     = Accent.WithOpacity(0.35f);
            s.BorderWidth     = 1;
            s.BorderRadius    = 5f;
            s.ShadowBlur      = 12f;
            s.ShadowColor     = PColor.Black.WithOpacity(0.55f);
        });

        string itemId = "ctxcopy:" + id;

        var item = new Node().WithId(itemId).WithText("Copy GUID").WithStyle(s =>
        {
            s.WidthMode            = SizeMode.Fill;
            s.HeightMode           = SizeMode.Fixed; s.Height = MenuItemH;
            s.Padding              = new EdgeSize(0, MenuItemPadX);
            s.FontSize             = MenuItemFontSize;
            s.Color                = Theme.TextMuted;
            s.HoverColor           = TextHi;
            s.BackgroundColor      = PColor.Transparent;
            s.HoverBackgroundColor = Accent.WithOpacity(0.22f);
        });

        item.OnClick += _ =>
        {
            ImGui.SetClipboardText(id);
            Plugin.ChatGui.Print($"[Challenges] Copied GUID {id}");
            _ctxChallengeId = null;
        };

        panel.AppendChild(item);
        root.AppendChild(panel);
    }
#endif

    // Measured, not estimated. These were `label.Length * 6.9f` and `label.Length * 6.6f` — a
    // mean glyph width that was wrong for every label that was not average, and wrong in the same
    // direction for all of them at once whenever the font size changed. PUI.MeasureText asks the
    // same SKFont the renderer will paint with, so the dropdown now lands under its title exactly
    // and the panel is exactly as wide as its longest item.
    //
    // Bold matters: an open menu title is bold (see BuildMenuBar), and bold is wider. Measuring
    // the regular weight would drift every title after an open one leftward.

    /// <summary>Width of a menu-bar title, at the weight it will actually be drawn in.</summary>
    private float MenuTitleWidth(string label, bool open) =>
        MenuTitlePadX * 2f + PUI.MeasureText(label, MenuTitleFontSize, bold: open);

    /// <summary>Width a dropdown item needs: padding, icon column, gap, then the label itself.</summary>
    private float MenuItemWidth(string label) =>
        MenuItemPadX * 2f + MenuIconSz + MenuIconGap + PUI.MeasureText(label, MenuItemFontSize);

    // ── Detail pane ──────────────────────────────────────────────────────────

    /// <summary>
    /// The right-hand pane. Grouping-agnostic on purpose: it is handed a title and a list, so a
    /// category and a zone render through exactly the same code and cannot drift apart.
    /// </summary>
    private Node BuildDetail(int detailW, string title, List<ChallengeDef> list,
                             (string Head, string Body) emptyCopy)
    {
        var pane = new Node().WithStyle(s =>
        {
            s.Flow            = Flow.Vertical;
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = Surface(Theme.Base);
        });

        if (string.IsNullOrEmpty(title))
        {
            pane.AppendChild(EmptyNote(emptyCopy.Head, emptyCopy.Body));
            return pane;
        }

        int done = 0;
        foreach (var d in list) if (_store.IsComplete(d.Id)) done++;

        int   total = list.Count;
        float frac  = ChallengeCatalog.Percent(done, total);
        bool  all   = total > 0 && done == total;
        string category = title;

        // Detail header — category name + this category's own x-of-y and percent.
        var head = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fixed; s.Height = DetailHeaderH;
            s.Padding       = new EdgeSize(PadPaneY, PadPaneX);
            s.Gap           = 6;
            s.PointerEvents = PointerEvents.None;
        });

        head.AppendChild(new Node().WithText(category).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 16f;
            s.Bold         = true;
            s.Color        = Accent;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        string scopeWord = _config.Grouping == GroupMode.Zones ? "zone" : "category";
        head.AppendChild(new Node().WithText($"{done} of {total} done  ·  {frac * 100f:0}% of this {scopeWord}").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 11.5f;
            s.Bold       = true;
            s.Color      = all ? StatusOk : Theme.TextMuted;
        }));

        head.AppendChild(ProgressBar(Math.Max(8f, detailW - PadPaneX * 2f), frac, StatusOk));

        pane.AppendChild(head);
        pane.AppendChild(Hairline(PColor.White.WithOpacity(0.06f)));

        // Id carries the focus nonce so a reveal restarts the scroll container at the top.
        var scroll = new Node().WithId($"chal_scroll_{_focusNonce}").WithStyle(s =>
        {
            s.Flow        = Flow.Vertical;
            s.WidthMode   = SizeMode.Fill;
            s.HeightMode  = SizeMode.Fill;
            s.OverflowY   = OverflowMode.Scroll;
            s.ClipContent = true;
            s.Gap         = 0;
        });

        if (list.Count == 0)
        {
            scroll.AppendChild(EmptyNote(emptyCopy.Head, emptyCopy.Body));
        }
        else
        {
            for (int i = 0; i < list.Count; i++)
            {
                // Numbered against THIS list, not the catalog. Whatever the pane is showing — a
                // category, a zone, a filtered set — the rows read 1, 2, 3 from the top. A number
                // carried in from the full catalog would start a category at #7 and skip.
                scroll.AppendChild(ChallengeRow(list[i] with { Number = i + 1 }, detailW));
                if (i < list.Count - 1)
                    scroll.AppendChild(Hairline(PColor.White.WithOpacity(0.05f)));
            }
        }

        pane.AppendChild(scroll);
        return pane;
    }

    /// <summary>
    /// One challenge. The ROW is deliberately not clickable: completion is written only by
    /// <see cref="ChallengeTracker"/> when the conditions are actually met, so the row body
    /// renders no hover cue — a hover on something you cannot click is a false affordance
    /// (DESIGN_SYSTEM §1.4). The Hint pill inside it is the one interactive element.
    ///
    /// <para>The row therefore CANNOT set <c>PointerEvents.None</c>: InteractionManager treats it
    /// as blocking the node and every descendant, which would make the Hint pill unclickable.
    /// Nothing else in the row subscribes to an event, so leaving it Auto costs nothing.</para>
    /// </summary>
    private Node ChallengeRow(ChallengeDef def, int detailW)
    {
        bool done    = _store.IsComplete(def.Id);
        bool focused = IsFocused(def.Id);

        // Completing a challenge means you were there — it stops being a spoiler the moment you
        // earn it, which is also why this is keyed off `done` rather than off attunement directly
        // (a WholeZone GearInArea challenge can complete without ever standing near an aetheryte).
        // Dev builds bypass this entirely outside public-preview: Trist authoring a zone's content
        // must not have that zone spoilered to himself.
        bool spoilered = !done && !DevBypassesSpoilers
                       && AttunementService.IsZoneSpoilered(_config, ZoneIndex.TerritoryOf(_config, def.Id));

        // A revealed hint replaces the description and is usually longer than it, so the row
        // grows to fit instead of ellipsising away the thing the player just asked to read. Never
        // for a spoilered challenge — the hint exists to help FIND something, so offering it here
        // would leak exactly what the mask is hiding.
        bool hintOpen = !spoilered && def.HasHint && _hintShown.Contains(def.Id);

        // Fit height over a MinHeight floor, rather than a height this method computes. A hint
        // that wraps to three lines makes the text column taller, the column makes the row
        // taller, and layout works out the wrap width from the siblings that are really there.
        // The floor is what keeps every unexpanded row the uniform RowH_Challenge the list is
        // designed around — Fit alone would shrink rows to their text.
        var row = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Horizontal;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit; s.MinHeight = RowH_Challenge;
            s.Padding       = new EdgeSize(0, PadPaneX);
            s.Gap           = 10;

            // A revealed row is tinted and outlined for a few seconds so the eye lands on it.
            // This marks the row; it does not make the row itself clickable.
            if (focused)
            {
                s.BackgroundColor = Accent.WithOpacity(0.14f);
                s.BorderColor     = Accent.WithOpacity(0.55f);
                s.BorderWidth     = 1;
                s.BorderRadius    = 6f;
            }
        });

        // Completion checkbox, vertically centred by margin.
        //
        // This row is the ONE place that deliberately keeps a hand-computed centring margin
        // instead of AlignItems.Center — do not "finish the job" here. AlignItems centres against
        // the row's real height, and this row grows when a hint is revealed; every marker would
        // then drift down to the middle of a three-line block. RowH_Challenge is the unexpanded
        // height, so pinning to it keeps the marks level with the TITLE line whatever the row
        // does underneath, which is the behaviour that was wanted. Same reasoning for the pin and
        // the difficulty meter below.
        //
        // A checkbox rather than the dot this replaced. The dot encoded done/not-done purely as a
        // colour change, which is the one distinction a colour-blind player is least likely to
        // catch; a tick versus an empty box carries the same fact in shape.
        var mark = PUI.Icon(done ? Ico.Complete : Ico.Incomplete, StatusIconSz,
                             done ? StatusOk : Accent.WithOpacity(0.45f));
        mark.WithStyle(s => s.Margin = new EdgeSize((RowH_Challenge - StatusIconSz) / 2f, 0, 0, 0));
        row.AppendChild(mark);

        var textCol = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Margin     = new EdgeSize(8, 0, 0, 0);
            s.Gap        = 2;
        });

        // The number is not spoiler information — only ordering — so it survives the mask.
        string title = spoilered ? "??? Challenge"
                      : string.IsNullOrWhiteSpace(def.Title) ? "(unnamed challenge)" : def.Title;
        if (def.Number > 0) title = $"#{def.Number}  {title}";

        // Id'd so the right-click handler can tell WHICH challenge the pointer is over. Carries no
        // click handler and no hover cue — it is not a button, and DESIGN_SYSTEM §1.4 forbids
        // advertising an affordance that a left click does not have.
        var titleNode = new Node().WithId("chal:" + def.Id).WithText(title).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Bold         = true;
            s.Italic       = spoilered;
            s.Color        = done ? StatusOk.WithOpacity(0.95f) : spoilered ? Theme.TextSubtle : TextHi;
            s.TextOverflow = TextOverflow.Ellipsis;
        });

#if DEV_BUILD
        // Hover is tracked ONLY to answer "what is the pointer over?" for the right-click menu, so
        // it is wired in dev builds alone. No visual change: the style declares no Hover* colours,
        // which keeps a non-clickable title from growing a cue it has not earned. Enter/leave
        // pair for the same reason ZoneRow uses one — enter alone never clears.
        if (!_config.PublicPreview)
        {
            string hoverKey = "chal:" + def.Id;
            titleNode.OnMouseEnter += _ => _hoverNext = hoverKey;
            titleNode.OnMouseLeave += _ => { if (_hoverNext == hoverKey) _hoverNext = null; };
        }
#endif

        textCol.AppendChild(titleNode);

        // Second line: the description, or — in dev builds — the reason this challenge can
        // never fire, which is far more useful than a blank line.
        string sub      = def.Detail;
        PColor subColor = Theme.TextMuted;

        // Once complete, WHEN it was done is the interesting fact — not the description you have
        // already satisfied.
        if (done)
        {
            var when = _store.CompletedAt(def.Id);
            if (when.HasValue)
            {
                sub      = $"Complete on {CompletionStore.FormatDate(when.Value)} !";
                subColor = StatusOk.WithOpacity(0.85f);
            }
        }

#if DEV_BUILD
        if (!_config.PublicPreview)
        {
            if (!def.HasDetails)
            {
                sub      = "Missing details — a name and a description are both required.";
                subColor = Danger;
            }
            else if (!def.HasDetector)
            {
                sub      = $"{def.Detail}   ·   no detector: nothing can complete this";
                subColor = Neutral;
            }
        }
#endif

        // Overrides everything above, including the dev-only flags — a "Missing details" or
        // "no detector" line still leaks def.Detail's content, which a spoilered row must not.
        if (spoilered)
        {
            sub      = "Explore this zone to reveal this challenge.";
            subColor = Theme.TextSubtle.WithOpacity(0.85f);
        }

        // The hint wins over everything above, including the completion date and the dev flags:
        // it is only ever showing because the player explicitly asked for it, and an explicit
        // request should not be silently overridden by an automatic line.
        if (hintOpen)
        {
            // The one node in this window that wraps. Fit height reports the wrapped height, which
            // is what grows the row — the prefix is part of the same string so it cannot push the
            // first line over on its own.
            textCol.AppendChild(new Node().WithText("Hint: " + def.Hint.Trim()).WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = SubFontSize;
                s.Color        = HintText;
                s.TextOverflow = TextOverflow.Wrap;
                s.MaxLines     = HintMaxLines;
            }));
        }
        else
        {
            textCol.AppendChild(new Node().WithText(sub).WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = SubFontSize;
                s.Color        = subColor;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));
        }

        row.AppendChild(textCol);

        // "You are standing in this challenge's zone right now." Cheap to compute and the single
        // most actionable fact a row can carry — it turns a long list into "these ones, today".
        //
        // Never on a spoilered row: which zone a hidden challenge belongs to is precisely what the
        // mask exists to withhold, and a marker that lights up only in the right place would leak
        // it one zone at a time.
        if (!spoilered)
        {
            uint here = (uint)Plugin.ClientState.TerritoryType;
            if (here != 0 && ZoneIndex.TerritoryOf(_config, def.Id) == here)
            {
                var pin = PUI.Icon(Ico.HereNow, HereIconSz, Accent);
                pin.WithStyle(s => s.Margin = new EdgeSize((RowH_Challenge - HereIconSz) / 2f, 0, 0, 0));
                row.AppendChild(pin);
            }
        }

        // Difficulty meter. Hidden entirely on a spoilered row — how hard something is is a
        // strong hint about what it involves, which is the shape of thing the mask withholds.
        if (def.HasDifficulty && !spoilered)
            row.AppendChild(StarRow(def.Difficulty));

        // Locally authored challenges are badged, always and in every build, so an official
        // challenge and a homemade one are never confused.
        if (!def.IsOfficial)
        {
            var custom = StaticPill("CUSTOM", Neutral);
            custom.WithStyle(s => s.Margin = new EdgeSize(11, 0, 0, 0));
            row.AppendChild(custom);
        }

        row.AppendChild(HintPillFor(def, hintOpen, spoilered));
        row.AppendChild(StatusPillFor(def, done, spoilered));
        return row;
    }

    /// <summary>
    /// Every challenge carries a hint control. When a hint exists it toggles the description line
    /// out for the hint; when none was authored the control is a dead label saying so, NOT a live
    /// button — a button that reveals nothing is worse than an honest "NO HINT". A spoilered
    /// challenge gets neither — the hint itself is the exact thing being withheld.
    /// </summary>
    private Node HintPillFor(ChallengeDef def, bool open, bool spoilered)
    {
        if (spoilered)
        {
            // Still a word, not a glyph. "???" is the mask itself speaking, and a dimmed
            // question-mark icon would be near-indistinguishable from the live Hint button
            // one row down — the two mean opposite things and must not rhyme.
            var masked = StaticPill("???", Neutral);
            masked.WithStyle(s => s.Opacity = 0.55f);
            masked.WithStyle(s => s.Margin  = new EdgeSize(11, 0, 0, 0));
            return masked;
        }

        float top = (RowH_Challenge - HintBtn) / 2f;

        if (!def.HasHint)
        {
            // A dead affordance made obviously dead: same glyph, but no button chrome and heavily
            // dimmed. The old "NO HINT" pill said this in words; the point survives the switch
            // because what makes it honest is the absence of a button, not the label
            // (DESIGN_SYSTEM §1.4 — never a hover cue on something that cannot be clicked).
            var none = PUI.Icon(Ico.Hint, HintGlyph, Neutral.WithOpacity(0.30f));
            none.WithStyle(s => s.Margin = new EdgeSize(top + (HintBtn - HintGlyph) / 2f, 0, 0, 0));
            return none;
        }

        // Lit while the hint is open — that is the "HIDE HINT" label's job, done with fill instead
        // of words. The row underneath visibly changes at the same time, so there is no ambiguity
        // about what the lit state means.
        string id = "hint:" + def.Id;
        var btn = IconButton(id, Ico.Hint, HintBtn, HintGlyph, HintAccent, open, () =>
        {
            if (!_hintShown.Remove(def.Id)) _hintShown.Add(def.Id);
        });
        btn.WithStyle(s => s.Margin = new EdgeSize(top, 0, 0, 0));
        return btn;
    }


    /// <summary>
    /// Right-hand status pill: DONE, live step progress for multi-area challenges
    /// (e.g. "2/4"), or the tracking state.
    /// </summary>
    private Node StatusPillFor(ChallengeDef def, bool done, bool spoilered)
    {
        string text;
        PColor color;

        if (done)
        {
            text  = "DONE";
            color = StatusOk;
        }
        else if (spoilered)
        {
            // Not TRACKING, not a step count — either would confirm "yes, something is here and
            // it has N steps", which is exactly the shape of information the mask exists to hide.
            text  = "???";
            color = Neutral;
        }
        else
        {
#if DEV_BUILD
            if (!def.HasDetails)
            {
                text  = "MISSING";
                color = Danger;
                var missing = StaticPill(text, color);
                missing.WithStyle(s => s.Margin = new EdgeSize(11, 0, 0, 0));
                return missing;
            }
#endif
            // NOT gated on def.IsCustom. Once challenges became syncable, an official challenge
            // returned null here and fell through to "TRACKING" with no count — which is exactly
            // the case that matters, since published challenges are the ones players have.
            // FindCustom already searches official first, then local.
            var custom = ChallengeCatalog.FindCustom(_config, def.Id);

            if (custom != null && custom.ShowProgress
                && _tracker.TryGetProgress(custom, out int step, out int total) && total > 0)
            {
                text  = $"{step}/{total}";
                color = step > 0 ? Accent : Neutral;
            }
            else if (!def.HasDetector)
            {
                text  = "—";
                color = Neutral;
            }
            else
            {
                text  = "TRACKING";
                color = Accent;
            }
        }

        var pill = StaticPill(text, color);
        pill.WithStyle(s => s.Margin = new EdgeSize(11, 0, 0, 0));
        return pill;
    }

    // ── Small builders ───────────────────────────────────────────────────────

    /// <summary>
    /// An interactive pill with the mandatory hover cue wired up — declared on the node now, so
    /// the renderer cross-fades it instead of this window repainting on a state field.
    /// </summary>
    private Node Pill(string id, string text, PColor accent, Action onClick)
    {
        var node = PUI.PillButton(id, text, accent);
        node.WithStyle(s => s.HoverBackgroundColor = accent.WithOpacity(0.32f));
        node.OnClick += _ => onClick();
        return node;
    }

    /// <summary>
    /// The difficulty meter: five slots, <paramref name="difficulty"/> of them filled.
    ///
    /// <para>Always five, never just the earned ones — "●●○○○" is read as a proportion at a
    /// glance, where "●●" alone has to be counted and then compared against a maximum the row
    /// never states.</para>
    /// </summary>
    private Node StarRow(int difficulty)
    {
        var row = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Horizontal;
            s.WidthMode     = SizeMode.Fit;
            s.HeightMode    = SizeMode.Fit;
            s.Gap           = StarGap;
            s.Margin        = new EdgeSize((RowH_Challenge - StarSz) / 2f, 0, 0, 0);
            s.PointerEvents = PointerEvents.None;
        });

        for (int i = 1; i <= 5; i++)
        {
            bool lit = i <= difficulty;
            row.AppendChild(PUI.Icon(lit ? Ico.StarFull : Ico.StarEmpty, StarSz,
                                      lit ? Accent : Neutral.WithOpacity(0.35f)));
        }

        return row;
    }


    /// <summary>
    /// A small square toggle whose face is a bundled icon — the lock control. Filled when active
    /// rather than merely outlined, so "locked" reads as a solid state rather than a hover-style
    /// highlight.
    ///
    /// <para>Takes TWO icon IDs because here the state IS the icon. The control this replaced was
    /// a single letter "L" that looked identical locked and unlocked; a padlock that visibly opens
    /// and closes answers "which way is it set?" without the user having to click it to find out.</para>
    /// </summary>
    private Node IconToggle(string id, int iconActive, int iconInactive, bool active, Action onClick)
    {
        var node = new Node().WithId(id).WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = ChromeBtn;
            s.HeightMode      = SizeMode.Fixed; s.Height = ChromeBtn;
            s.Flow            = Flow.Horizontal;
            // Padding, not AlignItems: this has to centre on BOTH axes, and AlignItems only
            // handles the cross one. An even inset does both at once.
            s.Padding         = new EdgeSize((ChromeBtn - ChromeGlyph) / 2f);
            s.BorderRadius    = 5;
            s.BorderWidth     = 1;
            s.BackgroundColor = active ? Accent.WithOpacity(0.32f) : Neutral.WithOpacity(0.10f);
            s.BorderColor     = active ? Accent.WithOpacity(0.75f) : Neutral.WithOpacity(0.40f);

            s.HoverBackgroundColor = active ? Accent.WithOpacity(0.45f) : Neutral.WithOpacity(0.20f);
        });

        // Tinted by state, never by hover — which is what lets this control use the renderer's
        // hover entirely. A glyph inside a button is PointerEvents.None, so it is never itself
        // "hovered" and a HoverColor on it would never fire.
        node.AppendChild(PUI.Icon(active ? iconActive : iconInactive, ChromeGlyph,
            active ? PColor.Black.WithOpacity(0.85f) : Neutral.WithOpacity(0.85f)));

        node.OnClick += _ => onClick();
        return node;
    }

    /// <summary>
    /// The window's close control, sharing <see cref="ChromeBtn"/> / <see cref="ChromeGlyph"/> with
    /// the lock beside it so the two corner buttons are the same size with the same size glyph.
    ///
    /// <para>Deliberately NOT <see cref="PUI.CloseButton"/>, which is otherwise the right thing to
    /// use. It fixes its glyph at 52% of the button box, which at 22px draws an X far smaller than
    /// this corner needs, and that ratio lives in the shared framework — raising it there would
    /// resize the close button of every other plugin built on PanacheUI, which is not this
    /// plugin's call to make. Same box, same corner, same idea; only the ratio differs.</para>
    /// </summary>
    private Node CloseButton(string id, Action onClick)
    {
        var node = new Node().WithId(id).WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = ChromeBtn;
            s.HeightMode      = SizeMode.Fixed; s.Height = ChromeBtn;
            s.Flow            = Flow.Horizontal;
            s.Padding         = new EdgeSize((ChromeBtn - ChromeGlyph) / 2f);
            s.BorderRadius    = 5;
            s.BorderWidth     = 1;
            s.BackgroundColor = Danger.WithOpacity(0.12f);
            s.BorderColor     = Danger.WithOpacity(0.45f);

            s.HoverBackgroundColor = Danger.WithOpacity(0.35f);
            s.HoverBorderColor     = Danger.WithOpacity(0.85f);
        });

        // Was 0.85 rising to 1.0 on hover. The glyph is a PointerEvents.None child and so never
        // receives hover of its own; the box behind it triples its fill and nearly doubles its
        // border instead, which is a far louder cue than 15% on the X ever was.
        node.AppendChild(PUI.Icon(Ico.Close, ChromeGlyph, Danger.WithOpacity(0.95f)));

        node.OnClick += _ => onClick();
        return node;
    }

    /// <summary>
    /// A round icon-only button with the mandatory hover cue — the icon equivalent of
    /// <see cref="Pill"/>, for controls whose meaning a single glyph carries completely.
    /// </summary>
    private Node IconButton(string id, int iconId, float box, float glyph, PColor accent,
                            bool lit, Action onClick)
    {
        var node = new Node().WithId(id).WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = box;
            s.HeightMode      = SizeMode.Fixed; s.Height = box;
            s.Flow            = Flow.Horizontal;
            s.Padding         = new EdgeSize((box - glyph) / 2f);
            s.BorderRadius    = box / 2f;
            s.BorderWidth     = 1;
            s.BackgroundColor = accent.WithOpacity(lit ? 0.28f : 0.08f);
            s.BorderColor     = accent.WithOpacity(lit ? 0.75f : 0.40f);

            s.HoverBackgroundColor = accent.WithOpacity(lit ? 0.42f : 0.20f);
        });

        // Opacity now tracks `lit` alone. It used to be `lit || hot`, which made an unlit button
        // under the cursor indistinguishable from a lit one — the hover cue was impersonating the
        // state cue. The box's fill carries hover on its own.
        node.AppendChild(PUI.Icon(iconId, glyph, accent.WithOpacity(lit ? 1f : 0.85f)));

        node.OnClick += _ => onClick();
        return node;
    }

    /// <summary>
    /// A pill-shaped label. Explicitly NOT clickable and explicitly no hover cue — a hover on
    /// something you can't click is a false affordance (DESIGN_SYSTEM §1.4).
    /// </summary>
    private Node StaticPill(string text, PColor color) =>
        new Node().WithText(text).WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fit;
            s.HeightMode      = SizeMode.Fit;
            s.BackgroundColor = color.WithOpacity(0.12f);
            s.BorderRadius    = 9f;
            s.BorderColor     = color.WithOpacity(0.45f);
            s.BorderWidth     = 1;
            s.Padding         = new EdgeSize(4, 10);
            s.FontSize        = 10f;
            s.Bold            = true;
            s.Color           = color;
            s.PointerEvents   = PointerEvents.None;
        });

    /// <summary>3px progress bar, 2px radius (DESIGN_SYSTEM §7.5). Teal fill = progress toward a goal.</summary>
    private Node ProgressBar(float width, float frac, PColor fill, float topMargin = 0f)
    {
        float clamped = Math.Clamp(frac, 0f, 1f);

        var track = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = ProgressH;
            s.Margin          = new EdgeSize(topMargin, 0, 0, 0);
            s.BackgroundColor = PColor.White.WithOpacity(0.08f);
            s.BorderRadius    = 2f;
            s.PointerEvents   = PointerEvents.None;
        });

        // Absolutely positioned, so its width must be a real number rather than a Fill.
        track.AppendChild(new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = 0;
            s.Top             = 0;
            s.WidthMode       = SizeMode.Fixed; s.Width  = Math.Max(0f, width * clamped);
            s.HeightMode      = SizeMode.Fixed; s.Height = ProgressH;
            s.BackgroundColor = fill;
            s.BorderRadius    = 2f;
            s.PointerEvents   = PointerEvents.None;
        }));

        return track;
    }

    private static Node Hairline(PColor color) =>
        new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = 1;
            s.BackgroundColor = color;
            s.PointerEvents   = PointerEvents.None;
        });

    private Node InfoLine(string text) =>
        new Node().WithText(text).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.FontSize      = 11f;
            s.Color         = Theme.TextSubtle;
            s.TextOverflow  = TextOverflow.Ellipsis;
            s.PointerEvents = PointerEvents.None;
        });

    private Node EmptyNote(string reason, string nextStep)
    {
        var box = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Padding       = new EdgeSize(16, PadPaneX);
            s.Gap           = 5;
            s.PointerEvents = PointerEvents.None;
        });

        box.AppendChild(new Node().WithText(reason).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 12f;
            s.Bold       = true;
            s.Color      = Theme.TextMuted;
        }));

        box.AppendChild(new Node().WithText(nextStep).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 10.5f;
            s.Color      = Theme.TextSubtle;
        }));

        return box;
    }

}
