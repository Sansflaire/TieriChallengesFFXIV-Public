using System;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
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
    /// Partial progress for adventures and quest chains — how far through a multi-objective
    /// challenge the player is. Static for the same reason as <see cref="Inventory"/>: it is
    /// consulted from the tracker tick, from <c>ChallengeCatalog</c> while building every row, and
    /// from <c>ZoneIndex</c> while deciding which zone a chain currently belongs to. Threading an
    /// instance through all three would touch every call site of each to gain nothing.
    /// </summary>
    internal static ProgressStore Progress { get; private set; } = null!;

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
    /// The race panel, split the same way as the toasts: a race must remain playable when
    /// PanacheUI is off, because its clock is not decoration.
    /// </summary>
    private readonly FallbackRacePrompt _fallbackRacePrompt;
    private          RacePromptToast?   _racePrompt;

    /// <summary>
    /// Live character/zone readout, on demand. Renderer-agnostic and drawn at root scope, so both
    /// UIs open the same popup rather than each carrying its own copy of the text.
    /// </summary>
    private readonly StatusWindow _statusWindow;

    /// <summary>
    /// The requirement sheet for quests and adventures. Renderer-agnostic and drawn at root scope,
    /// like the status popup — both renderers open the same window rather than each carrying one.
    /// </summary>
    private readonly ObjectiveWindow _objectiveWindow;

    /// <summary>Player-facing sound, notification and colour settings. Renderer-agnostic.</summary>
    private readonly SettingsWindow _settingsWindow;

    /// <summary>The searchable manual, parsed from the shipped HELP.md. Renderer-agnostic.</summary>
    private readonly HelpWindow _helpWindow = new();

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
    /// <summary>One-shot latch for the map geometry dump — see DrawUI.</summary>
    private bool _mapDiagDone;

    private readonly ChallengeCreatorWindow _creatorWindow;

    /// <summary>
    /// Investigation harness for questions only the running game can answer. Not a feature —
    /// see <see cref="LiveProbe"/>. Remove once OPEN_QUESTIONS Q13 is recorded.
    /// </summary>
    private readonly LiveProbeWindow _probeWindow = new();

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

        // Partial progress. Loaded before the catalogue is read for the first time — a chain's row
        // is built from whichever step the player is on, so an unloaded store would render every
        // chain at step one for the first frame.
        Progress = new ProgressStore(PluginInterface.GetPluginConfigDirectory());
        Progress.Load();

        _config.MigrateIfNeeded(_store);
        SaveConfig();   // persist any id / sort rewrites migration performed

        // Bound before anything can raise a cue or paint a pixel: both read the config through a
        // static, and an unbound Palette would draw the first frame in shipped colours before
        // snapping to the user's.
        Palette.Bind(_config);
        SoundService.Bind(_config);
        ApplySoundSettings();

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
            _racePrompt    = new RacePromptToast(TextureProvider, _config, _store, _tracker, SaveConfig);
        }
        else
        {
            Log.Warning("PanacheUI unavailable — running the plain-ImGui fallback UI. "
                      + "Completion popups still work, drawn in ImGui.");
        }

        _fallbackProgressToast = new FallbackProgressToast(RevealChallenge);
        _fallbackRacePrompt    = new FallbackRacePrompt(_config, _store, _tracker, SaveConfig);
        _statusWindow          = new StatusWindow(_config);
        _objectiveWindow       = new ObjectiveWindow(_config, _store, _tracker);
        _settingsWindow        = new SettingsWindow(_config, SaveConfig, ApplySoundSettings);
        _settingsWindow.ApplyDurations();

        if (_mainWindow != null)
        {
            _mainWindow.OnOpenStatus     = () => _statusWindow.IsVisible = !_statusWindow.IsVisible;
            _mainWindow.OnOpenObjectives = id => _objectiveWindow.Toggle(id);
            _mainWindow.OnOpenSettings   = () => _settingsWindow.IsVisible = !_settingsWindow.IsVisible;
            _mainWindow.OnOpenHelp       = () => _helpWindow.Open();
        }

        // One handler per event, which fans out to sound, fly text and the popup IN THAT ORDER.
        // Subscribing the popup queues directly used to put the cue behind the display; now the
        // sound request goes out first and independently, so nothing downstream can silence it.
        _tracker.Completed  += OnCompleted;
        _tracker.Progressed += OnProgressed;
        _tracker.RaceEnded  += OnRaceEnded;

        _fallbackWindow = new FallbackWindow(_config, _store, _tracker, _dialogs, _sync,
                                             SaveConfig, RestoreFromPermanent);
        _fallbackWindow.OnOpenStatus     = () => _statusWindow.IsVisible = !_statusWindow.IsVisible;
        _fallbackWindow.OnOpenObjectives = id => _objectiveWindow.Toggle(id);
        _fallbackWindow.OnOpenSettings   = () => _settingsWindow.IsVisible = !_settingsWindow.IsVisible;
        _fallbackWindow.OnOpenHelp       = () => _helpWindow.Open();
        _tracker.Attach();

        // Pick up new official challenges shortly after load, without blocking startup.
        if (_config.AutoSync) StartAutoSync();

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

    /// <summary>
    /// Cancels the pending auto-sync delay when the plugin unloads. Without it the jittered wait
    /// below is fire-and-forget: a dev reload every few minutes would leave a task sleeping for up
    /// to <see cref="AutoSyncJitterSeconds"/>, waking inside a disposed plugin to touch a config
    /// and a tracker that are gone.
    /// </summary>
    private readonly System.Threading.CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Upper bound on the random delay before a routine auto-sync, in seconds.
    ///
    /// <para><b>Why jitter at all.</b> Plugin load is normally spread across the day, but the
    /// events that matter are correlated: a plugin update, a Dalamud hotfix or a game patch has
    /// everyone reload inside the same few minutes, and every client then fetches at once. The
    /// hourly quest rotation planned in <c>docs/Challenge Tokens and Quests.md</c> makes this
    /// worse by construction — a fixed rotation boundary synchronises every client on the
    /// planet — so the spreading belongs here, before anything depends on it.</para>
    /// </summary>
    private const int AutoSyncJitterSeconds = 300;

    /// <summary>
    /// Fetch the official catalogue in the background, after a random delay.
    ///
    /// <para><b>The first sync is never delayed.</b> A player who has never synced has no
    /// challenges at all, and making them stare at an empty list for up to five minutes to solve a
    /// crowding problem they are not part of gets the trade backwards — one client is not a herd.
    /// Jitter applies only to routine re-syncs, where the data is already on disk and a few
    /// minutes' staleness costs nothing.</para>
    /// </summary>
    private void StartAutoSync()
    {
        bool firstEver = _config.LastSyncUtc == DateTime.MinValue;

        // Captured HERE, not inside the task. Reading _shutdown.Token from the task body races
        // Dispose: on a fast reload the source can already be disposed by the time the task is
        // scheduled, and the property throws ObjectDisposedException — which the catch below would
        // then report as a sync failure and log through a plugin that has already unloaded.
        var token = _shutdown.Token;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                if (!firstEver)
                {
                    int delay = System.Random.Shared.Next(AutoSyncJitterSeconds + 1);
                    Diag.Debug($"[Sync] auto-sync in {delay}s (jittered).");
                    await System.Threading.Tasks.Task
                        .Delay(TimeSpan.FromSeconds(delay), token)
                        .ConfigureAwait(false);
                }

                // The jitter widened the unload window from milliseconds to up to five minutes,
                // so re-check before touching config and tracker rather than relying on the
                // Delay's own cancellation alone.
                if (token.IsCancellationRequested) return;

                var r = await _sync.SyncAsync().ConfigureAwait(false);
                if (r.Ok && (r.Added > 0 || r.Updated > 0))
                {
                    Diag.Info($"[Sync] auto-sync: {r.Message}");
                    _tracker.Invalidate();
                }
            }
            catch (OperationCanceledException)
            {
                // Plugin unloaded while waiting. Normal, and not worth a log line.
            }
            catch (Exception ex)
            {
                Diag.Error($"[Sync] auto-sync failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        // Ahead of everything else: the auto-sync task touches _config and _tracker, so it has to
        // be told to stop before either is torn down.
        _shutdown.Cancel();

        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenMainUi;
        PluginInterface.UiBuilder.Draw         -= DrawUI;

        CommandManager.RemoveHandler(CmdShort);
        CommandManager.RemoveHandler(CmdMain);

        _tracker.RaceEnded  -= OnRaceEnded;
        _tracker.Progressed -= OnProgressed;
        _tracker.Completed  -= OnCompleted;
        _tracker.Dispose();
        Inventory.Dispose();
        Sound.Dispose();
        _toast?.Dispose();
        _progressToast?.Dispose();
        _racePrompt?.Dispose();
#if DEV_BUILD
        _soundTestWindow?.Dispose();
        LiveProbe.Detach();
#endif
        _mainWindow?.Dispose();
        _shutdown.Dispose();
        SaveConfig();

        Log.Info("TieriChallengesFFXIV unloaded.");
    }

    internal void SaveConfig()
    {
        try { PluginInterface.SavePluginConfig(_config); }
        catch (Exception ex) { Log.Error(ex, "Failed to save config"); }
    }

    /// <summary>
    /// Push the audio settings into <see cref="GameSound"/>'s statics. Called at startup and
    /// whenever the settings window changes one — mirroring on change rather than reading the
    /// config on every cue keeps the playback path free of a config reference.
    /// </summary>
    internal void ApplySoundSettings()
    {
        GameSound.Volume = Math.Clamp(_config.SoundVolume, 0f, 1f);
        GameSound.Muted  = _config.SoundMuted;
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

                case "probe":
                    _probeWindow.IsVisible = !_probeWindow.IsVisible;
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

        HandleEscape();

#if DEV_BUILD
        // One-shot map geometry dump for the zone we are in. Fired from the DRAW loop, not the
        // constructor: the ctor runs off the main thread and ObjectTable.LocalPlayer throws there.
        if (!_mapDiagDone && ClientState.IsLoggedIn)
        {
            _mapDiagDone = true;
            MapPinService.LogZoneDiagnostics();

            // Parse the help document once at startup in dev builds, so a format mistake shows up
            // in the log rather than the first time a player opens the window.
            int sections = HelpLibrary.Sections.Count;
            int keywords = 0;
            foreach (var s in HelpLibrary.Sections) keywords += s.Keywords.Count;
            Log.Information($"[Help] parsed {sections} section(s), {keywords} hidden keyword(s). "
                          + $"Error: '{HelpLibrary.Error}'");
        }
#endif

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

        try { _objectiveWindow.Draw(); }
        catch (Exception ex) { Log.Error(ex, "Objective window draw exception"); }

        try { _settingsWindow.Draw(); }
        catch (Exception ex) { Log.Error(ex, "Settings window draw exception"); }

        try { _helpWindow.Draw(); }
        catch (Exception ex) { Log.Error(ex, "Help window draw exception"); }

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

        // The race panel. Not queued and not timed — it mirrors live tracker state, so it decides
        // for itself whether there is anything to show.
        try
        {
            if (UsePanache) _racePrompt!.Draw();
            else            _fallbackRacePrompt.Draw();
        }
        catch (Exception ex) { Log.Error(ex, "Race prompt draw exception"); }

#if DEV_BUILD
        try { _probeWindow.Draw(); }
        catch (Exception ex) { Diag.Error($"[Probe] window failed: {ex.Message}"); }

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
    /// <summary>
    /// <b>Escape stops whatever the plugin started.</b> Standing rule, Trist 2026-08-26.
    ///
    /// <para>Runs once per frame, before any window draws, so a release lands on the very frame
    /// the key goes down rather than one frame later. One call site rather than one per surface:
    /// a rule that has to be remembered separately by every new window is a rule that will be
    /// missed by the next one.</para>
    ///
    /// <para><b>What it does:</b> hands the keyboard back to the game, drops text focus, and
    /// closes the transient things the plugin put on screen — menus, the filter dropdown, and the
    /// settings, objectives and info windows.</para>
    ///
    /// <para><b>What it deliberately does NOT do:</b> abandon a running race, wipe a search term,
    /// collapse a row being read, or close the main window. Escape is pressed constantly in FFXIV
    /// to dismiss game windows, so anything it does here has to cost nothing — a key that could
    /// silently end a timed run the player was three minutes into is worse than no rule at all.
    /// The dev Creator is left alone for the same reason: it holds unsaved authoring state.</para>
    ///
    /// <para>The press is READ, never consumed. The game still receives it and still closes its
    /// own windows, which is what a player expects Escape to keep doing.</para>
    /// </summary>
    private void HandleEscape()
    {
        try
        {
            if (!ImGui.IsKeyPressed(ImGuiKey.Escape, false)) return;

            // Panache-side claims: keyboard focus, the menu bar, the filter dropdown. Null when
            // PanacheUI could not load, in which case there is nothing of its to release.
            _mainWindow?.ReleaseInput();

            // Renderer-agnostic windows the player opened. Closed rather than left behind, since
            // "stop what you are doing" plainly includes the panel sitting over the game.
            _settingsWindow.IsVisible = false;
            _statusWindow.IsVisible   = false;
            _helpWindow.IsVisible     = false;
            _objectiveWindow.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Escape handler failed");
        }
    }

    /// <summary>
    /// Should visual notifications be held right now? Sound is deliberately NOT gated by this —
    /// a cue cannot obscure anything, and this plugin treats audio as its highest-priority
    /// feedback (see <see cref="SoundService"/>). This suppresses what would be drawn OVER the
    /// fight, not what tells you the fight earned you something.
    /// </summary>
    private bool NotificationsHeld
    {
        get
        {
            if (!_config.SuppressInCombat) return false;

            try
            {
                return Condition[ConditionFlag.InCombat] || Condition[ConditionFlag.BoundByDuty];
            }
            catch { return false; }
        }
    }

    private void OnCompleted(CompletionEvent e)
    {
        Sound.Play(SoundService.Cue.ChallengeComplete);

        if (_config.ShowFlyText && !NotificationsHeld) FlyTextService.ShowComplete(e.Title);
        if (_config.ShowCompletionBanner && !NotificationsHeld) _toastQueue.Enqueue(e);
    }

    /// <summary>
    /// A race run ended. Announced in chat rather than through a popup, for two reasons: a run can
    /// end for four different reasons and only one of them is worth celebrating, and the corner of
    /// the screen the popup would use is the corner the race panel itself occupies.
    ///
    /// <para>A FIRST finish also raises <see cref="CompletionEvent"/> through the normal path, so
    /// the fanfare and toast happen there. This handler must not duplicate them — hence no sound
    /// on <see cref="RaceOutcome.Finished"/> unless it was a repeat run setting a new best, which
    /// nothing else announces.</para>
    /// </summary>
    private void OnRaceEnded(RaceEndedEvent e)
    {
        string name = string.IsNullOrWhiteSpace(e.Title) ? "Race" : e.Title;

        // The bottom-right result panel. Suppresses itself on a first completion, where the normal
        // completion toast is already celebrating the same event.
        if (UsePanache) _racePrompt?.OnRaceEnded(e, e.FirstCompletion);
        else            _fallbackRacePrompt.OnRaceEnded(e, e.FirstCompletion);

        switch (e.Outcome)
        {
            case RaceOutcome.Finished:
                if (e.NewBest && !e.FirstCompletion)
                {
                    // Beat your own time. The completion path says nothing about this — it only
                    // ever fires once, on the first finish — so this is the whole celebration:
                    // gold fly text over the player, the corner panel above, and a chat line.
                    Sound.Play(SoundService.Cue.ObjectiveProgress);
                    FlyTextService.ShowPersonalBest(e.Seconds);

                    string was = e.PreviousBest.HasValue
                        ? $" (was {CompletionStore.FormatRaceTime(e.PreviousBest.Value)})"
                        : string.Empty;
                    ChatGui.Print(
                        $"[Challenges] {name} — PERSONAL BEST {CompletionStore.FormatRaceTime(e.Seconds)}{was}.");
                }
                else if (!e.FirstCompletion)
                {
                    string best = e.PreviousBest.HasValue
                        ? $" Best stands at {CompletionStore.FormatRaceTime(e.PreviousBest.Value)}."
                        : string.Empty;
                    ChatGui.Print(
                        $"[Challenges] {name} — finished in {CompletionStore.FormatRaceTime(e.Seconds)}.{best}");
                }
                else
                {
                    // First completion: the fanfare and toast belong to OnCompleted. Only the time
                    // itself is worth adding, since nothing else reports it.
                    ChatGui.Print(
                        $"[Challenges] {name} — finished in {CompletionStore.FormatRaceTime(e.Seconds)}.");
                }
                break;

            case RaceOutcome.TimedOut:
                ChatGui.Print($"[Challenges] {name} — out of time at {CompletionStore.FormatRaceTime(e.Seconds)}.");
                break;

            case RaceOutcome.LeftArea:
                ChatGui.Print($"[Challenges] {name} — run ended, you left the course.");
                break;

            case RaceOutcome.Abandoned:
                ChatGui.Print($"[Challenges] {name} — run abandoned.");
                break;
        }
    }

    /// <summary>One step landed. Same ordering rule as <see cref="OnCompleted"/>.</summary>
    private void OnProgressed(ProgressEvent e)
    {
        Sound.Play(SoundService.Cue.ObjectiveProgress);

        if (_config.ShowFlyText && !NotificationsHeld)
            FlyTextService.ShowProgress(e.Title, e.Done, e.Total);

        // Interrupts whatever is on screen rather than queueing behind it — the newest count is
        // the only one worth reading, and it is cumulative anyway.
        if (_config.ShowProgressPopups && !NotificationsHeld) _progressQueue.Show(e);
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
        _config.DefinitionsChanged();
        _tracker.Invalidate();

        ChatGui.Print(restored > 0
            ? $"[Challenges] Restored {restored} completion(s) from permanent storage."
            : "[Challenges] Nothing to restore — current data already matches permanent storage.");
    }
}
