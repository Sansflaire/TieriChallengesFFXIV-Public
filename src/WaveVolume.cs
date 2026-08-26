using System;
using System.IO;

namespace TieriChallengesFFXIV;

/// <summary>
/// Volume control for the shipped .wav cues, done by rescaling the samples.
///
/// <para><b>Why this is not a volume parameter.</b> The zingles play through winmm's
/// <c>PlaySound</c>, which has no volume argument, and they cannot be moved back to the game's
/// mixer — that was proven silent over roughly fifteen builds (BROKEN.md 003). The alternatives
/// were raw <c>waveOut</c> (device handles, buffer callbacks and cleanup, inside a process where a
/// mistake takes the game down) or a new audio dependency. Multiplying PCM samples is arithmetic on
/// a byte array: it cannot crash the game, needs no new reference, and leaves the existing playback
/// path exactly as it was.</para>
///
/// <para><b>One cached file per (cue, volume step).</b> Volume is quantised to 5% steps so dragging
/// the slider produces at most twenty files per cue rather than one per pixel of travel, and a
/// rebuild only happens the first time a given step is used.</para>
/// </summary>
internal static class WaveVolume
{
    /// <summary>Volume is rounded to this many steps. 20 = 5% granularity.</summary>
    private const int Steps = 20;

    /// <summary>Folder for rescaled copies, under the plugin's own config directory.</summary>
    private const string CacheFolder = "sound-cache";

    /// <summary>
    /// The file to actually play for a cue at the current volume.
    ///
    /// <para>Returns the ORIGINAL path at full volume — no copy, no cache, no I/O — so the default
    /// configuration behaves exactly as it did before this existed. Returns null when muted or at
    /// zero, which the caller treats as "play nothing".</para>
    /// </summary>
    public static string? ResolveForPlayback(string originalPath, float volume, bool muted)
    {
        if (muted || volume <= 0.001f) return null;
        if (volume >= 0.999f)          return originalPath;

        int step = Math.Clamp((int)MathF.Round(volume * Steps), 1, Steps);

        try
        {
            string cached = CachePathFor(originalPath, step);
            if (File.Exists(cached)) return cached;

            return Rescale(originalPath, cached, step / (float)Steps) ? cached : originalPath;
        }
        catch (Exception ex)
        {
            // Falling back to the original means the cue plays LOUD rather than not at all. That is
            // the right failure: a missed cue is invisible, and this plugin treats sound as its
            // highest-priority feedback.
            Plugin.Log.Warning($"[Sound] volume rescale failed, playing at full: {ex.Message}");
            return originalPath;
        }
    }

    private static string CachePathFor(string originalPath, int step)
    {
        string dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), CacheFolder);
        Directory.CreateDirectory(dir);

        string name = Path.GetFileNameWithoutExtension(originalPath);
        return Path.Combine(dir, $"{name}.v{step:00}.wav");
    }

    /// <summary>
    /// Copy a RIFF/WAVE file with every PCM sample multiplied by <paramref name="factor"/>.
    ///
    /// <para>Walks the chunk list rather than assuming a 44-byte canonical header — the shipped
    /// cues came out of the game's archives and carry extra chunks. Only <c>data</c> is touched;
    /// everything else is copied through byte for byte, so the result is the same file with
    /// quieter samples.</para>
    ///
    /// <para>Handles 8-bit unsigned and 16-bit signed PCM, which is what the shipped cues are.
    /// Anything else is refused rather than mangled — returning false plays the original.</para>
    /// </summary>
    private static bool Rescale(string source, string destination, float factor)
    {
        byte[] bytes = File.ReadAllBytes(source);

        if (bytes.Length < 12
            || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
        {
            Plugin.Log.Warning($"[Sound] {Path.GetFileName(source)} is not a RIFF/WAVE file.");
            return false;
        }

        int bitsPerSample = 0;
        int formatTag     = 0;
        int pos           = 12;

        while (pos + 8 <= bytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int    size    = BitConverter.ToInt32(bytes, pos + 4);
            int    body    = pos + 8;

            if (size < 0 || body + size > bytes.Length) break;   // truncated or lying header

            if (chunkId == "fmt ")
            {
                formatTag     = BitConverter.ToUInt16(bytes, body);
                bitsPerSample = size >= 16 ? BitConverter.ToUInt16(bytes, body + 14) : 0;
            }
            else if (chunkId == "data")
            {
                // WAVE_FORMAT_PCM only. Compressed audio would need decoding, and scaling its bytes
                // would produce noise rather than a quieter sound.
                if (formatTag != 1)
                {
                    Plugin.Log.Warning(
                        $"[Sound] {Path.GetFileName(source)} is format {formatTag}, not PCM — not rescaled.");
                    return false;
                }

                if (!ScaleSamples(bytes, body, size, bitsPerSample, factor)) return false;

                File.WriteAllBytes(destination, bytes);
                return true;
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte.
            pos = body + size + (size & 1);
        }

        Plugin.Log.Warning($"[Sound] no data chunk found in {Path.GetFileName(source)}.");
        return false;
    }

    private static bool ScaleSamples(byte[] bytes, int offset, int count, int bitsPerSample, float factor)
    {
        switch (bitsPerSample)
        {
            case 16:
                for (int i = offset; i + 1 < offset + count; i += 2)
                {
                    short sample = BitConverter.ToInt16(bytes, i);
                    int scaled   = (int)MathF.Round(sample * factor);
                    // Clamped even though we only ever scale DOWN — a factor is a float and
                    // rounding at the extremes should not be able to wrap a sample to full-scale
                    // opposite sign, which is an audible click.
                    scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
                    BitConverter.TryWriteBytes(bytes.AsSpan(i, 2), (short)scaled);
                }
                return true;

            case 8:
                // 8-bit WAV is UNSIGNED with 128 as silence, so it scales about the midpoint
                // rather than about zero. Treating it as signed produces a loud buzz.
                for (int i = offset; i < offset + count; i++)
                {
                    int centred = bytes[i] - 128;
                    int scaled  = (int)MathF.Round(centred * factor) + 128;
                    bytes[i]    = (byte)Math.Clamp(scaled, 0, 255);
                }
                return true;

            default:
                Plugin.Log.Warning($"[Sound] {bitsPerSample}-bit audio is not handled — not rescaled.");
                return false;
        }
    }

    /// <summary>
    /// Drop the rescaled copies. Called when the shipped cue files change under us — a cache entry
    /// keyed only by name and volume step would otherwise keep playing the old sound forever.
    /// </summary>
    public static void ClearCache()
    {
        try
        {
            string dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), CacheFolder);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Sound] could not clear the volume cache: {ex.Message}");
        }
    }
}
