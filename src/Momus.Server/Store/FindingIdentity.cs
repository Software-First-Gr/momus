using Momus.Core;

namespace Momus.Server.Store;

/// <summary>
/// What makes a finding in today's scan "the same one" as in yesterday's. Subjects are the
/// identity, because titles move: the top-queries check writes the call count into its title,
/// so a title-keyed identity would reset every minute and every finding would look new.
/// </summary>
public static class FindingIdentity
{
    public static string For(Finding finding) =>
        finding.Subjects.Count > 0
            ? $"{finding.CheckId}|{Subject.Canonical(finding.Subjects)}"
            // A check that names no subject is a bug (M1.1 gave every check one), but a store
            // must not merge two unrelated findings because of it.
            : $"{finding.CheckId}|title:{finding.Title}";
}
