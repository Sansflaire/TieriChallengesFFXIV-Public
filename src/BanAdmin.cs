#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY — the ban authoring tool. Compiled out of the public DLL entirely.
///
/// <para><b>Why a local plaintext ledger exists.</b> The published <c>bans.json</c> holds salted
/// hashes, which cannot be reversed — that is the entire point of it. So the only way to show a
/// readable list, or to lift a ban, is to keep the plaintext here and regenerate the published file
/// from it. <b>This ledger is the source of truth. Lose it and existing bans become permanent and
/// unattributable</b>, because nothing can turn a hash back into a name. It is written to the
/// plugin's config directory, outside any repo, because it is the one file in this system that does
/// contain real character names.</para>
/// </summary>
internal sealed class BanAdmin
{
    private sealed class Record
    {
        [JsonProperty("name")]   public string Name   { get; set; } = string.Empty;
        [JsonProperty("world")]  public string World  { get; set; } = string.Empty;
        [JsonProperty("reason")] public string Reason { get; set; } = string.Empty;
        [JsonProperty("added")]  public string Added  { get; set; } = string.Empty;
    }

    private readonly string _ledgerPath;
    private List<Record> _records = new();

    /// <summary>
    /// Where mirrors of the ledger are written on every save. Set to a checkout of the PRIVATE
    /// plugin repo — the mirror is committed there, which is what makes the ledger survive losing
    /// this machine. Empty disables mirroring (and says so loudly in the UI).
    /// </summary>
    private string _backupRepoPath = string.Empty;

    /// <summary>Last mirror result, shown in the UI so a silently failing backup is impossible.</summary>
    private string _backupStatus = "not yet written";

    // Add form. All three are required — a ban with no reason is one nobody can explain later,
    // including you.
    private string _name   = string.Empty;
    private string _world  = string.Empty;
    private string _reason = string.Empty;

    private string _repoPath = string.Empty;
    private string _feedback = string.Empty;
    private bool   _publishing;

    /// <summary>Guard against a mis-click wiping a ban; holds the index awaiting confirmation.</summary>
    private int _confirmRemove = -1;

    public BanAdmin(string configDir)
    {
        _ledgerPath = Path.Combine(configDir, "bans-private.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_ledgerPath)) return;
            _records = JsonConvert.DeserializeObject<List<Record>>(File.ReadAllText(_ledgerPath))
                       ?? new List<Record>();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[BanAdmin] ledger unreadable");
            _feedback = "Could not read the ban ledger — see the log. NOT overwriting it.";
        }
    }

    private bool Save()
    {
        string json;
        try
        {
            json = JsonConvert.SerializeObject(_records, Formatting.Indented);

            // Temp-then-replace: a half-written ledger is the one failure that cannot be recovered
            // from, since the published hashes are one-way.
            string tmp = _ledgerPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _ledgerPath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[BanAdmin] ledger write failed");
            _feedback = "Could not write the ban ledger — see the log.";
            return false;
        }

        Mirror(json);
        return true;
    }

    /// <summary>
    /// Copy the ledger into the private repo, every single save, without being asked.
    ///
    /// <para><b>Two files, on purpose.</b> <c>backup/bans-private.json</c> is the current mirror
    /// and is overwritten each time — that is what gets committed. <c>backup/history/</c> keeps a
    /// dated snapshot per day, which covers the failure the mirror cannot: a bad edit or an
    /// accidental mass-unban faithfully mirrored over the only good copy.</para>
    ///
    /// <para>Failure is reported into the UI rather than logged and forgotten. A backup that
    /// quietly stopped working is worse than no backup, because it is believed.</para>
    /// </summary>
    private void Mirror(string json)
    {
        if (string.IsNullOrWhiteSpace(_backupRepoPath))
        {
            _backupStatus = "NOT MIRRORED — no private repo path set";
            return;
        }

        try
        {
            string dir     = Path.Combine(_backupRepoPath, "backup");
            string histDir = Path.Combine(dir, "history");
            Directory.CreateDirectory(histDir);

            File.WriteAllText(Path.Combine(dir, "bans-private.json"), json);
            File.WriteAllText(
                Path.Combine(histDir, $"bans-private-{DateTime.UtcNow:yyyy-MM-dd}.json"), json);

            _backupStatus = $"mirrored to {dir} at {DateTime.Now:HH:mm:ss} — commit the private repo";
            Plugin.Log.Information("[BanAdmin] ledger mirrored to the private repo.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[BanAdmin] mirror failed");
            _backupStatus = "MIRROR FAILED — see the log. The ledger itself was still saved.";
        }
    }

    public void Draw(Configuration config)
    {
        _repoPath       = config.SyncRepoPath ?? string.Empty;
        _backupRepoPath = config.DevRepoPath  ?? string.Empty;

        ImGui.TextWrapped("Bans are published as salted hashes with individually encrypted reasons. "
                        + "Nobody reading the published file can tell who is on it, or why.");
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.89f, 0.70f, 0.25f, 1f),
            "This list is the only readable copy. Hashes cannot be reversed — if this file is lost, "
          + "existing bans can never be lifted.");
        ImGui.TextDisabled(_ledgerPath);

        ImGui.TextUnformatted("Private repo (ledger is mirrored here on every save)");
        ImGui.SetNextItemWidth(-1);
        string backup = _backupRepoPath;
        if (ImGui.InputTextWithHint("##ban_backup_repo",
                @"C:\...\devPlugins\TieriChallengesFFXIV", ref backup, 512))
        {
            config.DevRepoPath = backup;
            _backupRepoPath    = backup;
        }

        bool mirrored = _backupStatus.StartsWith("mirrored", StringComparison.Ordinal);
        ImGui.TextColored(mirrored ? new Vector4(0.50f, 0.84f, 0.66f, 1f)
                                   : new Vector4(0.90f, 0.42f, 0.38f, 1f),
                          "Backup: " + _backupStatus);

        if (ImGui.SmallButton("Back up now"))
        {
            Mirror(JsonConvert.SerializeObject(_records, Formatting.Indented));
            _feedback = "Backup written.";
        }

        ImGui.Separator();
        DrawAddForm();
        ImGui.Separator();
        DrawList();
        ImGui.Separator();
        DrawPublish(config);

        if (!string.IsNullOrEmpty(_feedback))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_feedback);
        }
    }

    private void DrawAddForm()
    {
        ImGui.TextUnformatted("Ban a character");

        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##ban_name", "Character name", ref _name, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint("##ban_world", "World", ref _world, 48);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ban_reason", "Reason (shown to them, required)", ref _reason, 400);

        string name   = _name.Trim();
        string world  = _world.Trim();
        string reason = _reason.Trim();

        bool dupe = false;
        string identity = BanService.Identity(name, world);
        foreach (var r in _records)
            if (BanService.Identity(r.Name, r.World) == identity) dupe = true;

        bool ok = name.Length > 0 && world.Length > 0 && reason.Length > 0 && !dupe;

        if (name.Length == 0 || world.Length == 0)
            ImGui.TextDisabled("Name and world are both required — the ban is keyed on the pair.");
        else if (reason.Length == 0)
            ImGui.TextColored(new Vector4(0.90f, 0.42f, 0.38f, 1f),
                "A reason is required. It is the message they will see.");
        else if (dupe)
            ImGui.TextColored(new Vector4(0.90f, 0.42f, 0.38f, 1f), "That character is already banned.");

        if (!ok) ImGui.BeginDisabled();
        if (ImGui.Button("Add ban", new Vector2(140, 28)))
        {
            _records.Add(new Record
            {
                Name   = name,
                World  = world,
                Reason = reason,
                Added  = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            });

            if (Save())
            {
                _feedback = $"Banned {name}@{world}. Publish to make it take effect.";
                _name = _world = _reason = string.Empty;
            }
        }
        if (!ok) ImGui.EndDisabled();
    }

    private void DrawList()
    {
        ImGui.TextUnformatted($"Current bans ({_records.Count})");

        if (_records.Count == 0)
        {
            ImGui.TextDisabled("Nobody is banned.");
            return;
        }

        for (int i = 0; i < _records.Count; i++)
        {
            var r = _records[i];
            ImGui.PushID(i);

            ImGui.TextUnformatted($"{r.Name}@{r.World}");
            ImGui.SameLine();
            ImGui.TextDisabled($"· {r.Added}");

            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(r.Reason);
            ImGui.PopTextWrapPos();

            if (_confirmRemove == i)
            {
                ImGui.TextColored(new Vector4(0.90f, 0.42f, 0.38f, 1f), "Unban?");
                ImGui.SameLine();
                if (ImGui.SmallButton("Yes, unban"))
                {
                    _records.RemoveAt(i);
                    _confirmRemove = -1;
                    if (Save()) _feedback = "Unbanned. Publish to make it take effect.";
                    ImGui.PopID();
                    break;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) _confirmRemove = -1;
            }
            else if (ImGui.SmallButton("Unban"))
            {
                _confirmRemove = i;
            }

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawPublish(Configuration config)
    {
        ImGui.TextUnformatted("Sync repo checkout");
        ImGui.SetNextItemWidth(-1);
        string path = _repoPath;
        if (ImGui.InputTextWithHint("##ban_repo", @"C:\path\to\TieriChallengesFFXIV-Sync", ref path, 512))
        {
            config.SyncRepoPath = path;
            _repoPath = path;
        }

        bool canPublish = !_publishing && !string.IsNullOrWhiteSpace(_repoPath);
        if (!canPublish) ImGui.BeginDisabled();

        if (ImGui.Button(_publishing ? "Publishing…" : "Publish ban list", new Vector2(180, 30)))
        {
            _publishing = true;
            _feedback   = string.Empty;

            // Regenerated from the ledger every time rather than patched, so the published file is
            // always exactly what the ledger says — an unban cannot leave a stale entry behind.
            var rows = new List<(string, string, string)>();
            foreach (var r in _records) rows.Add((r.Name, r.World, r.Reason));
            string json  = BanService.BuildBansJson(rows);
            string repo  = _repoPath;
            int    count = _records.Count;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var result = await GitPublisher.PublishFileAsync(
                    repo, "bans.json", json, $"Update ban list ({count} entr(ies))");

                _feedback = result.Ok
                    ? result.Summary + "  Allow up to 5 minutes for the CDN to serve it."
                    : "Publish failed: " + result.Summary;
                _publishing = false;
            });
        }

        if (!canPublish) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Reload ledger", new Vector2(130, 30))) { Load(); _feedback = "Ledger reloaded."; }
    }
}
#endif
