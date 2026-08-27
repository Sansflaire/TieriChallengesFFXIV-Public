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

    /// <summary>Columns are derived from this many entries — enough to catch optional fields.</summary>
    private const int ColumnSampleSize = 300;

    /// <summary>Longest cell string kept. Nested objects can be enormous; the grid is for scanning.</summary>
    private const int MaxCellLength = 160;

    private static readonly Vector4 ColUnknown = new(1.00f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 ColWarn    = new(1.00f, 0.75f, 0.30f, 1f);
    private static readonly Vector4 ColOk      = new(0.45f, 0.90f, 0.50f, 1f);
    private static readonly Vector4 ColDim     = new(0.60f, 0.60f, 0.60f, 1f);

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

            // Columns: union over a sample, so a field only some entries carry still gets a column.
            var cols = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries.Take(ColumnSampleSize).OfType<JObject>())
                foreach (var p in e.Properties())
                    if (seen.Add(p.Name)) cols.Add(p.Name);
            _columns = cols.ToArray();

            _rows = new List<string[]>(entries.Count);
            _blobs = new List<string>(entries.Count);

            foreach (var e in entries.OfType<JObject>())
            {
                var cells = new string[_columns.Length];
                var blob = new StringBuilder(128);
                for (int c = 0; c < _columns.Length; c++)
                {
                    cells[c] = Flatten(e[_columns[c]]);
                    blob.Append(cells[c]).Append('');
                }
                _rows.Add(cells);
                _blobs.Add(blob.ToString().ToLowerInvariant());
            }

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

        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > MaxCellLength ? s.Substring(0, MaxCellLength) + "…" : s;
    }

    private void ApplyFilter()
    {
        _filtered = new List<int>(_rows.Count);
        string q = _appliedSearch.Trim().ToLowerInvariant();

        if (q.Length == 0)
        {
            for (int i = 0; i < _rows.Count; i++) _filtered.Add(i);
        }
        else
        {
            for (int i = 0; i < _blobs.Count; i++)
                if (_blobs[i].Contains(q, StringComparison.Ordinal)) _filtered.Add(i);
        }

        _page = 0;
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

        _catalogue ??= null;
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

        if (_filtered.Count == 0)
        {
            ImGui.TextDisabled("Nothing matches that search.");
            return;
        }

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
                  | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("##tc_ds_grid", _columns.Length, flags)) return;

        ImGui.TableSetupScrollFreeze(1, 1);   // keep the first column and the header visible
        foreach (var c in _columns) ImGui.TableSetupColumn(c);
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
                ImGui.TextUnformatted(v);
                if (unknown) ImGui.PopStyleColor();

                if (v.Length >= MaxCellLength && ImGui.IsItemHovered())
                    ImGui.SetTooltip(v);
            }
        }

        ImGui.EndTable();
    }
}
#endif
