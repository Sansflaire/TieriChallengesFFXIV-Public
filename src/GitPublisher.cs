#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Runs the git side of publishing so challenges reach the public repo
/// from a button instead of a terminal.
///
/// <para><b>Subprocess discipline</b> (devPlugins/CLAUDE.md): this launches git, so it obeys the
/// rules. There is no loop — a publish is a fixed, short sequence of commands, each with its own
/// timeout and each killed if it overruns. Only one publish can be in flight at a time. Nothing
/// here can fan out, so the spawn-cap and circuit-breaker rules are satisfied by construction
/// rather than by a counter.</para>
///
/// <para>Pushes rely on the machine's per-owner credential routing, so no token is stored in or
/// handled by the plugin. Commits are authored as Sansflaire explicitly rather than trusting
/// whatever local git config happens to be set.</para>
/// </summary>
internal static class GitPublisher
{
    public const string RepoUrl = "https://github.com/Sansflaire/TieriChallengesFFXIV-Sync.git";

    private const int CommandTimeoutMs = 90_000;
    private const int CloneTimeoutMs   = 180_000;

    private const string AuthorName  = "Sansflaire";
    private const string AuthorEmail = "sansflaire@users.noreply.github.com";

    public sealed class PublishResult
    {
        public bool   Ok      { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Log     { get; init; } = string.Empty;
    }

    private static readonly object Gate = new();
    private static bool _running;

    public static bool IsRunning { get { lock (Gate) return _running; } }

    /// <summary>
    /// Whole publish in one call: make sure the checkout exists, write the challenge files into
    /// it, then commit and push.
    /// </summary>
    public static async Task<PublishResult> PublishAsync(
        IReadOnlyList<CustomChallenge> challenges, string repoPath,
        IReadOnlyList<string>? categories = null, bool allowRemovals = false)
    {
        lock (Gate)
        {
            if (_running)
                return new PublishResult { Ok = false, Summary = "A publish is already running." };
            _running = true;
        }

        var log = new StringBuilder();
        try
        {
            if (string.IsNullOrWhiteSpace(repoPath))
                return Fail(log, "No repo folder set.");

            // 1. Checkout.
            var prep = await EnsureCheckoutAsync(repoPath, log).ConfigureAwait(false);
            if (!prep) return Fail(log, "Could not prepare the repo checkout.");

            // 2. Regenerate the challenge files inside the checkout, so what is committed is
            //    always exactly what the plugin would export — never a stale hand-copy.
            // The checkout must be current BEFORE exporting: the export reads the master list
            // already in it to decide what is an update and what would be a removal. Against a
            // stale checkout, a challenge published from elsewhere looks like it never existed.
            var export = ChallengeExporter.Export(challenges, repoPath, categories, allowRemovals);
            log.AppendLine(export.Message);
            if (!export.Ok) return Fail(log, export.Message);
            if (export.Written == 0)
                return Fail(log, "Nothing publishable — every challenge is missing a name, a description, or required setup.");

            // 3. Stage. .gitattributes goes too — it is what stops git converting the challenge
            //    files' line endings, which would break their hashes on download.
            var add = await RunGitAsync(repoPath, "add challenges .gitattributes", CommandTimeoutMs, log)
                            .ConfigureAwait(false);
            if (!add.Ok) return Fail(log, "git add failed.");

            // 4. Commit. "nothing to commit" is a success, not a failure — it means the repo
            //    already matches what we just exported.
            string msg = $"Publish {export.Written} challenge(s) from the Challenge Creator";
            var commit = await RunGitAsync(repoPath,
                $"-c user.name={AuthorName} -c user.email={AuthorEmail} " +
                $"commit --author=\"{AuthorName} <{AuthorEmail}>\" -m \"{msg}\"",
                CommandTimeoutMs, log).ConfigureAwait(false);

            bool nothingToCommit = commit.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                                || commit.Output.Contains("working tree clean", StringComparison.OrdinalIgnoreCase);

            if (!commit.Ok && !nothingToCommit)
                return Fail(log, "git commit failed.");

            if (nothingToCommit)
            {
                log.AppendLine("Nothing changed since the last publish.");
                return new PublishResult
                {
                    Ok = true,
                    Summary = $"Already up to date — {export.Written} challenge(s), no changes to push.",
                    Log = log.ToString(),
                };
            }

            // 5. Push.
            var push = await RunGitAsync(repoPath, "push origin main", CommandTimeoutMs, log).ConfigureAwait(false);
            if (!push.Ok) return Fail(log, "git push failed — see the log below.");

            string summary = $"Published {export.Written} challenge(s). Users get them on their next Sync.";
            Diag.Info("[Publish] " + summary);

            return new PublishResult { Ok = true, Summary = summary, Log = log.ToString() };
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Publish] failed");
            return Fail(log, $"Publish failed: {ex.Message}");
        }
        finally
        {
            lock (Gate) _running = false;
        }
    }

    private static PublishResult Fail(StringBuilder log, string summary) =>
        new() { Ok = false, Summary = summary, Log = log.ToString() };

    /// <summary>
    /// Write one file into a checkout and push it. Used for the ban list, which is a single
    /// generated file rather than the challenge tree <see cref="PublishAsync"/> handles.
    ///
    /// <para>Shares the same checkout preparation, so a stale clone is pulled forward before the
    /// write — publishing a ban list against a stale checkout would silently revert any ban issued
    /// from another machine.</para>
    /// </summary>
    public static async Task<PublishResult> PublishFileAsync(
        string repoPath, string relativePath, string content, string commitMessage)
    {
        lock (Gate)
        {
            if (_running)
                return new PublishResult { Ok = false, Summary = "A publish is already running." };
            _running = true;
        }

        var log = new StringBuilder();
        try
        {
            if (string.IsNullOrWhiteSpace(repoPath)) return Fail(log, "No repo folder set.");

            if (!await EnsureCheckoutAsync(repoPath, log).ConfigureAwait(false))
                return Fail(log, "Could not prepare the repo checkout.");

            string full = System.IO.Path.Combine(repoPath, relativePath);
            System.IO.File.WriteAllText(full, content);
            log.AppendLine($"Wrote {relativePath}.");

            var add = await RunGitAsync(repoPath, $"add {relativePath}", CommandTimeoutMs, log)
                            .ConfigureAwait(false);
            if (!add.Ok) return Fail(log, "git add failed.");

            var commit = await RunGitAsync(repoPath,
                $"-c user.name={AuthorName} -c user.email={AuthorEmail} " +
                $"commit --author=\"{AuthorName} <{AuthorEmail}>\" -m \"{commitMessage}\"",
                CommandTimeoutMs, log).ConfigureAwait(false);

            bool nothingToCommit = commit.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
                                || commit.Output.Contains("working tree clean", StringComparison.OrdinalIgnoreCase);

            if (!commit.Ok && !nothingToCommit) return Fail(log, "git commit failed.");
            if (nothingToCommit)
                return new PublishResult { Ok = true, Summary = "Already up to date — nothing to push.", Log = log.ToString() };

            var push = await RunGitAsync(repoPath, "push origin HEAD", CommandTimeoutMs, log)
                             .ConfigureAwait(false);
            if (!push.Ok) return Fail(log, "git push failed.");

            return new PublishResult { Ok = true, Summary = $"Published {relativePath}.", Log = log.ToString() };
        }
        catch (Exception ex)
        {
            return Fail(log, "Publish failed: " + ex.Message);
        }
        finally
        {
            lock (Gate) _running = false;
        }
    }

    /// <summary>
    /// Guarantee <paramref name="repoPath"/> is a checkout of the public repo.
    ///
    /// If the folder exists but is not a repo, it is only removed when everything in it is
    /// output this tool generated (a <c>challenges</c> folder and nothing else). Anything
    /// unexpected is left alone and reported — deleting a folder a user pointed us at, on a
    /// guess about what is in it, is not a risk worth taking.
    /// </summary>
    private static async Task<bool> EnsureCheckoutAsync(string repoPath, StringBuilder log)
    {
        if (Directory.Exists(Path.Combine(repoPath, ".git")))
        {
            log.AppendLine($"Using existing checkout at {repoPath}");
            var pull = await RunGitAsync(repoPath, "pull --ff-only origin main", CommandTimeoutMs, log)
                             .ConfigureAwait(false);
            // A failed pull matters more than it looks: the export decides what counts as an
            // update and what counts as a removal by reading this checkout's master.json. Against
            // a stale one those judgements are made on old information. The push would still be
            // rejected for being behind, so nothing reaches users — but say so plainly.
            if (!pull.Ok)
                log.AppendLine("(pull failed — the checkout may be stale, so update/removal detection "
                             + "is working from old information. The push will be rejected if it is behind.)");
            return true;
        }

        if (Directory.Exists(repoPath))
        {
            if (!OnlyContainsGeneratedOutput(repoPath))
            {
                log.AppendLine($"{repoPath} already exists and holds files this tool did not create.");
                log.AppendLine("Refusing to delete it. Point at an empty folder, or clone the repo there yourself.");
                return false;
            }

            log.AppendLine($"Replacing the generated-only folder at {repoPath} with a real checkout.");
            try { Directory.Delete(repoPath, true); }
            catch (Exception ex)
            {
                log.AppendLine($"Could not remove it: {ex.Message}");
                return false;
            }
        }

        log.AppendLine($"Cloning {RepoUrl}");
        var parent = Path.GetDirectoryName(repoPath);
        if (string.IsNullOrEmpty(parent)) return false;
        Directory.CreateDirectory(parent);

        var clone = await RunGitAsync(parent, $"clone {RepoUrl} \"{repoPath}\"", CloneTimeoutMs, log)
                          .ConfigureAwait(false);
        return clone.Ok;
    }

    /// <summary>True when the folder holds nothing but a <c>challenges</c> directory of JSON.</summary>
    private static bool OnlyContainsGeneratedOutput(string path)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path)) return false;   // any loose file: not ours

            foreach (var dir in Directory.GetDirectories(path))
            {
                if (!string.Equals(Path.GetFileName(dir), "challenges", StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var f in Directory.GetFiles(dir))
                    if (!f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;

                if (Directory.GetDirectories(dir).Length > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private readonly struct GitResult
    {
        public GitResult(bool ok, string output) { Ok = ok; Output = output; }
        public bool Ok { get; }
        public string Output { get; }
    }

    /// <summary>
    /// One git invocation. Output is captured, the process is killed on timeout, and nothing is
    /// left running when this returns.
    /// </summary>
    private static async Task<GitResult> RunGitAsync(string workingDir, string args, int timeoutMs,
                                                     StringBuilder log)
    {
        log.AppendLine($"> git {args}");

        var psi = new ProcessStartInfo
        {
            FileName               = "git",
            Arguments              = args,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { log.AppendLine("could not start git"); return new GitResult(false, string.Empty); }

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!await Task.Run(() => proc.WaitForExit(timeoutMs)).ConfigureAwait(false))
            {
                try { proc.Kill(true); } catch { }
                log.AppendLine($"timed out after {timeoutMs / 1000}s and was killed");
                return new GitResult(false, string.Empty);
            }

            string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(output)) log.AppendLine(output.Trim());
            log.AppendLine($"(exit {proc.ExitCode})");

            return new GitResult(proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            log.AppendLine($"git failed to run: {ex.Message}");
            return new GitResult(false, string.Empty);
        }
    }
}
#endif
