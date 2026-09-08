using Autor3Search.Core.RunState;
using Autor3Search.Core.SourceControl;

namespace Autor3Search.Cli;

/// <summary>Resolves which run a command is talking about.</summary>
internal static class RunTag
{
    /// <summary>
    /// The -tag flag when given, otherwise the tag encoded in the current branch name.
    ///
    /// The flag exists so status and stop work from any branch and any terminal — a
    /// human checking on an overnight run should not have to check out the run branch
    /// to ask a question about it.
    /// </summary>
    public static async Task<string> ResolveAsync(Args args, string repo, CancellationToken ct)
    {
        var explicitTag = args.GetString("tag");
        if (!string.IsNullOrWhiteSpace(explicitTag)) return explicitTag;

        var branch = await Git.CurrentBranchAsync(repo, ct).ConfigureAwait(false);
        var tag = StateStore.TagFromBranch(branch);

        // TagFromBranch strips the prefix and nothing else, so a branch that is exactly
        // the prefix (or the prefix plus only whitespace) yields "" or "   ", not null —
        // guarded the same way the explicit -tag flag is above, rather than letting an
        // empty/blank tag silently resolve to a nonsensical run.
        return !string.IsNullOrWhiteSpace(tag)
            ? tag
            : throw new InvalidOperationException(
                $"the current branch \"{branch}\" is not a run branch, so there is no run to act on. " +
                "Pass -tag <tag>, or check out the run branch.");
    }
}
