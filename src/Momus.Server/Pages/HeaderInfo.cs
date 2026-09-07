using Momus.Server.Store;

namespace Momus.Server.Pages;

/// <summary>
/// What the header shows on every page: which application is reporting and which build of it.
/// A version changing here is a deploy, so it is worth being visible from wherever you are rather
/// than only on the History tab.
/// </summary>
/// <remarks>
/// Scoped, so it is one small read per request against a table with one row per application. The
/// alternative — every page model carrying it into ViewData — puts the same three lines in six
/// places and gets forgotten in the seventh.
/// </remarks>
public sealed class HeaderInfo(MomusStore store)
{
    public async Task<string?> AppAsync(CancellationToken ct = default)
    {
        var app = (await store.AppsAsync(ct)).FirstOrDefault();
        if (app is null) return null;

        return app.Version is { Length: > 0 } version ? $"{app.Name} {version}" : app.Name;
    }
}
