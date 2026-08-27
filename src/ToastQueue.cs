using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// Queue and timing for the completion popup, kept separate from how it is drawn.
///
/// <para>Contains <b>no PanacheUI types</b>, deliberately. The popup has two renderers — the
/// Panache one and a plain-ImGui fallback — and this must keep working when the Panache library
/// cannot be loaded at all. Previously the queue lived inside the Panache renderer, so switching
/// PanacheUI off (or losing the library) meant finishing a challenge produced no celebration
/// whatsoever.</para>
///
/// <para>Exactly one renderer may call <see cref="TryCurrent"/> per frame — it advances the
/// clock. Calling it from both would double the fade speed and drop popups.</para>
/// </summary>
public sealed class ToastQueue
{
    /// <summary>
    /// How long the completion banner stays up, from Settings. A static for the same reason as
    /// <see cref="ProgressQueue.TotalSeconds"/> — the queues are pure timing state and deliberately
    /// hold no config reference.
    /// </summary>
    /// <remarks>
    /// <b>This used to be a <c>const 5.0f</c>, and the setting did nothing to the completion
    /// banner.</b> Settings wrote its value to <c>CompletionToast.HoldSeconds</c>, which was read by
    /// nothing at all — a write-only property whose doc comment said "Set from Settings", sitting
    /// next to the const that actually governed the timing. Everything looked wired up: the slider
    /// moved, the value was saved, applied at startup, and had no effect whatsoever.
    /// </remarks>
    public static float TotalSeconds { get; set; } = 5.0f;

    private const float FadeSeconds = 0.9f;

    /// <summary>Fully-opaque time. Never negative, however short the total is set.</summary>
    private static float HoldSeconds => MathF.Max(0.2f, TotalSeconds - FadeSeconds);

    private readonly Queue<CompletionEvent> _queue = new();
    private CompletionEvent? _current;
    private float _elapsed;

    public int Pending => _queue.Count;

    public void Enqueue(CompletionEvent e)
    {
        _queue.Enqueue(e);
        Diag.Debug($"[Toast] queued completion #{e.Number} \"{e.Title}\"");
    }

    /// <summary>
    /// Preview the popup for the challenge being authored. Fields not yet filled in fall back to
    /// baseline placeholder text, flagged so the renderer can draw them in red.
    /// </summary>
    public void ShowPreview(CustomChallenge draft, int number)
    {
        bool titleMissing  = string.IsNullOrWhiteSpace(draft.Title);
        bool detailMissing = string.IsNullOrWhiteSpace(draft.Detail);

        // The preview is meant to show what the player will actually get, and that includes the
        // fanfare. Requested through the plugin's sound service like every other cue.
        Plugin.Sound.Play(SoundService.Cue.ChallengeComplete);

        Enqueue(new CompletionEvent
        {
            Number        = number,
            Title         = titleMissing  ? "Untitled Challenge" : draft.Title.Trim(),
            Detail        = detailMissing ? "No description yet — this line is required." : draft.Detail.Trim(),
            TitleMissing  = titleMissing,
            DetailMissing = detailMissing,
        });
    }

    /// <summary>
    /// Advance the clock and hand back what should be on screen, with its fade alpha.
    /// Returns false when there is nothing to draw.
    /// </summary>
    public bool TryCurrent(float deltaSeconds, out CompletionEvent current, out float alpha)
    {
        current = null!;
        alpha   = 0f;

        if (_current == null)
        {
            if (_queue.Count == 0) return false;
            _current = _queue.Dequeue();
            _elapsed = 0f;

            // The fanfare is deliberately NOT played here. Firing it as the popup surfaced made
            // audio a slave to the display queue — a completion whose popup was still waiting
            // behind another had its sound delayed with it. Sound is high priority and now fires
            // the instant the tracker raises the event; see Plugin.OnCompleted.
        }

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
