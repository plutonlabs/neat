namespace neat;

/// <summary>
/// What <see cref="ExtAdd.Install"/> came to. Copied is true once NEAT has made its own copy of the
/// extension: a failure after that is the browser's (Res says why), and the copy has been deleted again.
/// A failure while Copied is false is a failure to make the copy; a package that was unpacked is still unpacked.
/// </summary>
internal readonly record struct ExtDone(ExtRes Res, bool Copied);

/// <summary>
/// The part of adding an extension that does not draw anything, shared by the Extensions page (opts.ext.cs)
/// and the Chrome Web Store button (win.cws.cs): make the copy NEAT keeps, tell WebView2 about it,
/// remember it, or undo all of it. Which of the two asks is the only difference, and it is not made here.
/// Not part of tests/ext.check (it needs the app's data folder and the live profile).
///
/// Also the one place that holds the <see cref="ExtStore"/> of the app, so that the page, the store button
/// and the sweep at the start all work on the same instance and the same lock.
/// </summary>
internal static class ExtAdd
{
    private static readonly Lazy<ExtStore> Held = new(() => new ExtStore(Path.Combine(Env.Root, "Extensions")));

    /// <summary>The copies NEAT keeps of the extensions. One for the whole app; usable from the UI thread and, for the slow parts, a pool thread.</summary>
    public static ExtStore Store => Held.Value;

    /// <summary>
    /// Runs bookkeeping on a pool thread: writing the list of copies, deleting a folder. A failure
    /// is logged and goes no further, because the extension itself has already been added or
    /// removed by then and the caller must go on.
    /// </summary>
    public static Task Keep(Action work)
    {
        return Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("ext", ex, "extension copies could not be kept up to date");
            }
        });
    }

    /// <summary>
    /// Adds an extension. The copy is what is added, at the place it will stay: WebView2 remembers the
    /// folder, and the extension is gone the day that folder moves or changes. A folder (stage is null) is
    /// copied there from source; a package (stage is its staging folder) is already unpacked, and is moved
    /// there before WebView2 hears of it. Call from the UI thread, like <see cref="Ext.Add"/>.
    ///
    /// settled is called, on the caller's thread, the moment a staging folder has been moved: from then on it
    /// is no longer the caller's to drop. storeId is the id of the Chrome Web Store for an extension that came
    /// from there, null for the others; it is only remembered, never used as a name.
    /// Nothing is asked here: the caller has asked the person already.
    /// </summary>
    public static async Task<ExtDone> Install(string? stage, string source, ExtMeta meta, string? storeId, Action? settled = null)
    {
        var xs = Store;
        string dir;

        try
        {
            if (stage is null)
            {
                dir = await Task.Run(() => xs.Put(source, meta.Name));
            }
            else
            {
                dir = await Task.Run(() => xs.Settle(stage, meta.Name));
                settled?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // A package stays unpacked, so the person can try again or say Cancel.
            Log.Error("ext", ex, "keeping an extension failed");
            return new ExtDone(
                ExtRes.Bad(ex is InvalidOperationException ? ex.Message : "NEAT could not keep a copy of that extension."),
                false);
        }

        var res = await Ext.Add(xs.Full(dir));
        if (!res.Ok || res.Item is null)
        {
            await Keep(() => xs.Discard(dir));
            Log.Write("ext", "adding " + Cws.Tidy(meta.Name, 80) + " failed");
            return new ExtDone(ExtRes.Bad(res.Err ?? "The extension could not be added."), true);
        }

        // Without this link the extension still works; only its version, description and "Added" are lost.
        await Keep(() => xs.Link(res.Item.Id, dir, storeId));

        // The name is a package's own words (a store package's too), so it is tidied before it reaches the log.
        var shown = Cws.Tidy(res.Item.Name.Length > 0 ? res.Item.Name : meta.Name, 80);
        Log.Write("ext", "added " + res.Item.Id + " (" + shown + ")");

        return new ExtDone(res, true);
    }
}
