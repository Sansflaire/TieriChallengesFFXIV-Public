using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Sound;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace TieriChallengesFFXIV;

/// <summary>
/// Plays sound effects straight out of the game's own audio engine.
///
/// <para>Routed through <c>SoundManager.PlaySound</c>, which takes an <c>.scd</c> bank path plus
/// the index of an entry inside it — the exact pair VFXEditor's Scd browser displays. Browsing to
/// <c>SE_UI.scd</c> and clicking "Audio 50" therefore names a sound unambiguously, with nothing
/// to translate on the way in.</para>
///
/// <para>References no PanacheUI type, same rule as <see cref="ToastQueue"/> — the completion
/// sound has to survive the Panache library being switched off or missing entirely.</para>
/// </summary>
internal static class GameSound
{
    /// <summary>The UI sound bank — clicks, confirms, errors. Holds 54 sounds, so 0–53.</summary>
    public const string UiBank = "sound/system/SE_UI.scd";

    // ── WAV playback ─────────────────────────────────────────────────────────
    //
    // The zingle cues ship as .wav files next to the DLL and play through Windows rather than
    // through the game's mixer.
    //
    // This is not a shortcut. Those sounds load correctly from the game's own archives and are
    // still inaudible, because something in the mixer silences them before they reach the output
    // — the Zingle bus reads zero, will not hold a write, and BypassVolumeRules does not get past
    // it. VFXEditor plays the very same sounds perfectly, and it does so by decoding them and
    // pushing the samples through WASAPI, never touching the game engine. Same idea here, minus
    // the decoder: the files are already WAV, so winmm's PlaySound is enough and adds no
    // dependency at all.
    //
    // Known limitation: winmm gives no volume control, so these play at the Windows level for the
    // process rather than following the in-game sound-effect slider. Game-bank cues still route
    // through SoundManager and still obey it.

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SndAsync     = 0x0001;   // return immediately; never block the frame
    private const uint SndNoDefault = 0x0002;   // silence rather than the Windows ding on failure
    private const uint SndFilename  = 0x00020000;

    /// <summary>Folder beside the DLL holding the shipped cue audio.</summary>
    private const string SoundFolder = "sounds";

    /// <summary>A cue that is a shipped file rather than an index into a game archive.</summary>
    public static bool IsWave(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>Absolute path of a shipped cue file.</summary>
    public static string ResolveAsset(string fileName)
    {
        string dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
        return Path.Combine(dir, SoundFolder, fileName);
    }

    /// <summary>
    /// Master cue volume and mute, mirrored from config by <c>Plugin</c>.
    ///
    /// <para>Held here as statics rather than reached through the config because this class is
    /// static and is called from the framework tick; the mirroring is one assignment on change,
    /// which is cheaper than a config reference on every cue.</para>
    /// </summary>
    public static float Volume { get; set; } = 1f;
    public static bool  Muted  { get; set; }

    private static bool PlayWave(string fileName) => PlayWaveAbsolute(ResolveAsset(fileName));

    private static bool PlayWaveAbsolute(string full)
    {
        string fileName = Path.GetFileName(full);

        if (!File.Exists(full))
        {
            Plugin.Log.Warning($"[Sound] {full} is missing — cue cannot play.");
            return false;
        }

        bool ok = PlaySoundW(full, IntPtr.Zero, SndAsync | SndFilename | SndNoDefault);

        if (ok) Plugin.Log.Information($"[Sound] wav {fileName} playing.");
        else    Plugin.Log.Warning($"[Sound] winmm refused {fileName} "
                                 + $"(error {Marshal.GetLastWin32Error()}).");
        return ok;
    }

    /// <summary>
    /// Completion fanfare. A dedicated single-sound bank rather than an index into the UI bank,
    /// which is why cues carry a path as well as an entry number.
    /// </summary>
    public const string DefaultCompleteBank = "zingle_fate_ffxi_clear.wav";

    /// <summary>
    /// Partial-progress sting. Fires for every step EXCEPT the one that finishes the challenge —
    /// that step raises completion instead, so the two fanfares never stack.
    /// </summary>
    public const string DefaultProgressBank = "zingle_treasure01.wav";

    /// <summary>
    /// Played on arriving somewhere with challenges still open. A game-bank cue rather than a
    /// shipped file — SE_UI entry 45 routes through a bus that is demonstrably open, and unlike
    /// the WAV cues it still follows the in-game sound-effect slider.
    /// </summary>
    public const string DefaultZoneBank  = UiBank;
    public const uint   DefaultZoneEntry = 45;

    /// <summary>Played once a progress wipe is confirmed — the one destructive action here.</summary>
    public const string DefaultResetBank = "zingle_pvpii_down.wav";

    /// <summary>
    /// Sounds started by the bank scan, kept so a looping one can be stopped.
    ///
    /// <para>Only the scan tracks anything. Ordinary cues are short, one-shot and fire-and-forget;
    /// the scan is the only thing that walks entries nobody has vetted, and it is where a looping
    /// entry played forever.</para>
    /// </summary>
    private static readonly List<(nint Handle, uint Entry)> ScanOwned = new();

    /// <summary>Default step cue entry. Zero — the zingle bank holds a single sound.</summary>
    public const uint DefaultProgressEntry = 0;

    /// <summary>
    /// Default completion cue. Zero because the zingle bank holds a single sound — unlike the UI
    /// bank, where the entry number does the choosing.
    /// </summary>
    public const uint DefaultCompleteEntry = 0;

    /// <summary>Default reset cue entry. Zero — the zingle bank holds a single sound.</summary>
    public const uint DefaultResetEntry = 0;

    /// <summary>
    /// Entry played when part of an objective lands — one area out of several, say. Deliberately
    /// the quieter of the two: this can fire repeatedly within a single challenge, so it has to
    /// read as a tick rather than a fanfare or it turns into noise.
    ///
    /// <para>Settable at runtime rather than a const because not every index in the bank holds
    /// audible audio, and finding one that does is a matter of listening. <c>/tchallenges sfx</c>
    /// auditions and sets these live so a dud entry never needs a rebuild to diagnose.</para>
    /// </summary>
    public static uint ProgressEntry { get; set; } = DefaultProgressEntry;

    /// <summary>Entry played when a challenge completes outright.</summary>
    public static uint CompleteEntry { get; set; } = DefaultCompleteEntry;

    /// <summary>Entry played once the user confirms wiping all progress.</summary>
    public static uint ResetEntry { get; set; } = DefaultResetEntry;

    // Each cue names its own bank. A cue is a (path, entry) pair, not just an index — the
    // completion fanfare lives in its own zingle file rather than in the shared UI bank.
    public static string ProgressBank { get; set; } = DefaultProgressBank;
    public static string CompleteBank { get; set; } = DefaultCompleteBank;
    public static string ResetBank     { get; set; } = DefaultResetBank;
    public static string ZoneBank      { get; set; } = DefaultZoneBank;
    public static uint   ZoneEntry     { get; set; } = DefaultZoneEntry;

    /// <summary>
    /// Where a cue currently points. The single mapping from cue to (bank, entry) — both playback
    /// and the dev sound-test panel read it, so the panel can never advertise a target that
    /// differs from what would actually play.
    /// </summary>
    public static (string Bank, uint Entry) CueTarget(SoundService.Cue cue) => cue switch
    {
        SoundService.Cue.ChallengeComplete => (CompleteBank, CompleteEntry),
        SoundService.Cue.ResetConfirmed    => (ResetBank,    ResetEntry),
        SoundService.Cue.ZoneAvailable     => (ZoneBank,     ZoneEntry),
        _                                  => (ProgressBank, ProgressEntry),
    };

    /// <summary>
    /// Play one entry from an .scd bank. Never throws — a missing bank, a bad index, or the sound
    /// engine not being up yet must not take down the caller. This runs off the back of a
    /// completion, and losing the *completion* because the *jingle* failed would be absurd.
    /// </summary>
    /// <summary>
    /// Dump the head of the loaded SE_UI bank to the log as hex.
    ///
    /// <para>Every entry resolves to the SAME SoundResourceHandle — it is one resource holding
    /// the whole bank — so the count of sounds it actually contains is sitting in memory right
    /// now. Entries past that count are accepted and report <c>active</c>, they just have nothing
    /// to play, which is the behaviour 55 and 85 show. Reading the real bytes settles how many
    /// there are instead of inferring it from silence.</para>
    /// </summary>
    public static unsafe void DumpBankHeader()
    {
        try
        {
            var mgr = SoundManager.Instance();
            if (mgr == null) { Plugin.Log.Warning("[Sound] dump: no SoundManager."); return; }

            // The first attempt at this used entry 0 at volume 0 and got a NULL SoundData back,
            // so the engine declines that combination. Try progressively louder/realer requests
            // until one is accepted; all we need is the handle, and the bank is shared by every
            // entry so it does not matter which one opens it.
            SoundData* data = null;

            (uint entry, float volume, SoundVolumeCategory cat)[] attempts =
            {
                (ProgressEntry, 1f,    SoundVolumeCategory.NoPlay),
                (ProgressEntry, 0.02f, SoundVolumeCategory.Player),
                (ProgressEntry, 1f,    SoundVolumeCategory.Player),
            };

            foreach (var (entry, volume, cat) in attempts)
            {
                data = mgr->PlaySound(UiBank, volume, 0u, 0f, 0f, 0f, 1f, 0, entry, true,
                                      cat, false, 0, false, false, false, false);

                if (data != null)
                {
                    Plugin.Log.Information($"[Sound] dump: handle obtained via entry {entry}, "
                                         + $"volume {volume}, category {cat}.");
                    break;
                }
            }

            if (data == null) { Plugin.Log.Warning("[Sound] dump: no SoundData from any probe."); return; }

            var res = data->SoundResourceHandle;
            if (res == null) { Plugin.Log.Warning("[Sound] dump: no resource handle."); return; }

            byte* bytes = res->GetData();
            ulong len   = res->GetLength();

            if (bytes == null || len == 0)
            {
                Plugin.Log.Warning($"[Sound] dump: resource has no data (len {len}).");
                return;
            }

            Plugin.Log.Information($"[Sound] dump: {UiBank} length {len} bytes, FileSize {res->FileSize}.");

            int take = (int)Math.Min(len, 256UL);
            var sb = new System.Text.StringBuilder(take * 3);
            for (int i = 0; i < take; i++)
            {
                sb.Append(bytes[i].ToString("X2"));
                sb.Append(((i + 1) % 32 == 0) ? '\n' : ' ');
            }

            Plugin.Log.Information("[Sound] dump head:\n" + sb);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Sound] bank dump failed");
        }
    }

    /// <param name="midiNote">
    /// Only meaningful for MIDI-sequenced entries. Zero is right for ordinary sampled audio, and
    /// is the one parameter of the seventeen that has never been validated against an entry known
    /// to need it — hence <c>/tchallenges sfx note</c>.
    /// </param>
    /// <param name="trackForStop">
    /// Remember the handle so <see cref="StopAll"/> can silence it. Used by the bank scan, which
    /// plays unvetted entries and can hit a looping one.
    /// </param>
    /// <returns>True when the engine handed back a SoundData, i.e. it accepted the entry.</returns>
    public static unsafe bool Play(string path, uint soundNumber, int midiNote = 0,
                                   bool trackForStop = false)
    {
        try
        {
            // Shipped files never touch the game mixer — that is the entire point of them.
            // Volume is applied by handing PlayWave a rescaled COPY, since winmm takes no volume
            // argument; null means muted or zero, i.e. play nothing at all. See WaveVolume.
            if (IsWave(path))
            {
                string? resolved = WaveVolume.ResolveForPlayback(
                    ResolveAsset(path), Volume, Muted);

                return resolved != null && PlayWaveAbsolute(resolved);
            }

            // A path that does not exist still produces a resource handle and a "successful"
            // play, so the only way to catch a typo is to ask before playing.
            if (!BankExists(path))
            {
                Plugin.Log.Warning($"[Sound] {path} does not exist — cue cannot play.");
                return false;
            }

            var mgr = SoundManager.Instance();
            if (mgr == null)
            {
                // EVERY branch in here logs at Warning or above, deliberately. An earlier version
                // logged the failure paths at Debug, which dalamud.log filters out by default —
                // so a call throwing on every single invocation looked exactly like a call
                // succeeding silently, and cost two rounds of wrong diagnosis.
                Plugin.Log.Warning("[Sound] SoundManager not available — cue skipped.");
                return false;
            }

            // The Zingle bus reads 0 in ordinary play — the game opens it only for its own
            // fanfares — so a zingle .scd played under the normal rules is silenced at the bus no
            // matter what volume or category is passed. Two of the chosen banks loaded perfectly
            // (35 KB and 185 KB) and were still inaudible for exactly this reason.
            //
            // BypassVolumeRules is the only way to hear one. To stop that becoming "ignores the
            // user's audio settings", the volume is scaled by the SE bus's effective volume, so
            // the cue still tracks their sound-effect slider and a muted game stays muted.
            // Once per session, on the first cue, record every bus. Which bus a sound actually
            // routes to is decided by the engine from the sound data — "zingle" is a FOLDER name,
            // and treating it as the bus name was an assumption never checked. Seeing all 21 at
            // once shows what is open and what is shut without guessing which one matters.
            if (!_busesLogged)
            {
                _busesLogged = true;
                DumpBusesToLog(mgr);
            }

            bool  zingle   = IsZingle(path);
            float busSe    = mgr->GetEffectiveVolume(SoundBus.SE);
            float volume   = zingle ? Math.Clamp(busSe, 0f, 1f) : 1f;
            var   category = zingle ? SoundVolumeCategory.BypassVolumeRules
                                    : SoundVolumeCategory.Player;

            // The return value is the diagnostic that matters: a null SoundData means the engine
            // declined the entry (empty slot, bad index), while non-null means it really is
            // playing and any silence is the audio itself, not the call.
            var data = mgr->PlaySound(
                path:            path,
                volume:          volume,
                fadeInDuration:  0u,

                // Not a world sound, so the position is never consulted (isPositional: false).
                posX:            0f,
                posY:            0f,
                posZ:            0f,

                speed:           1f,

                // Undocumented in FFXIVClientStructs. Zero is the inert value for both.
                a9:              0,

                soundNumber:     soundNumber,

                // TRUE. Switching this to false in 0.81.9.12 to make looping entries stoppable
                // silenced every cue — the docs say a non-auto-released SoundData "stays active
                // so it can be reused", which evidently means the handle is prepared rather than
                // fired. Audibility beats stoppability: loops are stopped instead by releasing a
                // tracked handle, guarded so a recycled slot is never touched. See TryRelease.
                autoRelease:     true,

                volumeCategory:  category,

                a13:             false,

                midiNote:        midiNote,

                a15:             false,

                // The 10-second default fade-out is for music beds, not a short UI sting.
                defaultFadeOut:  false,
                isPositional:    false,
                a18:             false);

            if (data == null)
            {
                Plugin.Log.Warning($"[Sound] entry {soundNumber} in {path} returned no SoundData "
                                 + "— the engine declined it (empty slot or bad index).");
                return false;
            }

            // A non-null SoundData only proves the engine took the request — entries 55 and 85
            // both got one and stayed silent. What separates an audible entry from a dead one is
            // whether a sound RESOURCE actually backs it, so log the resource handle and the
            // driver index alongside it.
            var res = data->SoundResourceHandle;

            // FileSize is the decisive one. A path that does not exist still yields a resource
            // handle — the engine creates one and then fails to fill it — so a non-null pointer
            // proves nothing. Zero bytes means the .scd was never found, which is indistinguishable
            // from a valid-but-silent entry by ear alone.
            string resDesc = res == null
                ? "NULL"
                : $"0x{(nint)res:X} size={res->FileSize} load={res->LoadState} read={res->ReadState}";

            Plugin.Log.Information(
                $"[Sound] {path} #{soundNumber}: SoundData 0x{(nint)data:X}, resource {resDesc}, "
              + $"active {data->IsActive}, loading {data->IsLoadingSoundResource}, "
              + $"midiNote {data->MidiNote}, vol {data->Volume:0.##}, cat {category}, "
              + $"bus-SE {busSe:0.##}, "
              + $"bus-Zingle {mgr->GetEffectiveVolume(SoundBus.Zingle):0.##}");

            if (trackForStop) ScanOwned.Add(((nint)data, soundNumber));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[Sound] PlaySound threw for entry {soundNumber} in {path}");
            return false;
        }
    }

    /// <summary>
    /// Print every sound bus and its effective volume.
    ///
    /// <para>bus-Zingle reads 0 while bus-SE reads 0.33, and BypassVolumeRules did not get past
    /// it — the bus is applied after the category. What is not known is WHY it is zero: a game
    /// setting that maps to it, or a bus the engine keeps shut outside its own fanfares. Seeing
    /// all 21 at once answers that: if Zingle is the only zero, nothing the user chose put it
    /// there.</para>
    /// </summary>
    public static unsafe void DumpBuses()
    {
        try
        {
            var mgr = SoundManager.Instance();
            if (mgr == null) { Plugin.ChatGui.PrintError("[Challenges] No SoundManager."); return; }

            var sb = new System.Text.StringBuilder("[Sound] buses:");
            foreach (SoundBus bus in Enum.GetValues<SoundBus>())
            {
                float v = mgr->GetEffectiveVolume(bus);
                sb.Append($" {bus}={v:0.###}");
                Plugin.ChatGui.Print($"[Challenges] bus {bus} = {v:0.###}");
            }

            // Also to the log. The first version of this printed to chat only, which made its
            // output unreadable after the fact — a diagnostic nobody can retrieve is not one.
            Plugin.Log.Information(sb.ToString());
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "[Sound] bus dump failed"); }
    }

    /// <summary>Guards the once-per-session bus snapshot.</summary>
    private static bool _busesLogged;

    private static unsafe void DumpBusesToLog(SoundManager* mgr)
    {
        try
        {
            var sb = new System.Text.StringBuilder("[Sound] bus snapshot:");
            foreach (SoundBus bus in Enum.GetValues<SoundBus>())
                sb.Append($" {bus}={mgr->GetEffectiveVolume(bus):0.###}");

            Plugin.Log.Information(sb.ToString());
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "[Sound] bus snapshot failed"); }
    }

    /// <summary>Effective volume of one bus, or 0 if the engine is not up.</summary>
    public static unsafe float BusVolume(SoundBus bus)
    {
        try
        {
            var mgr = SoundManager.Instance();
            return mgr == null ? 0f : mgr->GetEffectiveVolume(bus);
        }
        catch { return 0f; }
    }

    /// <summary>
    /// Set a bus volume. <b>This changes the running game's audio</b>, not a plugin setting — it
    /// exists to test whether the zingle bus being zero is the whole story, not as a fix.
    /// </summary>
    public static unsafe void SetBusVolume(SoundBus bus, float value)
    {
        try
        {
            var mgr = SoundManager.Instance();
            if (mgr == null) { Plugin.ChatGui.PrintError("[Challenges] No SoundManager."); return; }

            mgr->SetVolume(bus, Math.Clamp(value, 0f, 1f), 0);
            Plugin.ChatGui.Print($"[Challenges] bus {bus} set to {value:0.##} "
                               + $"— now reads {mgr->GetEffectiveVolume(bus):0.###}.");
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "[Sound] set bus volume failed"); }
    }

    /// <summary>Zingle banks are routed to a bus the game keeps closed outside its own fanfares.</summary>
    private static bool IsZingle(string path) =>
        path.StartsWith("sound/zingle/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Does this .scd actually exist? Cheap and cached by Dalamud, and the only way to tell a
    /// typo from a valid-but-silent bank — both play "successfully" and produce a resource handle.
    /// </summary>
    public static bool BankExists(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return IsWave(path) ? File.Exists(ResolveAsset(path))
                                : Plugin.DataManager.FileExists(path);
        }
        catch { return false; }
    }

    /// <summary>
    /// Silence anything the scan started. Cleared first, so a handle can never be released twice
    /// even if this is called repeatedly.
    /// </summary>
    public static void StopAll()
    {
        if (ScanOwned.Count == 0) return;

        var pending = ScanOwned.ToArray();
        ScanOwned.Clear();

        foreach (var (handle, entry) in pending) TryRelease(handle, entry);
    }

    /// <summary>
    /// Release a scanned sound, but only if the slot still holds it.
    ///
    /// <para>These are played with <c>autoRelease: true</c>, so the engine may already have freed
    /// the SoundData and handed the slot to something else. Releasing that would silence an
    /// unrelated sound at best and corrupt the pool at worst. The guard is the pair of checks
    /// below: still active, and still reporting the entry we started. A short sound that already
    /// finished fails them and is left alone — which is fine, because it is not the problem. The
    /// case this exists for is a LOOPING entry, which is by definition still active and still
    /// reporting its own number.</para>
    /// </summary>
    private static unsafe void TryRelease(nint handle, uint entry)
    {
        try
        {
            if (handle == 0) return;

            var mgr = SoundManager.Instance();
            if (mgr == null) return;

            var data = (SoundData*)handle;
            if (!data->IsActive || data->SoundNumber != entry) return;

            mgr->ReleaseSoundData(data);
            Plugin.Log.Information($"[Sound] stopped entry {entry}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"[Sound] release failed for 0x{handle:X}");
        }
    }
}
