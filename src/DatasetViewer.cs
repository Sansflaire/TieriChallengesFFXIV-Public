#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Developer-only.</b> Browses the JSON reference datasets in <c>data/</c> as a searchable grid.
/// Compiled out of the public build entirely.
///
/// <para><b>Why it is generic.</b> All seven datasets share one envelope but have completely
/// different entry shapes, so columns are derived from the data at load time rather than declared
/// per dataset. Adding an eighth dataset needs no code here — drop the file in <c>data/</c>.</para>
///
/// <para><b>Why paging rather than a clipper.</b> <c>ImGuiListClipper</c> exists in the bindings
/// but its construction pattern there is unverified, and a dev tool is not worth an unverified
/// API in the draw loop. Paging plus search reaches every row with no such risk. If full
/// free-scroll is ever wanted, that is the one thing to revisit.</para>
///
/// <para><b>Memory.</b> <c>npcs.json</c> is ~34 MB on disk and costs several times that parsed.
/// Exactly one dataset is held at a time and <see cref="Unload"/> drops it — this runs inside the
/// game's process, so leaving 300 MB parked because a window is open would be rude.</para>
/// </summary>
internal sealed class DatasetViewer
{
    public bool IsVisible;

    /// <summary>Rows drawn per page. Paging is what makes every row reachable without a clipper.</summary>
    private const int PageSize = 500;

    // NOTE: columns are discovered over EVERY entry, not a sample. A 300-entry sample silently
    // dropped any field that first appears later - the curated duties fields (unlockQuest,
    // itemsFound, timeLimitMinutes) are added per-entry and are exactly that shape, so the data
    // would have been present in the file and invisible in the grid. The full scan is one
    // HashSet.Add per property, which is nothing next to parsing the file that produced them.

    /// <summary>
    /// Longest cell string DISPLAYED. Cells are stored in full and clipped only when drawn.
    ///
    /// <para>Storing them clipped was a real defect: the search blob was built from the clipped
    /// text, so an item late in a long comma-separated list simply could not be found. The whole
    /// point of packing every drop into one cell is that it is searchable.</para>
    /// </summary>
    private const int MaxCellLength = 160;

    /// <summary>
    /// Cap on the string handed to CalcTextSize when sizing a column. Anything this long already
    /// exceeds <see cref="MaxColumnWidth"/>, so measuring a 5,000-character item list would burn
    /// the time and arrive at the same clamp.
    /// </summary>
    private const int MaxMeasureLength = 200;

    /// <summary>
    /// Separates cells inside a row's search blob. ASCII Unit Separator: it cannot occur in the
    /// data, so a search term can never accidentally span two columns.
    /// </summary>
    private const char BlobSeparator = '\u001f';

    private static readonly Vector4 ColUnknown = new(1.00f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 ColWarn    = new(1.00f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 ColOk      = new(0.45f, 0.90f, 0.50f, 1f);
    private static readonly Vector4 ColDim     = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 ColNumeric = new(0.45f, 0.75f, 1.00f, 1f);

    // ── Catalogue ────────────────────────────────────────────────────────────

    private sealed class DatasetInfo
    {
        public string Path = string.Empty;
        public string FileName = string.Empty;
        public string Description = string.Empty;
        public string? NeedsVerification;
        public string[] UnknownFields = Array.Empty<string>();
        public int Count;
        public long Bytes;
        public string? HeaderError;
    }

    private List<DatasetInfo>? _catalogue;
    private string _catalogueError = string.Empty;

    // ── Loaded dataset ───────────────────────────────────────────────────────

    private DatasetInfo? _open;
    private string[] _columns = Array.Empty<string>();
    private List<string[]> _rows = new();
    private List<string> _blobs = new();
    private List<int> _filtered = new();

    private string _search = string.Empty;
    private string _appliedSearch = string.Empty;
    private int _page;

    /// <summary>
    /// How a column filter compares. The first two are substring text matches; the rest parse both
    /// the cell and the entered value as numbers.
    /// </summary>
    private enum FilterOp { Include, Exclude, Gt, Ge, Lt, Le, Eq }

    private static readonly string[] OpLabels =
        { "INCLUDE", "EXCLUDE", ">", ">=", "<", "<=", "=" };

    private static bool IsNumericOp(FilterOp op) => op >= FilterOp.Gt;

    /// <summary>
    /// One per-column rule. Text ops match a case-insensitive substring, so "black" finds
    /// Blacksmith. Numeric ops parse the cell; a cell that is not a number simply fails them,
    /// which is what makes two rules on one column behave as a range.
    /// </summary>
    private sealed class ColumnFilter
    {
        /// <summary>One index normally; every member index for a group target like "ingredient (any)".</summary>
        public int[] Columns = Array.Empty<int>();
        public string Label = string.Empty;
        public FilterOp Op;
        public string Text = string.Empty;

        /// <summary>Parsed once at add time rather than per row, per filter, per rebuild.</summary>
        public double Value;
    }

    /// <summary>Dropdown labels: every real column, then any group declared by the dataset.</summary>
    private string[] _filterTargets = Array.Empty<string>();

    /// <summary>Column indices behind each entry of <see cref="_filterTargets"/>.</summary>
    private int[][] _targetColumns = Array.Empty<int[]>();

    /// <summary>
    /// Longest cell string per column, found once over EVERY row at load. Measuring 30,000 rows
    /// with CalcTextSize would be absurd; picking the longest string by character count and
    /// measuring only that is one call per column and lands in the right place.
    /// </summary>
    private string[] _widest = Array.Empty<string>();

    /// <summary>Pixel width per column. Filled on the first draw, when an ImGui context exists.</summary>
    private float[] _widths = Array.Empty<float>();
    private bool _widthsReady;

    /// <summary>
    /// Bumped per load so the table gets a FRESH ImGui id each time a dataset is opened. ImGui
    /// remembers column widths per table id, so a stable id would restore the previous, badly
    /// sized layout and make the auto-fit look broken.
    /// </summary>
    private int _loadSeq;

    /// <summary>
    /// Ceiling on an auto-fitted column. A single 160-character cell would otherwise push every
    /// other column off screen - which is the same wasted-space complaint from the other side.
    /// Anything clipped is still readable through the hover tooltip, and columns stay resizable.
    /// </summary>
    private const float MaxColumnWidth = 420f;

    /// <summary>
    /// Filters are ANDed - with each other and with the search box. That is what makes a range
    /// expressible at all: ">= 1" plus "&lt; 12" on one column is 1-11, whereas ORing them would
    /// match everything.
    /// </summary>
    private readonly List<ColumnFilter> _filters = new();

    private int _newCol;
    private FilterOp _newOp = FilterOp.Include;
    private string _newText = string.Empty;
    private string _loadError = string.Empty;
    private bool _loading;

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The repo's <c>data/</c> folder. The dev plugin folder IS the repo root, so the datasets sit
    /// beside the built DLL. A public install has no such folder, which is why this is dev-only.
    /// </summary>
    private static string DataDirectory
    {
        get
        {
            string dll = Plugin.PluginInterface.AssemblyLocation.FullName;
            string dir = System.IO.Path.GetDirectoryName(dll) ?? string.Empty;
            return System.IO.Path.Combine(dir, "data");
        }
    }

    // ── Catalogue scan ───────────────────────────────────────────────────────

    private void ScanCatalogue()
    {
        _catalogue = new List<DatasetInfo>();
        _catalogueError = string.Empty;

        try
        {
            string dir = DataDirectory;
            if (!Directory.Exists(dir))
            {
                _catalogueError = $"No data folder at:\n{dir}\n\n"
                                + "The datasets live in the repo's data/ folder, which only exists in a "
                                + "source checkout. Run scripts/gen-datasets to build them.";
                return;
            }

            foreach (var path in Directory.GetFiles(dir, "*.json").OrderBy(p => p))
                _catalogue.Add(ReadHeader(path));

            if (_catalogue.Count == 0)
                _catalogueError = $"No .json files in {dir}. Run scripts/gen-datasets.";
        }
        catch (Exception ex)
        {
            _catalogueError = $"Could not scan data folder: {ex.Message}";
            Diag.Error($"[Datasets] scan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads only the envelope, stopping at "entries". Streaming matters here: the largest file is
    /// ~34 MB and the picker would otherwise parse all seven just to draw a list.
    /// </summary>
    private static DatasetInfo ReadHeader(string path)
    {
        var info = new DatasetInfo
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            Bytes = new FileInfo(path).Length,
        };

        try
        {
            using var sr = new StreamReader(path);
            using var jr = new JsonTextReader(sr);

            while (jr.Read())
            {
                if (jr.TokenType != JsonToken.PropertyName) continue;
                string prop = (string)(jr.Value ?? string.Empty);

                if (prop == "entries") break;   // everything we want precedes it

                switch (prop)
                {
                    case "description":
                        info.Description = jr.ReadAsString() ?? string.Empty;
                        break;
                    case "needsVerification":
                        info.NeedsVerification = jr.ReadAsString();
                        break;
                    case "count":
                        info.Count = jr.ReadAsInt32() ?? 0;
                        break;
                    case "unknownFields":
                        jr.Read();
                        info.UnknownFields = JToken.Load(jr) is JArray a
                            ? a.Select(x => x.ToString()).ToArray()
                            : Array.Empty<string>();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            info.HeaderError = ex.Message;
        }

        return info;
    }

    // ── Load ─────────────────────────────────────────────────────────────────

    private void Load(DatasetInfo info)
    {
        Unload();
        _loading = true;

        try
        {
            using var sr = new StreamReader(info.Path);
            using var jr = new JsonTextReader(sr);
            var root = JObject.Load(jr);
            var entries = root["entries"] as JArray;

            if (entries is null)
            {
                _loadError = "No 'entries' array in this file.";
                return;
            }

            // The file is written slim (schema 2): keys are aliased, and any field that is "???"
            // on EVERY entry is stripped and named once in the header. Both are reversed here, so
            // the grid shows real field names and the ??? columns exactly as if they had been
            // stored 30,000 times. Schema 1 files still load — the alias map is simply absent.
            var aliasMap = root["fieldAliases"] as JObject;
            var omitted = (root["omittedAlwaysUnknown"] as JArray)?
                          .Select(x => x.ToString()).ToArray() ?? Array.Empty<string>();

            string RealName(string key) => aliasMap?[key] is { } real ? real.ToString() : key;

            // Stored keys in first-seen order, so columns keep the generator's ordering. Every
            // entry is scanned - see the note on the removed ColumnSampleSize.
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries.OfType<JObject>())
                foreach (var p in e.Properties())
                    if (seen.Add(p.Name)) keys.Add(p.Name);

            // Fields hoisted out because they carry the SAME value on every entry. Restored as
            // ordinary columns showing that value - the reader should not have to know or care
            // that the file stored it once.
            var constants = root["omittedConstant"] as JObject;
            var constNames = constants?.Properties().Select(pr => pr.Name).ToArray() ?? Array.Empty<string>();
            var constCells = constants?.Properties().Select(pr => Flatten(pr.Value)).ToArray() ?? Array.Empty<string>();

            // Real columns, then the always-??? ones, then the constant ones.
            _columns = keys.Select(RealName).Concat(omitted).Concat(constNames).ToArray();
            int stored = keys.Count;
            int unknownEnd = stored + omitted.Length;

            _rows = new List<string[]>(entries.Count);
            _blobs = new List<string>(entries.Count);

            foreach (var e in entries.OfType<JObject>())
            {
                var cells = new string[_columns.Length];
                var blob = new StringBuilder(128);

                for (int c = 0; c < stored; c++)
                {
                    cells[c] = Flatten(e[keys[c]]);
                    blob.Append(cells[c]).Append(BlobSeparator);
                }

                // Synthesised: stripped precisely BECAUSE they never vary.
                for (int c = stored; c < unknownEnd; c++) cells[c] = "???";
                for (int c = unknownEnd; c < _columns.Length; c++) cells[c] = constCells[c - unknownEnd];

                // Constants go into the blob too. They are real values that happen to be uniform,
                // and the box says "Search all columns" - searching for "level" and getting
                // nothing because unlockType was hoisted out would be a lie. The ??? columns are
                // left out deliberately: every row would match "???" and the term is useless.
                for (int c = unknownEnd; c < _columns.Length; c++)
                    blob.Append(cells[c]).Append(BlobSeparator);

                _rows.Add(cells);
                _blobs.Add(blob.ToString().ToLowerInvariant());
            }

            // Widest content per column, header included so a short column never clips its own name.
            _widest = new string[_columns.Length];
            for (int c = 0; c < _columns.Length; c++) _widest[c] = _columns[c];
            foreach (var cells in _rows)
                for (int c = 0; c < cells.Length && c < _widest.Length; c++)
                    if (cells[c].Length > _widest[c].Length) _widest[c] = cells[c];

            _widths = new float[_columns.Length];
            _widthsReady = false;
            _loadSeq++;

            // Filter targets: every column, plus any group the dataset declares. A group lets one
            // rule span several columns - "ingredient (any)" searches all eight slots at once,
            // which is the only sane way to ask "which recipes use Cotton Yarn".
            var targets = new List<string>(_columns);
            var targetCols = new List<int[]>();
            for (int i = 0; i < _columns.Length; i++) targetCols.Add(new[] { i });

            if (root["columnGroups"] is JObject groups)
            {
                foreach (var g in groups.Properties())
                {
                    var members = (g.Value as JArray)?
                        .Select(x => Array.IndexOf(_columns, x.ToString()))
                        .Where(ix => ix >= 0).ToArray() ?? Array.Empty<int>();
                    if (members.Length == 0) continue;
                    targets.Add(g.Name);
                    targetCols.Add(members);
                }
            }
            _filterTargets = targets.ToArray();
            _targetColumns = targetCols.ToArray();

            _open = info;
            _page = 0;
            _search = _appliedSearch = string.Empty;
            ApplyFilter();

            Diag.Info($"[Datasets] loaded {info.FileName}: {_rows.Count} rows, {_columns.Length} columns.");
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            Diag.Error($"[Datasets] load failed for {info.FileName}: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    public void Unload()
    {
        _open = null;
        _columns = Array.Empty<string>();
        _rows = new();
        _blobs = new();
        _filtered = new();
        _loadError = string.Empty;
        _search = _appliedSearch = string.Empty;
        _page = 0;

        // Column indices are meaningless against a different dataset - a stale filter would
        // silently point at whatever column happened to land in that slot.
        _widest = Array.Empty<string>();
        _widths = Array.Empty<float>();
        _widthsReady = false;
        _filters.Clear();
        _filterTargets = Array.Empty<string>();
        _targetColumns = Array.Empty<int[]>();
        _newCol = 0;
        _newText = string.Empty;
    }

    /// <summary>One cell as a string. Nested objects/arrays become compact JSON — the grid is for
    /// scanning, and a wall of pretty-printed JSON in a cell helps nobody.</summary>
    private static string Flatten(JToken? t)
    {
        if (t is null || t.Type == JTokenType.Null) return string.Empty;

        string s = t.Type switch
        {
            JTokenType.Object or JTokenType.Array => t.ToString(Formatting.None),
            JTokenType.Float => ((double)t).ToString("0.###", CultureInfo.InvariantCulture),
            _ => t.ToString(),
        };

        // Deliberately NOT truncated - see MaxCellLength. Clipping happens at draw time so that
        // search and the tooltip both see the whole value.
        return s.Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>
    /// Rebuilds the visible row list from the free-text search AND every column filter. Runs only
    /// when something changes, never per frame — which is why it can afford to walk 30,000 rows
    /// and do a case-insensitive scan per filter rather than caching a lowercased copy of every
    /// cell (that would roughly double the memory this thing already holds).
    /// </summary>
    private void ApplyFilter()
    {
        _filtered = new List<int>(_rows.Count);
        string q = _appliedSearch.Trim().ToLowerInvariant();

        for (int i = 0; i < _rows.Count; i++)
        {
            if (q.Length > 0 && !_blobs[i].Contains(q, StringComparison.Ordinal)) continue;

            var cells = _rows[i];
            bool ok = true;

            foreach (var f in _filters)
            {
                if (f.Text.Length == 0 || f.Columns.Length == 0) continue;

                bool pass;
                if (IsNumericOp(f.Op))
                {
                    // A non-numeric cell FAILS every numeric comparison rather than passing by
                    // default. "recipeLevel >= 1" must not quietly keep rows whose value is "???"
                    // or a word - a range filter that silently admits unknowns is worse than none.
                    // A group passes if ANY member satisfies the comparison.
                    pass = false;
                    foreach (int ci in f.Columns)
                    {
                        string cell = ci < cells.Length ? cells[ci] : string.Empty;
                        if (!double.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                            continue;
                        bool m = f.Op switch
                        {
                            FilterOp.Gt => n >  f.Value,
                            FilterOp.Ge => n >= f.Value,
                            FilterOp.Lt => n <  f.Value,
                            FilterOp.Le => n <= f.Value,
                            _           => Math.Abs(n - f.Value) < 0.000001,
                        };
                        if (m) { pass = true; break; }
                    }
                }
                else
                {
                    // INCLUDE passes if ANY member contains it; EXCLUDE requires that NONE does,
                    // which is the only reading that makes "hide recipes using Fire Shard" work
                    // when the shard could be in any of the eight ingredient slots.
                    bool hit = false;
                    foreach (int ci in f.Columns)
                    {
                        string cell = ci < cells.Length ? cells[ci] : string.Empty;
                        if (cell.Contains(f.Text, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                    }
                    pass = f.Op == FilterOp.Exclude ? !hit : hit;
                }

                if (!pass) { ok = false; break; }
            }

            if (ok) _filtered.Add(i);
        }

        _page = 0;
    }

    /// <summary>The filter builder plus the list of active rules.</summary>
    private void DrawFilters()
    {
        if (_filterTargets.Length == 0) return;

        ImGui.SetNextItemWidth(220);
        string colLabel = _newCol < _filterTargets.Length ? _filterTargets[_newCol] : "column";
        if (ImGui.BeginCombo("##tc_ds_fcol", colLabel))
        {
            for (int i = 0; i < _filterTargets.Length; i++)
                if (ImGui.Selectable(_filterTargets[i], i == _newCol)) _newCol = i;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.BeginCombo("##tc_ds_fmode", OpLabels[(int)_newOp]))
        {
            for (int i = 0; i < OpLabels.Length; i++)
                if (ImGui.Selectable(OpLabels[i], (int)_newOp == i)) _newOp = (FilterOp)i;
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        bool submitted = ImGui.InputTextWithHint("##tc_ds_ftext", "text to match…", ref _newText, 128,
                                                ImGuiInputTextFlags.EnterReturnsTrue);

        string typed = _newText.Trim();
        bool numericOk = !IsNumericOp(_newOp)
                       || double.TryParse(typed, NumberStyles.Any, CultureInfo.InvariantCulture, out _);

        ImGui.SameLine();
        if ((ImGui.Button("Add filter", new Vector2(90, 0)) || submitted)
            && typed.Length > 0 && numericOk)
        {
            double.TryParse(typed, NumberStyles.Any, CultureInfo.InvariantCulture, out var val);
            // Both reads guarded. The index is reset on unload so it should never be stale, but
            // one of these was guarded and the other was not, which is the kind of asymmetry that
            // becomes a crash the first time someone adds a code path that resizes the targets.
            _filters.Add(new ColumnFilter
            {
                Columns = _newCol < _targetColumns.Length ? _targetColumns[_newCol] : Array.Empty<int>(),
                Label = _newCol < _filterTargets.Length ? _filterTargets[_newCol] : "?",
                Op = _newOp,
                Text = typed,
                Value = val,
            });
            _newText = string.Empty;
            ApplyFilter();
        }

        // Refusing silently would read as a broken button.
        if (typed.Length > 0 && !numericOk)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);
            ImGui.TextUnformatted($"\"{typed}\" is not a number - " + OpLabels[(int)_newOp] + " needs one.");
            ImGui.PopStyleColor();
        }

        if (_filters.Count == 0) return;

        int remove = -1;
        for (int i = 0; i < _filters.Count; i++)
        {
            var f = _filters[i];
            ImGui.PushID(1000 + i);

            if (ImGui.Button("x", new Vector2(22, 0))) remove = i;
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Text,
                f.Op == FilterOp.Exclude ? ColUnknown : IsNumericOp(f.Op) ? ColNumeric : ColOk);
            ImGui.TextUnformatted(OpLabels[(int)f.Op]);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            ImGui.TextUnformatted($"{f.Label} : \"{f.Text}\"");

            ImGui.PopID();
        }

        if (remove >= 0)
        {
            _filters.RemoveAt(remove);
            ApplyFilter();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear filters", new Vector2(110, 0)))
        {
            _filters.Clear();
            ApplyFilter();
        }
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(1100, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Dataset Viewer (dev)##tc_datasets", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try
        {
            if (_open is null) DrawPicker();
            else DrawGrid();
        }
        catch (Exception ex)
        {
            // Never let a dev tool take the game down from the draw loop.
            Diag.Error($"[Datasets] draw failed: {ex.Message}");
        }

        ImGui.End();
    }

    private void DrawPicker()
    {
        ImGui.TextWrapped("Reference datasets in the repo's data/ folder. Select one to browse it.");
        ImGui.Spacing();

        if (ImGui.Button("Rescan", new Vector2(90, 0))) _catalogue = null;
        ImGui.SameLine();
        ImGui.TextDisabled(DataDirectory);
        ImGui.Separator();

        if (_catalogue is null) ScanCatalogue();

        if (_catalogueError.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);
            ImGui.TextWrapped(_catalogueError);
            ImGui.PopStyleColor();
            return;
        }

        if (_loadError.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);
            ImGui.TextWrapped($"Last load failed: {_loadError}");
            ImGui.PopStyleColor();
            ImGui.Separator();
        }

        foreach (var d in _catalogue!)
        {
            ImGui.PushID(d.FileName);

            bool clicked = ImGui.Button("Open", new Vector2(70, 0));
            ImGui.SameLine();

            string label = System.IO.Path.GetFileNameWithoutExtension(d.FileName);
            ImGui.Text(label);
            ImGui.SameLine();
            ImGui.TextDisabled($"{d.Count:N0} entries · {d.Bytes / 1024f / 1024f:0.#} MB");

            ImGui.Indent(78);

            if (d.HeaderError is { Length: > 0 })
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);
                ImGui.TextWrapped($"Header unreadable: {d.HeaderError}");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ColDim);
                ImGui.TextWrapped(d.Description);
                ImGui.PopStyleColor();

                if (d.NeedsVerification is { Length: > 0 })
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColWarn);
                    ImGui.TextWrapped("INCOMPLETE — " + d.NeedsVerification);
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, ColOk);
                    ImGui.TextUnformatted("Complete — every field is populated from game data.");
                    ImGui.PopStyleColor();
                }
            }

            ImGui.Unindent(78);
            ImGui.Spacing();
            ImGui.PopID();

            if (clicked) Load(d);
        }

        if (_loading) ImGui.TextDisabled("Loading…");
    }

    private void DrawGrid()
    {
        var d = _open!;

        if (ImGui.Button("< Datasets", new Vector2(110, 0))) { Unload(); return; }
        ImGui.SameLine();
        ImGui.Text(System.IO.Path.GetFileNameWithoutExtension(d.FileName));
        ImGui.SameLine();
        ImGui.TextDisabled($"{_rows.Count:N0} rows · {_columns.Length} columns");

        // The incompleteness banner — the thing that says "this dataset could not be fully made".
        if (d.NeedsVerification is { Length: > 0 })
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColWarn);
            ImGui.TextWrapped("INCOMPLETE DATASET — " + d.NeedsVerification);
            ImGui.PopStyleColor();
        }
        if (d.UnknownFields.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);
            ImGui.TextWrapped("Fields that are ??? : " + string.Join(", ", d.UnknownFields));
            ImGui.PopStyleColor();
        }

        ImGui.Separator();

        ImGui.SetNextItemWidth(360);
        if (ImGui.InputTextWithHint("##tc_ds_search", "Search all columns…", ref _search, 128))
        {
            _appliedSearch = _search;
            ApplyFilter();
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(70, 0)))
        {
            _search = _appliedSearch = string.Empty;
            ApplyFilter();
        }

        // Forces a re-measure and a brand new table id. If the columns are wrong on open but
        // right after pressing this, the measurement is fine and the application is at fault.
        ImGui.SameLine();
        if (ImGui.Button("Fit columns", new Vector2(100, 0)))
        {
            _widthsReady = false;
            _loadSeq++;
        }

        DrawFilters();

        int pages = Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        ImGui.SameLine();
        ImGui.TextDisabled($"{_filtered.Count:N0} match" + (_filtered.Count == 1 ? "" : "es"));

        ImGui.SameLine();
        if (ImGui.Button("<##pgprev", new Vector2(28, 0)) && _page > 0) _page--;
        ImGui.SameLine();
        ImGui.TextDisabled($"page {_page + 1} / {pages}");
        ImGui.SameLine();
        if (ImGui.Button(">##pgnext", new Vector2(28, 0)) && _page < pages - 1) _page++;

        ImGui.Spacing();

        if (_columns.Length == 0)
        {
            // ImGui requires at least one column; an empty dataset would otherwise assert inside
            // BeginTable rather than showing anything useful.
            ImGui.TextDisabled("This dataset has no columns — it is empty or malformed.");
            return;
        }

        if (_filtered.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches the current search and filters.");
            return;
        }

        // SizingFixedFit is deliberately NOT set. It tells ImGui to auto-fit columns to content,
        // which overrides the explicit per-column widths below - the columns came out ~380px
        // wide regardless of holding "1" or "False".
        //
        // NoSavedSettings matters too: ImGui persists table column widths in imgui.ini keyed by
        // table id, and a restored layout would silently win over a freshly computed one.
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
                  | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY
                  | ImGuiTableFlags.NoSavedSettings;

        // One CalcTextSize per column, once per dataset. Cannot happen at load time - there is no
        // ImGui context there - so it is deferred to the first frame that draws the grid.
        if (!_widthsReady)
        {
            for (int c = 0; c < _columns.Length; c++)
            {
                string probe = _widest[c].Length > MaxMeasureLength
                             ? _widest[c].Substring(0, MaxMeasureLength) : _widest[c];
                float w = ImGui.CalcTextSize(probe).X + 18f;   // + cell padding
                _widths[c] = Math.Clamp(w, 40f, MaxColumnWidth);
            }
            _widthsReady = true;
        }

        if (!ImGui.BeginTable($"##tc_ds_grid_{_loadSeq}", _columns.Length, flags)) return;

        for (int c = 0; c < _columns.Length; c++)
            ImGui.TableSetupColumn(_columns[c], ImGuiTableColumnFlags.WidthFixed, _widths[c]);

        // AFTER the column setup, not before. ImGui documents this ordering and the previous code
        // had it backwards, which corrupts the column layout - this is the likeliest reason the
        // widths were ignored entirely.
        ImGui.TableSetupScrollFreeze(1, 1);   // keep the first column and the header visible
        ImGui.TableHeadersRow();

        int start = _page * PageSize;
        int end = Math.Min(start + PageSize, _filtered.Count);

        for (int r = start; r < end; r++)
        {
            var cells = _rows[_filtered[r]];
            ImGui.TableNextRow();
            for (int c = 0; c < _columns.Length; c++)
            {
                ImGui.TableSetColumnIndex(c);
                string v = c < cells.Length ? cells[c] : string.Empty;

                // "???" is the whole point of these files — make it impossible to miss.
                bool unknown = v == "???" || v.Contains("\"???\"", StringComparison.Ordinal);
                if (unknown) ImGui.PushStyleColor(ImGuiCol.Text, ColUnknown);

                bool clipped = v.Length > MaxCellLength;
                ImGui.TextUnformatted(clipped ? v.Substring(0, MaxCellLength) + "…" : v);

                if (unknown) ImGui.PopStyleColor();

                // The tooltip shows the WHOLE value, which for an itemsFound cell is the entire
                // drop list. Wrapped, because a 400-item single line is not a tooltip.
                if (clipped && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(560f);
                    ImGui.TextUnformatted(v);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }
        }

        ImGui.EndTable();
    }
}
#endif
