using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace neat;

/// <summary>
/// Adding an extension from the Chrome Web Store: the window's side of it. The page script is in
/// web/cwsjs.cs, the download in web/cws.cs, the signature check in web/crxsig.cs, and what is done with
/// an extension once it is accepted in web/extadd.cs (the Extensions page uses the same). The question
/// itself is drawn by win.cwsbox.cs. This file connects them to the window.
///
/// How an install travels:
///   1. The page script says which extension's install button the person clicked (cws-install, the id from
///      the address). The button on the page keeps the store's own words; nothing is sent back to it.
///   2. The question opens at once, with a turning ring and Cancel.
///   3. Behind it the package is downloaded from Google into a file of its own (never held in memory), its
///      signature is checked (it must be a CRX3 for this very id), and it is unpacked into a staging folder.
///      Nothing of it runs.
///   4. The question fills in: the extension's name and icon, everything it can do, Cancel and Add to NEAT.
///      Nothing is added without that click.
///   5. Add keeps a copy, tells the browser, and the question closes. Cancel, Esc, closing the tab or the
///      window drops everything (the staging folder and the file).
///   An extension that is in already, and a failure, are said in the same dialog, with Close.
///
/// What is accepted from a page: a text message that starts with this window's prefix, from the store (https, its
/// host, the default port) in the page the tab shows, about the extension whose page that is. A page can post
/// anything, so none of that is trusted for more than what it is worth: the id is checked again against the
/// address of the tab, the extension asked about is shown to the person with what it can do, and the question is
/// the protection. One question is open at a time in the window.
/// Frames speak through their own channel (win.frames.cs), which this file does not listen to.
///
/// Not written to the log: addresses, paths, anything the download said. The id, the name (tidied) and the outcome are.
/// </summary>
public sealed partial class Win
{
    // The longest message that is looked at. The script's own are under a hundred bytes.
    private const int CwsMaxMessage = 1024;

    // How many times "the store script is running" is written to the log in one run.
    private const int CwsMaxLoaded = 5;

    // How big the icons of the question are (device independent pixels), and what they are decoded at.
    private const int CwsIconSize = 40;
    private const int CwsIconDecode = 96;

    /// <summary>Everything one install holds, from the click to the answer.</summary>
    private sealed class CwsFlow
    {
        public CwsFlow(Tab tab, string id)
        {
            Tab = tab;
            Id = id;
        }

        /// <summary>The tab that asked, and what it asked for.</summary>
        public Tab Tab { get; }
        public string Id { get; }

        /// <summary>Cancelled when the question closes, and when the tab or the window goes away.</summary>
        public CancellationTokenSource Stop { get; } = new();

        /// <summary>The downloaded file, until it is unpacked or the install ends.</summary>
        public string? Pack;

        /// <summary>The staging folder, until the extension is kept or the install ends.</summary>
        public string? Stage;

        /// <summary>What the unpacked package says it is; null until the question is asked.</summary>
        public ExtMeta? Meta;

        /// <summary>The dialog on screen, if there is one.</summary>
        public ContentDialog? Box;

        /// <summary>The adding that Add to NEAT started, if it was clicked.</summary>
        public Task Adding = Task.CompletedTask;

        /// <summary>True while the extension is being added: the dialog cannot be closed then.</summary>
        public bool Busy;

        /// <summary>True once the extension is in.</summary>
        public bool Added;
    }

    private CwsFlow? _cws;
    private int _cwsLoaded;

    // ---- wiring ----

    /// <summary>Adds the page script. It must be in place before the first page loads.</summary>
    private async Task AddCwsScript(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(CwsScript.For(_tok));
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "the store button script could not be added");
        }
    }

    /// <summary>Called for every tab when its web view is ready.</summary>
    private void CwsHook(Tab tab, CoreWebView2 core)
    {
        core.WebMessageReceived += (s, e) => CwsMessage(tab, core, e);
    }

    private void CwsInit()
    {
        // What an earlier run left behind: a download, or an unpacked package nobody finished with.
        // Both are held while an install runs, so nothing in use is touched.
        _ = ExtAdd.Keep(ExtAdd.Store.Sweep);
    }

    /// <summary>The window is closing: the install (if any) is given up.</summary>
    private void CwsStop()
    {
        if (_cws is { } f)
            CwsCancel(f, true);
    }

    /// <summary>A tab is closing: an install it asked for is given up, wherever it has got to.</summary>
    private void CwsTabClosed(Tab tab)
    {
        if (_cws is { } f && f.Tab == tab)
            CwsCancel(f, false);
    }

    // Stops the install and takes its dialog away. A dialog that is in the middle of adding stays until
    // that is over (it closes itself then), unless the whole window is going.
    private static void CwsCancel(CwsFlow f, bool windowClosing)
    {
        try
        {
            f.Stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already over.
        }

        if (windowClosing)
            f.Busy = false;

        try
        {
            if (!f.Busy)
                f.Box?.Hide();
        }
        catch (Exception ex)
        {
            Log.Write("ext", "the store question could not be closed (" + ex.GetType().Name + ")");
        }
    }

    // ---- what the page says ----

    /// <summary>
    /// Every message a page posts comes through here, as through the other handlers: it must never throw, because an
    /// exception in an event handler ends the app, and a page can post anything, not only text.
    /// </summary>
    private void CwsMessage(Tab tab, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? text;
            try
            {
                text = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            var head = CwsScript.Prefix(_tok);
            if (text is null || text.Length > CwsMaxMessage || !text.StartsWith(head, StringComparison.Ordinal))
                return;

            // Only the store, and only a tab that is still open.
            if (_closed || !_tabs.Contains(tab) || !Cws.IsStoreUrl(e.Source))
                return;

            using var doc = JsonDocument.Parse(text.AsMemory(head.Length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            var id = JsonText(root, "id");

            switch (JsonText(root, "kind"))
            {
                case "cws-loaded":
                    CwsSeen();
                    break;

                case "cws-install" when Cws.IsId(id):
                    // The extension has to be the one of the page the tab is on.
                    if (Cws.PageId(core.Source) != id)
                    {
                        Log.Write("ext", "store " + id + ": the click was not taken, the tab is on another page");
                        break;
                    }

                    // The dialog is not opened from inside the web view's own event.
                    DispatcherQueue.TryEnqueue(() => _ = CwsGuard(() => CwsInstall(tab, id!), "adding an extension from the store"));
                    break;
            }
        }
        catch (JsonException)
        {
            // Not what the script says; nothing to do.
        }
        catch (Exception ex)
        {
            Log.Write("ext", "a message from the store page could not be handled (" + ex.GetType().Name + ")");
        }
    }

    // The script is in place on a page of the store. Written to the log, a few times at most, so that the
    // person who reports "the click does nothing of ours" has a line to point at, or the lack of one.
    private void CwsSeen()
    {
        if (_cwsLoaded >= CwsMaxLoaded)
            return;

        _cwsLoaded++;
        Log.Write("ext", "store page: the install button script is running");
    }

    private static async Task CwsGuard(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Write("ext", "failed " + what + " (" + ex.GetType().Name + ")");
        }
    }

    // ---- one install ----

    private async Task CwsInstall(Tab tab, string id)
    {
        // One at a time. The dialog covers the window, so the person cannot click again while it is open.
        if (_cws is not null || _closed || !_tabs.Contains(tab) || root.XamlRoot is not { } xr)
            return;

        // Set before the first await, so that a second message cannot open a second question.
        var flow = new CwsFlow(tab, id);
        _cws = flow;
        Log.Write("ext", "store " + id + ": install asked for");

        var preparing = Task.CompletedTask;

        try
        {
            var box = new CwsBox(CwsSkin(), xr);
            box.Add.Click += (s, a) => flow.Adding = CwsAdd(flow, box);

            // The question is on screen first; what goes in it is fetched meanwhile.
            var shown = CwsShow(flow, box.Dialog);
            preparing = CwsPrepare(flow, box);
            await shown;
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "store " + id + ": the question failed");
        }
        finally
        {
            // The question is over: whatever is still being done for it is not wanted.
            flow.Stop.Cancel();
            _cws = null;
        }

        // Unpacking cannot be interrupted, so the work may still be winding down. What it leaves behind is
        // removed once it has; the next question does not wait for that.
        await preparing;
        await flow.Adding;

        Log.Write("ext", "store " + id + ": " + (flow.Added ? "added" : "not added"));
        CwsClean(flow);
        flow.Stop.Dispose();
    }

    /// <summary>
    /// Everything between the question opening and the question being asked: it ends with the dialog showing
    /// the extension, or a sentence. Never throws. A dialog that has been closed meanwhile cancels it.
    /// </summary>
    private async Task CwsPrepare(CwsFlow f, CwsBox box)
    {
        var ct = f.Stop.Token;

        void Fail(string why)
        {
            Log.Write("ext", "store " + f.Id + ": " + why);
            box.Say("Could not add the extension", why);
        }

        try
        {
            var store = ExtAdd.Store;

            var (has, known) = await CwsHas(f.Id);
            ct.ThrowIfCancellationRequested();
            if (has)
            {
                Log.Write("ext", "store " + f.Id + ": already added");
                var what = Cws.Tidy(known, 60) is { Length: > 0 } n ? "\u201c" + n + "\u201d" : "This extension";
                box.Say("Already added", what + " is in NEAT already. You can manage it in Settings, under Extensions.");
                return;
            }

            if (!Ext.Live)
            {
                Fail(Ext.Can ? "The browser is still starting. Try again in a moment." : Ext.Why);
                return;
            }

            // The engine's version goes in the address, as Chrome's own would; read here, on the thread of the web view.
            var version = f.Tab.View.CoreWebView2?.Environment.BrowserVersionString ?? string.Empty;

            // 1. The download.
            string pack;
            try
            {
                pack = await Task.Run(() => store.Download());
                f.Pack = pack;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("ext", ex, "store " + f.Id + ": no place for the download");
                Fail(Cws.Disk);
                return;
            }

            var why = await Task.Run(() => Cws.Fetch(f.Id, version, pack, store.PackBytes, ct));
            ct.ThrowIfCancellationRequested();
            if (why is not null)
            {
                Fail(why);
                return;
            }

            // 2. Whose package it is.
            why = await Task.Run(() => CrxSig.Check(pack, f.Id));
            ct.ThrowIfCancellationRequested();
            if (why is not null)
            {
                Fail(why);
                return;
            }

            // 3. Unpacked, and the file is not needed any more.
            var (stage, err) = await Task.Run(() => store.Stage(pack));
            f.Stage = stage;
            f.Pack = null;
            _ = ExtAdd.Keep(() => store.Release(pack));
            ct.ThrowIfCancellationRequested();
            if (stage is null)
            {
                Fail(err ?? "The package could not be opened.");
                return;
            }

            // 4. What it is and what it can do.
            var (meta, bad) = await Task.Run(() => ExtMeta.Read(store.StagePath(stage)));
            ct.ThrowIfCancellationRequested();
            if (meta is null)
            {
                Fail(bad ?? "The package is not an extension.");
                return;
            }

            var bytes = meta.Icon is { } rel ? await Task.Run(() => CwsIcon(store.StagePath(stage), rel)) : null;
            var icon = await CwsExtIcon(bytes, box.Skin);
            ct.ThrowIfCancellationRequested();

            // 5. The question. Nothing is added before the answer.
            f.Meta = meta;
            box.Ask(Cws.Tidy(meta.Name, 60), CwsNeatIcon(box.Skin), icon, meta.AskText);
        }
        catch (OperationCanceledException)
        {
            // The question was closed: nothing more to do or to say.
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "store " + f.Id + ": the install failed");

            if (!ct.IsCancellationRequested)
                box.Say("Could not add the extension", "The extension could not be added.");
        }
    }

    /// <summary>
    /// Whether the extension of this store id is in, and its name when the browser can say it. The list of
    /// copies says it came from the store; the browser's own list says it is still there (an extension that
    /// was removed some other way is not in).
    /// </summary>
    private static async Task<(bool Has, string Name)> CwsHas(string id)
    {
        var web = await Task.Run(() => ExtAdd.Store.WebId(id));
        if (web is null)
            return (false, string.Empty);

        // With no live profile to ask, the list of copies is all there is.
        if (!Ext.Live)
            return (true, string.Empty);

        var item = (await Ext.All()).FirstOrDefault(x => x.Id == web);
        return item is null ? (false, string.Empty) : (true, item.Name);
    }

    /// <summary>The person said Add to NEAT. Never throws.</summary>
    private async Task CwsAdd(CwsFlow f, CwsBox box)
    {
        if (f.Busy || f.Added || f.Stage is null || f.Meta is not { } meta)
            return;

        try
        {
            // Adding takes a moment (the copy, then the browser). The dialog stays, says so, and cannot be closed meanwhile.
            f.Busy = true;
            box.Adding();

            var done = await ExtAdd.Install(f.Stage, string.Empty, meta, f.Id, () => f.Stage = null);
            f.Busy = false;

            if (done.Res.Ok)
            {
                f.Added = true;
                box.Dialog.Hide();
                return;
            }

            // After a failure the person may try again, as long as the staging folder is still there to add from.
            box.Problem(done.Res.Err ?? "The extension could not be added.", f.Stage is not null);
        }
        catch (Exception ex)
        {
            f.Busy = false;
            Log.Error("ext", ex, "store " + f.Id + ": adding failed");

            if (!f.Added)
                box.Problem("The extension could not be added.", f.Stage is not null);
        }
    }

    // The icon's bytes, or null for one that cannot be used. The file was checked when the manifest was read;
    // it is checked again here, and its size in pixels as well, because it is decoded before the person has said yes.
    private static byte[]? CwsIcon(string folder, string rel)
    {
        try
        {
            if (ExtMeta.Usable(folder, rel) is not { } clean)
                return null;

            var bytes = File.ReadAllBytes(Path.Combine(folder, clean));
            return ExtMeta.Sane(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // What an install leaves behind, whatever way it ended.
    private static void CwsClean(CwsFlow f)
    {
        var store = ExtAdd.Store;

        if (f.Pack is { } pack)
        {
            f.Pack = null;
            _ = ExtAdd.Keep(() => store.Release(pack));
        }

        if (f.Stage is { } stage)
        {
            f.Stage = null;
            _ = ExtAdd.Keep(() => store.Drop(stage));
        }
    }

    // ---- the dialog ----

    // The window's colours for a dialog (a Skin of its own, like every window that draws with one).
    private static Skin CwsSkin()
    {
        var skin = new Skin();
        skin.Apply(App.Look.Cur);
        return skin;
    }

    // Shows the dialog and comes back when it has closed. Only one dialog can be open in a window: if another
    // one is, ShowAsync throws, and the install is dropped (the line in the log says why).
    private static async Task CwsShow(CwsFlow f, ContentDialog box)
    {
        f.Box = box;

        // Adding cannot be walked away from halfway.
        box.Closing += (s, a) =>
        {
            if (f.Busy)
                a.Cancel = true;
        };

        try
        {
            await box.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "store " + f.Id + ": the dialog could not be shown");
        }
        finally
        {
            f.Box = null;
        }
    }

    // NEAT's logo (neat.png next to neat.exe, see neat.csproj); a glyph when it is not there.
    private static UIElement CwsNeatIcon(Skin skin)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "neat.png");

        try
        {
            if (File.Exists(file))
            {
                var img = new BitmapImage { DecodePixelWidth = CwsIconDecode, UriSource = new Uri(file) };
                return new Image { Source = img, Width = CwsIconSize, Height = CwsIconSize, Stretch = Stretch.Uniform };
            }
        }
        catch (Exception ex)
        {
            Log.Write("ext", "NEAT's icon could not be shown (" + ex.GetType().Name + ")");
        }

        return new FontIcon { Glyph = "", FontSize = 32, Foreground = skin.Fg };
    }

    // The extension's icon; the puzzle piece of the Extensions tab when it has none that can be used.
    private static async Task<UIElement> CwsExtIcon(byte[]? png, Skin skin)
    {
        if (png is not null)
        {
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
                stream.Seek(0);

                // The width is set before the picture, so a big one is never decoded at its own size.
                var img = new BitmapImage { DecodePixelWidth = CwsIconDecode };
                await img.SetSourceAsync(stream);
                return new Image { Source = img, Width = CwsIconSize, Height = CwsIconSize, Stretch = Stretch.Uniform };
            }
            catch (Exception ex)
            {
                Log.Write("ext", "the extension's icon could not be shown (" + ex.GetType().Name + ")");
            }
        }

        return new FontIcon { Glyph = "", FontSize = 32, Foreground = skin.Dim };
    }
}
