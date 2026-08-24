using System;

namespace TieriChallengesFFXIV;

/// <summary>
/// Timing for the small bottom-right progress notification, kept separate from how it is drawn —
/// same split as <see cref="ToastQueue"/>, and for the same reason: the popup has a Panache
/// renderer and a plain-ImGui fallback, and the timing must keep working when the Panache library
/// cannot be loaded at all.
///
/// <para><b>Newest wins — this does not queue.</b> A step that lands while an earlier popup is
/// still on screen REPLACES it and restarts the five seconds. Queueing was wrong twice over: the
/// corner showed a stale count while a newer one waited, and the notification outlived the moment
/// it was describing. Nothing is lost by replacing, because the count is cumulative — 3/5
/// already tells you 2/5 happened.</para>
///
/// <para><b>This class no longer plays the sound.</b> It used to, on the frame a popup surfaced,
/// which quietly made audio a slave to the display queue: a dropped or delayed popup meant a
/// dropped or delayed cue. Sound is high priority and now fires the instant the tracker raises
/// the event — see <c>Plugin.OnProgressed</c>.</para>
///
/// <para>Contains <b>no PanacheUI types</b>. Deliberate — see <see cref="ToastQueue"/>. Exactly
/// one renderer may call <see cref="TryCurrent"/> per frame; it advances the clock.</para>
/// </summary>
public sealed class ProgressQueue
{
    /// <summary>
    /// Five seconds on screen end to end, as specified. The fade is carved out of that budget
    /// rather than added to it, so the popup is fully gone at the five-second mark instead of
    /// lingering to 5.8.
    /// </summary>
    private const float TotalSeconds = 5.0f;
    private const float FadeSeconds  = 0.8f;
    private const float HoldSeconds  = TotalSeconds - FadeSeconds;

    private ProgressEvent? _current;
    private float _elapsed;

    /// <summary>
    /// Show a step, interrupting whatever is on screen and restarting the five seconds.
    /// </summary>
    public void Show(ProgressEvent e)
    {
        _current = e;
        _elapsed = 0f;
        Diag.Debug($"[Progress] showing #{e.Number} \"{e.Title}\" {e.Done}/{e.Total}");
    }

    /// <summary>
    /// Advance the clock and hand back what should be on screen, with its fade alpha.
    /// Returns false when there is nothing to draw.
    /// </summary>
    public bool TryCurrent(float deltaSeconds, out ProgressEvent current, out float alpha)
    {
        current = null!;
        alpha   = 0f;

        if (_current == null) return false;

        _elapsed += deltaSeconds;

        if (_elapsed >= HoldSeconds + FadeSeconds)
        {
            _current = null;
            return false;
        }

        alpha = _elapsed <= HoldSeconds
            ? 1f
            : 1f - ((_elapsed - HoldSeconds) / FadeSeconds);
        alpha = Math.Clamp(alpha, 0f, 1f);

        current = _current;
        return true;
    }
}
