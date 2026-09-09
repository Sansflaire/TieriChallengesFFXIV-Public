using System;

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
    }

    /// <summary>
    /// Disposes both hooks. Safe from any teardown path — disposing a hook restores the game's own
    /// function, so an unload mid-dig hands input straight back rather than leaving it blocked.
    /// That is what makes it acceptable for this to be reached from <c>Dispose</c> at all, unlike
    /// the native calls in <see cref="PropService"/>.
    /// </summary>
    public void Dispose()
    {
        Blocking = false;

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
