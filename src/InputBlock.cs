using System;

using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace TieriChallengesFFXIV;

/// <summary>
/// Silently swallows movement and jump input while a prop performance is running, so a dig plays to
/// its end instead of being cancelled by the player's own keys.
///
/// <para><b>SHIPS PUBLICLY</b>, like <see cref="PropService"/> — challenge content that plays a dig
/// needs the dig to finish, and that cannot depend on the player holding still.</para>
///
/// <para><b>Two hooks, because movement and jump arrive by different routes.</b></para>
/// <list type="bullet">
/// <item><b>Movement — the RMI walk function.</b> This is where every source of movement converges
/// into three floats, so zeroing them there blocks keyboard, mouse-look, and controller at once, and
/// works identically on Standard and Legacy movement. Blocking keys individually would have missed
/// two of the three and both movement types.</item>
/// <item><b>Jump — <c>ActionManager.UseAction</c>, refused for exactly one action.</b> Jump is a
/// <see cref="ActionType.GeneralAction"/>, id 2. The detour returns false for that and calls the
/// original for everything else, so abilities, items and mounts are untouched. A blanket block on
/// UseAction would silence the player's whole hotbar.</item>
/// </list>
///
/// <para><b>Blocked, never consumed-and-reported.</b> The player gets no error, no shake, no
/// "cannot do that yet" — the input simply does nothing for a few seconds. That is the requested
/// behaviour and it is also the kinder one: a refusal message for every key pressed during a
/// four-second dig would be a wall of red text.</para>
///
/// <para><b>Fail-open by construction.</b> If either hook cannot be installed the plugin logs it and
/// carries on with digs that are merely cancellable, which is exactly how they behaved before this
/// existed. Nothing here is allowed to be a reason a dig does not happen.</para>
/// </summary>
internal sealed unsafe class InputBlock : IDisposable
{
    /// <summary>
    /// <c>Client::Game::Character::CharacterMoveController.HandleMoveInput</c> — the aggregation
    /// point for movement input. Same signature the sibling movement plugins in this directory use.
    ///
    /// <para>If a game patch moves this, the scan fails, a warning is logged, and digs go back to
    /// being interruptible. Update the string here and rebuild.</para>
    /// </summary>
    private const string RmiWalkSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";

    /// <summary>Jump, as a general action. General action ids are stable across patches.</summary>
    private const uint JumpActionId = 2;

    private delegate bool RmiWalkDelegate(
        void* self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* a6, byte a7);

    private delegate bool UseActionDelegate(
        ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted);

    private Hook<RmiWalkDelegate>?   _rmiWalkHook;
    private Hook<UseActionDelegate>? _useActionHook;

    /// <summary>
    /// While true, movement and jump do nothing. Owned by <see cref="PropService"/>, which sets it
    /// for exactly as long as a performance runs.
    /// </summary>
    public bool Blocking { get; set; }

    /// <summary>
    /// Facing to pin the character to while blocking, or null to leave rotation alone.
    ///
    /// <para><b>This has to be written from inside the hook, not from a tick.</b> The first attempt
    /// wrote it from <c>PropService.Tick</c>, which runs on the DRAW loop — after the scene for that
    /// frame has already been rendered — so the game recomputed rotation from the mouse on the next
    /// update and the held value was never the one drawn. The RMI detour runs inside the game's own
    /// movement processing, which is the phase rotation is actually applied in, so a write here
    /// lands for the frame being built.</para>
    ///
    /// <para>Rotation is not position: this is a facing angle, which the sibling movement plugins
    /// write routinely, and it carries none of the displacement concerns that make writing
    /// <c>Position</c> forbidden.</para>
    /// </summary>
    public float? HeldRotation { get; set; }

    public bool MovementHooked => _rmiWalkHook != null;
    public bool JumpHooked     => _useActionHook != null;

    public InputBlock()
    {
        // Installed independently: losing one is no reason to give up the other, and a dig with
        // movement blocked but jump free is still much better than neither.
        try
        {
            _rmiWalkHook = Plugin.GameInterop.HookFromSignature<RmiWalkDelegate>(
                RmiWalkSignature, RmiWalkDetour);
            _rmiWalkHook.Enable();
        }
        catch (Exception ex)
        {
            _rmiWalkHook = null;
            Diag.Error($"[Input] movement hook unavailable, digs stay interruptible: {ex.Message}");
        }

        try
        {
            _useActionHook = Plugin.GameInterop.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);
            _useActionHook.Enable();
        }
        catch (Exception ex)
        {
            _useActionHook = null;
            Diag.Error($"[Input] jump hook unavailable, jumping stays possible mid-dig: {ex.Message}");
        }

        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Second of the three points the facing is pinned at — see <see cref="PinRotation"/>.
    /// </summary>
    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (!Blocking) return;

        SuppressKeys();
        PinRotation();
    }

    /// <summary>
    /// Movement, turn and jump keys, cleared before the game reads them.
    ///
    /// <para><b>Why this is needed when the movement floats are already zeroed.</b> Some behaviours
    /// are driven by the RAW KEY STATE rather than by the movement the keys produce — holding Q or E
    /// with right-mouse snaps the character's facing to the camera, and that happens whether or not
    /// the resulting movement is discarded. Zeroing the floats removes the motion and leaves the
    /// side effect, which is exactly what was still turning the body mid-dig.</para>
    ///
    /// <para>Clearing the key at source removes both, and does it <i>before</i> the game acts rather
    /// than trying to undo the result afterwards — which is what the three rotation pins were doing,
    /// and why they kept losing to it.</para>
    /// </summary>
    private static readonly VirtualKey[] MovementKeys =
    {
        VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D,   // move / turn
        VirtualKey.Q, VirtualKey.E,                               // strafe — the camera-snap pair
        VirtualKey.SPACE,                                         // jump
        VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT,
    };

    private static void SuppressKeys()
    {
        try
        {
            foreach (var key in MovementKeys)
            {
                // Not every key is one the game is listening for; asking for an invalid one throws.
                if (Plugin.KeyState.IsVirtualKeyValid(key))
                    Plugin.KeyState[key] = false;
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Input] key suppression failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Forces the character back to <see cref="HeldRotation"/>.
    ///
    /// <para><b>Called from three different phases of the frame, and that is deliberate rather than
    /// lazy.</b> Camera-driven facing — hold strafe, move the mouse, the body swings to face the
    /// screen — is applied by the move controller, and that controller is not among the structs
    /// available here, so there is no single write site to intercept. Without knowing where in the
    /// frame it lands, the only reliable answer is to reassert the value in the game-logic phase
    /// (framework update), the movement phase (the RMI detour) and the render phase
    /// (<c>PropService.Tick</c>): whichever the game's write falls between, one of ours is still
    /// after it.</para>
    ///
    /// <para>If the true write site is ever identified, this collapses to one place and the other
    /// two should go.</para>
    /// </summary>
    public void PinRotation()
    {
        if (HeldRotation is not { } rot) return;

        try
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp == null || lp.Address == nint.Zero) return;

            ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)lp.Address)->Rotation = rot;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Input] rotation pin failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Disposes both hooks. Safe from any teardown path — disposing a hook restores the game's own
    /// function, so an unload mid-dig hands input straight back rather than leaving it blocked.
    /// That is what makes it acceptable for this to be reached from <c>Dispose</c> at all, unlike
    /// the native calls in <see cref="PropService"/>.
    /// </summary>
    public void Dispose()
    {
        Blocking     = false;
        HeldRotation = null;

        try { Plugin.Framework.Update -= OnFrameworkUpdate; } catch { /* teardown must not throw */ }

        try { _rmiWalkHook?.Dispose(); }   catch { /* teardown must not throw */ }
        try { _useActionHook?.Dispose(); } catch { /* teardown must not throw */ }

        _rmiWalkHook   = null;
        _useActionHook = null;
    }

    private bool RmiWalkDetour(
        void* self, float* sumLeft, float* sumForward, float* sumTurnLeft,
        byte* haveBackwardOrStrafe, byte* a6, byte a7)
    {
        // The original always runs, so the rest of the game's movement bookkeeping happens exactly
        // as it would have; only the resulting direction is zeroed. Skipping it entirely would be a
        // far bigger intervention than blocking input.
        bool result = _rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
                                             haveBackwardOrStrafe, a6, a7);

        try
        {
            if (Blocking)
            {
                if (sumLeft     != null) *sumLeft     = 0f;
                if (sumForward  != null) *sumForward  = 0f;
                if (sumTurnLeft != null) *sumTurnLeft = 0f;

                // Zeroing the turn float only covers turn KEYS. Camera-driven facing bypasses these
                // floats entirely, so the facing is pinned as well — here, in the movement phase.
                PinRotation();
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Input] move detour failed: {ex.Message}");
        }

        return result;
    }

    private bool UseActionDetour(
        ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId,
        uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        try
        {
            // Exactly one action, and only while a dig is running. Returning false is what the game
            // itself returns for an action it will not start, so nothing downstream sees anything
            // unusual — and the player sees no error.
            if (Blocking && actionType == ActionType.GeneralAction && actionId == JumpActionId)
                return false;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Input] jump detour failed: {ex.Message}");
        }

        return _useActionHook!.Original(thisPtr, actionType, actionId, targetId,
                                        extraParam, mode, comboRouteId, outOptAreaTargeted);
    }
}
