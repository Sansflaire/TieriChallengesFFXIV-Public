using System;
using System.IO;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PanacheUI.Icons;

namespace TieriChallengesFFXIV;

/// <summary>
/// Entry point. Owns the config, the main PanacheUI window, and the chat commands.
///
/// House conventions followed here (see devPlugins/CLAUDE.md):
///   • Dalamud services are static [PluginService] properties; the ctor is parameterless.
///   • No WindowSystem — a PanacheUI window drives its own ImGui.Begin/End and is called
///     directly from UiBuilder.Draw, gated on a bool.
///   • Every callback body is wrapped in try/catch. An exception escaping into Dalamud's
///     draw loop takes the game down with it.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui             FlyTextGui      { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager   { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IGameInventory          GameInventory   { get; private set; } = null!;

    /// <summary>
    /// Event-driven "do I hold item X?" map. Static because <see cref="ConditionEvaluator"/> is
    /// static and is reached from the tracker tick, the creator preview and the dev status block —
    /// threading an instance through all three would buy nothing, since there is exactly one
    /// inventory and it belongs to the process, not to any one window.
    /// </summary>
    internal static InventoryWatcher Inventory { get; private set; } = null!;

    /// <summary>
    /// True in Trist's developer build, false in the public artifact. Set from the DEV_BUILD
    /// compile constant, which TieriChallengesFFXIV.csproj defines for the Debug configuration
    /// only. Dev-only features are additionally compiled out entirely — this flag is for UI
    /// branching, not for security.
    /// </summary>
#if DEV_BUILD
    internal const bool IsDevBuild = true;
#else
    internal const bool IsDevBuild = false;
#endif

    /// <summary>
    /// The plugin's audio owner. Anything that wants a cue calls <c>Plugin.Sound.Play(...)</c> and
    /// forgets about it — playback happens on the service's own tick and cannot be suppressed,
    /// delayed or dropped by any UI. See <see cref="SoundService"/> for why that separation is
    /// load-bearing.
    /// </summary>
    internal static SoundService Sound { get; private set; } = null!;

    private const string CmdMain  = "/tchallenges";
    private const string CmdShort = "/tchal";

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;
    private readonly Dialogs              _dialogs;
    private readonly FallbackWindow       _fallbackWindow;
    private readonly OfficialCatalog      _official;
    private readonly ChallengeSyncService _sync;

    /// <summary>
    /// Queue and timing for the completion popup, shared by both renderers so a completion is
    /// celebrated whether PanacheUI is on, off, or missing entirely.
    /// </summary>
    private readonly ToastQueue    _toastQueue = new();
    private readonly FallbackToast _fallbackToast = new();

    /// <summary>
    /// Queue and timing for the small bottom-right progress notification, split the same way and
    /// for the same reason: partial progress must still be announced without PanacheUI.
    /// </summary>
    private readonly ProgressQueue         _progressQueue = new();
    private readonly FallbackProgressToast _fallbackProgressToast;

    /// <summary>
    /// Live character/zone readout, on demand. Renderer-agnostic and drawn at root scope, so both
    /// UIs open the same popup rather than each carrying its own copy of the text.
    /// </summary>
    private readonly StatusWindow _statusWindow;

    // PanacheUI-backed. NULL when the library could not be loaded — these types must never be
    // constructed in that case, because merely loading them throws. See PanacheAvailability.
    private readonly CompletionToast? _toast;
    private readonly ProgressToast?   _progressToast;
    private readonly MainWindow?      _mainWindow;

    /// <summary>Shared by both renderers so toggling does not lose the open/closed state.</summary>
    private bool _windowVisible;

    // One-shot A/B probe for "/tchallenges sfx thread <n>". Until 0.81.9.4 the cues were played
    // from the draw path and entry 50 was audible; they now play from the framework tick. These
    // two fields replay the same entry from the draw path a beat later so the two contexts can be
    // told apart by ear, which is the only way to know whether the thread is the variable.
    private uint _drawTestEntry;
    private long _drawTestAtMs;

    /// <summary>True when the Panache renderer is both available and switched on.</summary>
    private bool UsePanache => _mainWindow != null && _config.UsePanacheUI;

#if DEV_BUILD
    private readonly ChallengeCreatorWindow _creatorWindow;

    /// <summary>
    /// Null when PanacheUI could not load — it is a Panache surface, and merely constructing one
    /// resolves the library. The chat commands remain the fallback in that case.
    /// </summary>
    private readonly SoundTestWindow? _soundTestWindow;
#endif

    public Plugin()
    {
        // Point PanacheUI at the Icons folder shipped INSIDE this plugin, before any UI is built.
        //
        // Its automatic search looks at devPlugins\PanacheUI\Icons first, which exists on the dev
        // machine and nowhere else — so every icon silently degraded to a grey placeholder for
        // anyone who installed the plugin normally, and no amount of testing here could show it.
        // Releases up to and including 0.81.28.0 shipped no Icons folder at all; the build now
        // packages one and this line makes the lookup find it regardless of where Dalamud decides
        // to load the assembly from. Harmless if the folder is absent — the old search still runs.
        TrySetIconFolder();

        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Before anything else that could record progress or send a message. Loads the cached
        // verdict synchronously so a ban is in force on frame one, then refreshes in the background.
        BanService.Initialise(PluginInterface.GetPluginConfigDirectory());

        // Completion lives in its own two files, NOT in the config. Load it BEFORE migrating so
        // migration can move legacy progress into it rather than dropping it on the floor.
        _store = new CompletionStore(PluginInterface.GetPluginConfigDirectory());
        _store.Load();

        _config.MigrateIfNeeded(_store);
        SaveConfig();   // persist any id / sort rewrites migration performed

        // Official challenges synced from the public repo. Loaded before anything reads the
        // catalogue, and published to ChallengeCatalog so every query sees them.
        _official = new OfficialCatalog(PluginInterface.GetPluginConfigDirectory());
        _official.Load();
        ChallengeCatalog.Official = _official;

        // Audio is stood up before anything that could raise a cue. Entries come from config
        // rather than being compiled in: an index that turns out to be silent has to be
        // changeable by ear, not by rebuild.
        // One-time correction of cues that provably cannot sound. Entries 55 and 85 were stored
        // against the UI bank, which holds 54 sounds — anything above 53 is accepted by the
        // engine and plays nothing, so those two were silent by construction. Overwriting them is
        // not overriding a preference; the stored values were never capable of making a noise.
        // Version 2: the reset cue moved to its own zingle. Version 1 had already run on installs
        // updated mid-flight, so gating on < 1 would have left them on the old SE_UI placeholder.
        // Version 3: the three zingle cues moved from game archives to shipped .wav files. They
        // load correctly from the archives and are silenced somewhere in the game's mixer that no
        // category, volume or bus write could get past, so they now play through Windows instead.
        if (_config.SoundConfigVersion < 3)
        {
            _config.ProgressSoundPath  = GameSound.DefaultProgressBank;
            _config.ProgressSoundEntry = GameSound.DefaultProgressEntry;
            _config.CompleteSoundPath  = GameSound.DefaultCompleteBank;
            _config.CompleteSoundEntry = GameSound.DefaultCompleteEntry;
            _config.ResetSoundPath     = GameSound.DefaultResetBank;
            _config.ResetSoundEntry    = GameSound.DefaultResetEntry;
            _config.ZoneSoundPath      = GameSound.DefaultZoneBank;
            _config.ZoneSoundEntry     = GameSound.DefaultZoneEntry;
            _config.SoundConfigVersion = 3;

            Log.Information("[Sound] cue defaults reset — zingles now play as shipped .wav files.");
        }

        GameSound.ProgressBank  = _config.ProgressSoundPath;
        GameSound.ProgressEntry = _config.ProgressSoundEntry;
        GameSound.CompleteBank  = _config.CompleteSoundPath;
        GameSound.CompleteEntry = _config.CompleteSoundEntry;
        GameSound.ResetBank     = _config.ResetSoundPath;
        GameSound.ResetEntry    = _config.ResetSoundEntry;
        GameSound.ZoneBank      = _config.ZoneSoundPath;
        GameSound.ZoneEntry     = _config.ZoneSoundEntry;
        Sound = new SoundService();
        Sound.Attach();

        // Stood up before the tracker: an item condition evaluated on the first tick must find a
        // watcher, not a null. It starts dirty, so the first read builds the map.
        Inventory = new InventoryWatcher();
        Inventory.Attach();

        _tracker = new ChallengeTracker(_config, _store, SaveConfig);
        _sync    = new ChallengeSyncService(_official, _config);
        _dialogs = new Dialogs(_config, _store, _tracker, SaveConfig);

        // Construct the Panache-backed windows ONLY if the library actually loaded. Merely
        // referencing these types resolves PanacheUI, so guarding construction is what keeps the
        // plugin alive — and able to explain itself — when the library is missing.
        if (PanacheAvailability.IsAvailable)
        {
            _toast         = new CompletionToast(TextureProvider);
            _progressToast = new ProgressToast(TextureProvider, RevealChallenge);
            _mainWindow    = new MainWindow(_config, _store, TextureProvider, SaveConfig, _tracker,
                                            _dialogs, _sync);
        }
        else
        {
            Log.Warning("PanacheUI unavailable — running the plain-ImGui fallback UI. "
                      + "Completion popups still work, drawn in ImGui.");
        }

        _fallbackProgressToast = new FallbackProgressToast(RevealChallenge);
        _statusWindow          = new StatusWindow(_config);

        if (_mainWindow != null)
            _mainWindow.OnOpenStatus = () => _statusWindow.IsVisible = !_statusWindow.IsVisible;

        // One handler per event, which fans out to sound, fly text and the popup IN THAT ORDER.
        // Subscribing the popup queues directly used to put the cue behind the display; now the
        // sound request goes out first and independently, so nothing downstream can silence it.
        _tracker.Completed  += OnCompleted;
        _tracker.Progressed += OnProgressed;

        _fallbackWindow = new FallbackWindow(_config, _store, _tracker, _dialogs, _sync,
                                             SaveConfig, RestoreFromPermanent);
        _fallbackWindow.OnOpenStatus = () => _statusWindow.IsVisible = !_statusWindow.IsVisible;
        _tracker.Attach();

        // Pick up new official challenges shortly after load, without blocking startup.
        if (_config.AutoSync)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await _sync.SyncAsync();
                if (r.Ok && (r.Added > 0 || r.Updated > 0))
                {
                    Log.Information($"[Sync] auto-sync: {r.Message}");
                    _tracker.Invalidate();
                }
            });
        }

#if DEV_BUILD
        _creatorWindow = new ChallengeCreatorWindow(_config, _store, SaveConfig, _tracker, _toastQueue);

        if (PanacheAvailability.IsAvailable)
            _soundTestWindow = new SoundTestWindow(TextureProvider);

        if (_mainWindow != null)
        {
            _mainWindow.OnOpenCreator   = () => _creatorWindow.IsVisible = true;
            _mainWindow.OnOpenSoundTest = () =>
            {
                if (_soundTestWindow != null) _soundTestWindow.IsVisible = true;
            };
        }
#endif

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "FFXIV Miscellaneous Challenges — toggle the window. "
                        + "/tchallenges center brings a lost or locked window back on screen. "
                        + "/tchallenges reset asks before wiping progress. "
                        + "/tchallenges sfx auditions and sets the sound cues.",
        });
        CommandManager.AddHandler(CmdShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /tchallenges.",
        });

        PluginInterface.UiBuilder.Draw         += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi   += OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenMainUi;

        var (done, total) = ChallengeCatalog.OverallProgress(_config, _store);
        Log.Info($"TieriChallengesFFXIV {PluginVersion.DisplayLong} "
               + $"({(IsDevBuild ? "DEV" : "public")} build) — {total} challenges, {done} complete.");
    }

    /// <summary>
    /// Tell PanacheUI to load icons from the copy this plugin ships beside its own DLL.
    /// </summary>
    /// <remarks>
    /// Best-effort and deliberately silent on failure: no icon is worth failing construction over,
    /// and PanacheUI already degrades a missing icon to a placeholder rather than throwing. If the
    /// folder is not there, <c>FolderOverride</c> ignores the value and the framework's own search
    /// runs exactly as before — which is what keeps dev builds working from devPlugins\PanacheUI.
    /// </remarks>
    private static void TrySetIconFolder()
    {
        try
        {
            string? dir = PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrEmpty(dir)) return;

            string icons = Path.Combine(dir!, "Icons");
            if (Directory.Exists(icons)) PanacheIcons.FolderOverride = icons;
        }
        catch
        {
            // Nothing here is worth a crash on startup.
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenMainUi;
        PluginInterface.UiBuilder.Draw         -= DrawUI;

        CommandManager.RemoveHandler(CmdShort);
        CommandManager.RemoveHandler(CmdMain);

        _tracker.Progressed -= OnProgressed;
        _tracker.Completed  -= OnCompleted;
        _tracker.Dispose();
        Inventory.Dispose();
        Sound.Dispose();
        _toast?.Dispose();
        _progressToast?.Dispose();
#if DEV_BUILD
        _soundTestWindow?.Dispose();
#endif
        _mainWindow?.Dispose();
        SaveConfig();

        Log.Info("TieriChallengesFFXIV unloaded.");
    }

    internal void SaveConfig()
    {
        try { PluginInterface.SavePluginConfig(_config); }
        catch (Exception ex) { Log.Error(ex, "Failed to save config"); }
    }

    private void OnCommand(string command, string args)
    {
        try
        {
            string raw = args.Trim();

            // Prefix command rather than a switch case — it takes an argument.
            if (raw.StartsWith("sfx", StringComparison.OrdinalIgnoreCase))
            {
                HandleSfxCommand(raw.Substring(3).Trim());
                return;
            }

            switch (raw.ToLowerInvariant())
            {
                case "":
                    _windowVisible = !_windowVisible;
                    break;

                case "reset":
                    // Never wipe from a chat command directly — route through the same
                    // confirmation dialog the Reset control uses.
                    _dialogs.RequestReset();
                    break;

                case "center":
                case "centre":
                    CenterWindow();
                    break;

                case "status":
                {
                    var (done, total) = ChallengeCatalog.OverallProgress(_config, _store);
                    ChatGui.Print($"[Challenges] {done}/{total} complete "
                                + $"({ChallengeCatalog.Percent(done, total) * 100f:0}%).");
                    break;
                }

                case "sync":
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        var r = await _sync.SyncAsync();
                        ChatGui.Print("[Challenges] " + r.Message);
                        _tracker.Invalidate();
                    });
                    break;

#if DEV_BUILD
                case "creator":
                    _creatorWindow.IsVisible = !_creatorWindow.IsVisible;
                    break;

                case "sounds":
                    if (_soundTestWindow != null)
                        _soundTestWindow.IsVisible = !_soundTestWindow.IsVisible;
                    else
                        ChatGui.PrintError("[Challenges] Sound test needs PanacheUI — "
                                         + "use /tchallenges sfx instead.");
                    break;

                // The public-preview toggle hides itself in preview mode, so this is the way
                // back out.
                case "preview":
                    _config.PublicPreview = !_config.PublicPreview;
                    SaveConfig();
                    ChatGui.Print($"[Challenges] Public preview {(_config.PublicPreview ? "ON" : "OFF")}.");
                    break;
#endif

                default:
                    ChatGui.PrintError("[Challenges] Usage: /tchallenges [center|reset|status|sync|sfx]");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "OnCommand exception");
        }
    }

    private void DrawUI()
    {
        if (_drawTestEntry != 0 && Environment.TickCount64 >= _drawTestAtMs)
        {
            uint entry = _drawTestEntry;
            _drawTestEntry = 0;
            try
            {
                ChatGui.Print($"[Challenges] entry {entry} — DRAW thread now.");
                GameSound.Play(GameSound.UiBank, entry);
            }
            catch (Exception ex) { Log.Error(ex, "Draw-thread sound probe failed"); }
        }

        // One place converts the step to a multiplier; every surface reads it.
        UiScale.Set(_config.UiScale);

        // Ban check, every frame. Cheap by construction: Evaluate short-circuits unless the
        // logged-in identity actually changed, so the steady-state cost is one string compare.
        //
        // Placed here rather than on a login event on purpose — this runs whether or not any
        // window is open, so a ban lands even on a session where the player never opens the UI,
        // and it re-evaluates on a character switch without needing a second hook.
        BanService.Evaluate();

        if (BanService.IsBanned)
        {
            // The whole plugin collapses to one notice. Nothing else is drawn, the tracker is
            // stopped, and reports are refused — see BanNotice and ChallengeTracker.
            BanNotice.Draw();
            return;
        }

        // Exactly one renderer runs per frame. Visibility is held here rather than in either
        // window so flipping the toggle does not close the window under the user.
        try
        {
            if (UsePanache)
            {
                _mainWindow!.IsVisible = _windowVisible;
                _mainWindow.Draw();
                _windowVisible = _mainWindow.IsVisible;
            }
            else
            {
                _fallbackWindow.Draw(ref _windowVisible);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Main window draw exception"); }

        // Modals and the status popup are renderer-agnostic and drawn once, at root scope.
        try { _dialogs.Draw(); }
        catch (Exception ex) { Log.Error(ex, "Dialogs draw exception"); }

        try { _statusWindow.Draw(); }
        catch (Exception ex) { Log.Error(ex, "Status window draw exception"); }

        // Exactly one toast renderer per frame — TryCurrent advances the clock, so drawing both
        // would double the fade speed and drop popups.
        try
        {
            if (UsePanache) _toast!.Draw(_toastQueue);
            else            _fallbackToast.Draw(_toastQueue);
        }
        catch (Exception ex) { Log.Error(ex, "Completion toast draw exception"); }

        // Same one-renderer-per-frame rule as the completion toast — ProgressQueue.TryCurrent
        // advances its own clock.
        try
        {
            if (UsePanache) _progressToast!.Draw(_progressQueue);
            else            _fallbackProgressToast.Draw(_progressQueue);
        }
        catch (Exception ex) { Log.Error(ex, "Progress toast draw exception"); }

#if DEV_BUILD
        try { _creatorWindow.Draw(); }
        catch (Exception ex) { Log.Error(ex, "ChallengeCreator draw exception"); }

        try { _soundTestWindow?.Draw(); }
        catch (Exception ex) { Log.Error(ex, "SoundTest draw exception"); }

        // World wireframes only while the creator is open — this is a placement aid, not a
        // permanent HUD element.
        if (_creatorWindow.IsVisible)
        {
            try
            {
                _creatorWindow.Overlay.Draw(_config, _store, _creatorWindow.DraftAreas,
                                            _creatorWindow.SelectedAreaIndex);
            }
            catch (Exception ex) { Log.Error(ex, "AreaOverlay draw exception"); }
        }
#endif
    }

    private void OnOpenMainUi() => _windowVisible = true;

    /// <summary>
    /// Audition and choose the sound cues by ear.
    ///
    /// <para>Exists because a bank index is not guaranteed to hold audible audio, and the only
    /// way to find out is to listen. Without this, every "I hear nothing" costs a rebuild to
    /// test one number.</para>
    ///
    /// <para><c>sfx &lt;n&gt;</c> plays entry n once, changing nothing.
    /// <c>sfx progress|complete|reset &lt;n&gt;</c> assigns it, saves, and plays it back.</para>
    /// </summary>
    private void HandleSfxCommand(string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 && parts[0].Equals("buses", StringComparison.OrdinalIgnoreCase))
        {
            GameSound.DumpBuses();
            return;
        }

        // Changes the running game's audio, not a plugin setting — a probe, not a fix.
        if (parts.Length == 3 && parts[0].Equals("bus", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Sound.SoundBus>(parts[1], true, out var bus)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out float busVol))
        {
            GameSound.SetBusVolume(bus, busVol);
            return;
        }

        if (parts.Length == 1 && parts[0].Equals("dump", StringComparison.OrdinalIgnoreCase))
        {
            GameSound.DumpBankHeader();
            ChatGui.Print("[Challenges] Bank header written to the Dalamud log.");
            return;
        }

        // Bare "scan" walks the whole bank. SE_UI reports 54 sounds in VFXEditor's Scd editor, so
        // the engine's usable indices are 0–53 — which is precisely why 55 and 85 were silent.
        // PlaySound accepts an out-of-range index, returns a pooled SoundData, reports active,
        // and plays nothing, so there is no error to catch. Scanning the real range is the guard.
        if (parts.Length == 1 && parts[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            Sound.StartScan(0, 53);
            return;
        }

        if (parts.Length == 1 && parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            Sound.StopScan();
            return;
        }

        // Walk a range and name each entry as it plays. An .scd index is not a promise of audio —
        // empty slots play silently and report success, which is why 55 and 85 did nothing.
        if (parts.Length == 3 && parts[0].Equals("scan", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(parts[1], out uint from) && uint.TryParse(parts[2], out uint to))
        {
            Sound.StartScan(from, to);
            return;
        }

        // Test the MIDI-note hypothesis: if 55 and 85 are sequenced rather than sampled entries,
        // the note is what selects the audio and zero would legitimately produce nothing.
        if (parts.Length == 3 && parts[0].Equals("note", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(parts[1], out uint noteEntry) && int.TryParse(parts[2], out int note))
        {
            ChatGui.Print($"[Challenges] entry {noteEntry} with MIDI note {note}.");
            GameSound.Play(GameSound.UiBank, noteEntry, note);
            return;
        }

        // A/B the two calling contexts. Cues used to be played from the draw path, where entry 50
        // was audible; they now play from the framework tick. If the draw copy sounds and the
        // framework one does not, the thread is the bug and not the bank entry.
        if (parts.Length == 2 && parts[0].Equals("thread", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(parts[1], out uint threadEntry))
        {
            ChatGui.Print($"[Challenges] entry {threadEntry} — FRAMEWORK thread now.");
            Sound.PlayEntry(threadEntry);

            _drawTestEntry = threadEntry;
            _drawTestAtMs  = Environment.TickCount64 + 1500;
            ChatGui.Print("[Challenges] Draw-thread copy in 1.5s — tell me which one you heard.");
            return;
        }

        // Audition only — deliberately does not persist, so probing cannot clobber a good setting.
        if (parts.Length == 1 && uint.TryParse(parts[0], out uint probe))
        {
            ChatGui.Print($"[Challenges] Playing {GameSound.UiBank} entry {probe}.");
            Sound.PlayEntry(probe);
            return;
        }

        // <cue> <entry>          change the number, keep the bank
        // <cue> <path> [entry]   change the bank too; entry defaults to 0, which is right for a
        //                        single-sound bank like a zingle
        if (parts.Length >= 2 && IsCueName(parts[0]))
        {
            string cue = parts[0].ToLowerInvariant();

            if (uint.TryParse(parts[1], out uint onlyEntry))
            {
                AssignCue(cue, null, onlyEntry);
                return;
            }

            if (parts[1].Contains('/'))
            {
                uint entry = parts.Length >= 3 && uint.TryParse(parts[2], out uint pathEntry)
                    ? pathEntry
                    : 0u;

                AssignCue(cue, parts[1], entry);
                return;
            }
        }

        ChatGui.Print($"[Challenges] Currently: step {_config.ProgressSoundPath} #{_config.ProgressSoundEntry}");
        ChatGui.Print($"[Challenges]            complete {_config.CompleteSoundPath} #{_config.CompleteSoundEntry}");
        ChatGui.Print($"[Challenges]            reset {_config.ResetSoundPath} #{_config.ResetSoundEntry}");
        ChatGui.Print("[Challenges] /tchallenges sfx <n>  —  audition an SE_UI entry");
        ChatGui.Print("[Challenges] /tchallenges sfx scan  —  walk the whole bank (entries 0–53)");
        ChatGui.Print("[Challenges] /tchallenges sfx scan <from> <to>  —  play a range, naming each");
        ChatGui.Print("[Challenges] /tchallenges sfx thread <n>  —  same entry, framework vs draw");
        ChatGui.Print("[Challenges] /tchallenges sfx note <n> <midiNote>  —  try a sequenced entry");
        ChatGui.Print("[Challenges] /tchallenges sfx buses  —  list every sound bus and its volume");
        ChatGui.Print("[Challenges] /tchallenges sfx bus <name> <0..1>  —  set one (changes the GAME's audio)");
        ChatGui.Print("[Challenges] /tchallenges sfx dump  —  log the bank header (how many entries)");
        ChatGui.Print("[Challenges] /tchallenges sfx stop  —  cancel a scan and silence all cues");
        ChatGui.Print("[Challenges] /tchallenges sfx progress|complete|reset <n>  —  set that cue's entry");
        ChatGui.Print("[Challenges] /tchallenges sfx progress|complete|reset <path.scd> [n]  —  set its bank");
        ChatGui.Print($"[Challenges] Currently: step {_config.ProgressSoundEntry}, "
                    + $"complete {_config.CompleteSoundEntry}, reset {_config.ResetSoundEntry}.");
    }

    private static bool IsCueName(string s) =>
        s.Equals("progress", StringComparison.OrdinalIgnoreCase)
     || s.Equals("complete", StringComparison.OrdinalIgnoreCase)
     || s.Equals("reset",    StringComparison.OrdinalIgnoreCase)
     || s.Equals("zone",     StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Point a cue at a bank and/or entry, persist it, and play it back so the choice is
    /// confirmed by ear rather than by a chat line.
    /// </summary>
    /// <param name="bank">Null keeps the cue's current bank and changes only the entry.</param>
    private void AssignCue(string cue, string? bank, uint entry)
    {
        string path;

        switch (cue)
        {
            case "progress":
                if (bank != null) _config.ProgressSoundPath = bank;
                _config.ProgressSoundEntry = entry;
                GameSound.ProgressBank     = path = _config.ProgressSoundPath;
                GameSound.ProgressEntry    = entry;
                break;

            case "complete":
                if (bank != null) _config.CompleteSoundPath = bank;
                _config.CompleteSoundEntry = entry;
                GameSound.CompleteBank     = path = _config.CompleteSoundPath;
                GameSound.CompleteEntry    = entry;
                break;

            case "zone":
                if (bank != null) _config.ZoneSoundPath = bank;
                _config.ZoneSoundEntry = entry;
                GameSound.ZoneBank     = path = _config.ZoneSoundPath;
                GameSound.ZoneEntry    = entry;
                break;

            default:
                if (bank != null) _config.ResetSoundPath = bank;
                _config.ResetSoundEntry = entry;
                GameSound.ResetBank     = path = _config.ResetSoundPath;
                GameSound.ResetEntry    = entry;
                break;
        }

        SaveConfig();
        ChatGui.Print($"[Challenges] {cue} cue → {path} entry {entry}.");

        Sound.Play(cue switch
        {
            "progress" => SoundService.Cue.ObjectiveProgress,
            "complete" => SoundService.Cue.ChallengeComplete,
            "zone"     => SoundService.Cue.ZoneAvailable,
            _          => SoundService.Cue.ResetConfirmed,
        });
    }

    /// <summary>
    /// A challenge completed. Order is the design: the sound request goes out FIRST and
    /// unconditionally, because it is the highest-priority feedback and must not be able to fail
    /// for a UI reason. Fly text second (drawn by the game, so it shows with no window open).
    /// The popup last, since it is the only part that can be delayed behind another.
    /// </summary>
    private void OnCompleted(CompletionEvent e)
    {
        Sound.Play(SoundService.Cue.ChallengeComplete);
        FlyTextService.ShowComplete(e.Title);
        _toastQueue.Enqueue(e);
    }

    /// <summary>One step landed. Same ordering rule as <see cref="OnCompleted"/>.</summary>
    private void OnProgressed(ProgressEvent e)
    {
        Sound.Play(SoundService.Cue.ObjectiveProgress);
        FlyTextService.ShowProgress(e.Title, e.Done, e.Total);

        // Interrupts whatever is on screen rather than queueing behind it — the newest count is
        // the only one worth reading, and it is cumulative anyway.
        _progressQueue.Show(e);
    }

    /// <summary>
    /// Open the window and reveal a challenge in it. Called by the progress notification's Show
    /// button, routed through here rather than wired to a window directly so the button keeps
    /// working when PanacheUI is off and the fallback renderer owns the list.
    /// </summary>
    /// <summary>
    /// Bring the window back to the middle of the screen — the recovery path for a window dragged
    /// off-screen, or left on a monitor that is no longer attached.
    /// </summary>
    /// <remarks>
    /// <para><b>Unlocks first, deliberately.</b> A locked window has
    /// <c>ImGuiWindowFlags.NoMove</c>, and ImGui ignores <c>SetNextWindowPos</c> for it — so
    /// centring a locked window would appear to do nothing at all. Since the most likely reason
    /// to type this is "I cannot reach my window", silently failing in exactly that case would be
    /// the worst possible behaviour. The unlock is announced rather than quiet, because it
    /// changes a setting the user deliberately turned on.</para>
    ///
    /// <para><b>Opens the window if it is closed.</b> "Centre it" means "put it where I can see
    /// it"; centring something invisible is not a useful outcome.</para>
    /// </remarks>
    private void CenterWindow()
    {
        _windowVisible = true;

        bool wasLocked = _config.WindowLocked;
        if (wasLocked)
        {
            _config.WindowLocked = false;
            SaveConfig();
        }

        if (UsePanache) _mainWindow!.RequestCenter();
        else            _fallbackWindow.RequestCenter();

        ChatGui.Print(wasLocked
            ? "[Challenges] Window unlocked and centred."
            : "[Challenges] Window centred.");
    }

    private void RevealChallenge(ProgressEvent e)
    {
        try
        {
            _windowVisible = true;

            if (UsePanache) _mainWindow!.FocusChallenge(e.Id, e.Category);
            else            _fallbackWindow.FocusChallenge(e.Id, e.Category);
        }
        catch (Exception ex) { Log.Error(ex, "RevealChallenge failed"); }
    }

    /// <summary>
    /// Replenish current completion data from the permanent ledger, restoring each challenge's
    /// ORIGINAL date rather than stamping today. Lives here rather than in a renderer so both
    /// UIs share one implementation.
    /// </summary>
    internal void RestoreFromPermanent()
    {
        int restored = _store.RestoreFromPermanent();
        _config.StateVersion++;
        _tracker.Invalidate();

        ChatGui.Print(restored > 0
            ? $"[Challenges] Restored {restored} completion(s) from permanent storage."
            : "[Challenges] Nothing to restore — current data already matches permanent storage.");
    }
}
