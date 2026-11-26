using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace neat;

/// <summary>
/// The "Add to NEAT" button on the Chrome Web Store: the window's side of it. The page script is in
/// web/cwsjs.cs, the download in web/cws.cs, the signature check in web/crxsig.cs, and what is done with
/// an extension once it is accepted in web/extadd.cs (the Extensions page uses the same). This file connects
/// them to the window and asks the person.
///
/// How an install travels:
///   1. The page script says which extension the person clicked (cws-install, the id from the address) and the
///      window answers "busy" to that tab.
///   2. The package is downloaded from Google into a file of its own (never held in memory), its signature is
///      checked (it must be a CRX3 for this very id), and it is unpacked into a staging folder. Nothing of it runs.
///   3. Only then is the person asked, in a dialog: the extension's name and icon, what it can do (the same words
///      and the same red warning as the Extensions page), and Add to NEAT or Cancel. Nothing is added without that click.
///   4. Add keeps a copy, tells the browser, and the page says "Added". Cancel, Esc, closing the tab or the window
///      drops everything (the staging folder and the file) and the page goes back to "Add to NEAT".
///
/// What is accepted from a page: a text message that starts with this window's prefix, from the store (https, its
/// host, the default port) in the page the tab shows, about the extension whose page that is. A page can post
/// anything, so none of that is trusted for more than what it is worth: the id is checked again against the
/// address of the tab, the extension asked about is shown to the person with what it can do, and the question is
/// the protection. One install runs at a time in the window; another click gets "Add to NEAT" back.
/// Frames speak through their own channel (win.frames.cs), which this file does not listen to.
///
/// Not written to the log: addresses, paths, anything the download said. The id, the name (tidied) and the outcome are.
/// </summary>
public sealed partial class Win
{
    // The longest message that is looked at. The script's own are under a hundred bytes.
    private const int CwsMaxMessage = 1024;

    // How many times a page without the button is written to the log in one run.
    private const int CwsMaxMissing = 5;

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

        /// <summary>Cancelled when the tab or the window goes away.</summary>
        public CancellationTokenSource Stop { get; } = new();

        /// <summary>The downloaded file, until it is unpacked or the install ends.</summary>
        public string? Pack;

        /// <summary>The staging folder, until the extension is kept or the install ends.</summary>
        public string? Stage;

        /// <summary>The dialog on screen, if there is one.</summary>
        public ContentDialog? Box;

        /// <summary>True while the extension is being added: the dialog cannot be closed then.</summary>
        public bool Busy;

        /// <summary>True once the extension is in.</summary>
        public bool Added;
    }

    private CwsFlow? _cws;
    private int _cwsMissing;

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
                case "cws-nobutton":
                    CwsMissing();
                    break;

                case "cws-ready" when Cws.IsId(id):
                    // The extension has to be the one of the page the tab is on.
                    if (Cws.PageId(core.Source) == id)
                        _ = CwsGuard(() => CwsReady(tab, id!), "answering the store page");

                    break;

                case "cws-install" when Cws.IsId(id):
                    if (Cws.PageId(core.Source) == id)
                        _ = CwsGuard(() => CwsInstall(tab, id!), "adding an extension from the store");
                    else
                        CwsReply(tab, id!, "add");   // so that the button does not stay on "Adding..."

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

    // A page without the button, as the script saw it. Written to the log, a few times at most, so that the
    // person who reports "the button is not there" has a line to point at.
    private void CwsMissing()
    {
        if (_cwsMissing >= CwsMaxMissing)
            return;

        _cwsMissing++;
        Log.Write("ext", "store page: install button not found (the store's page may have changed)");
    }

    /// <summary>Tells a tab the state of the button for an extension. Nothing happens for a tab that has gone.</summary>
    private void CwsReply(Tab tab, string id, string state)
    {
        try
        {
            if (_closed || !_tabs.Contains(tab) || tab.View.CoreWebView2 is not { } core)
                return;

            // Both values come from this code (state) or have been checked (id: 32 letters), so nothing needs escaping.
            core.PostWebMessageAsString(CwsScript.Prefix(_tok) + "{\"state\":\"" + state + "\",\"id\":\"" + id + "\"}");
        }
        catch (Exception ex)
        {
            Log.Write("ext", "the store page could not be answered (" + ex.GetType().Name + ")");
        }
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

    // ---- the state of the button ----

    private async Task CwsReady(Tab tab, string id)
    {
        var has = await CwsHas(id);

        // A click may have started the install while this was being looked up.
        var state = _cws is { } f && f.Tab == tab && f.Id == id ? "busy" : has ? "added" : "add";
        CwsReply(tab, id, state);
    }

    /// <summary>
    /// True when the extension of this store id is in. The list of copies says it came from the store; the
    /// browser's own list says it is still there (an extension that was removed some other way is not "Added").
    /// </summary>
    private static async Task<bool> CwsHas(string id)
    {
        var web = await Task.Run(() => ExtAdd.Store.WebId(id));
        if (web is null)
            return false;

        // With no live profile to ask, the list of copies is all there is.
        if (!Ext.Live)
            return true;

        return (await Ext.All()).Any(x => x.Id == web);
    }

    // ---- one install ----

    private async Task CwsInstall(Tab tab, string id)
    {
        if (_cws is { } other)
        {
            // One at a time. The one that asked for this is told so; any other gets its button back.
            CwsReply(tab, id, other.Tab == tab && other.Id == id ? "busy" : "add");
            return;
        }

        // Set before the first await, so that a second click cannot start a second install.
        var flow = new CwsFlow(tab, id);
        _cws = flow;
        CwsReply(tab, id, "busy");
        Log.Write("ext", "store " + id + ": install asked for");

        var state = "add";

        try
        {
            if (await CwsRun(flow))
                state = "added";
        }
        catch (OperationCanceledException)
        {
            // The tab or the window went away: nothing more to do or to say.
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "store " + id + ": the install failed");
            await CwsError(flow, "The extension could not be added.");
        }
        finally
        {
            _cws = null;
            CwsClean(flow);
            CwsReply(tab, id, state);
            flow.Stop.Dispose();
        }
    }

    // Everything between the click and the answer. True when the extension is in (or was already).
    private async Task<bool> CwsRun(CwsFlow f)
    {
        var store = ExtAdd.Store;
        var ct = f.Stop.Token;

        if (await CwsHas(f.Id))
        {
            Log.Write("ext", "store " + f.Id + ": already added");
            return true;
        }

        if (!Ext.Live)
            return await CwsFail(f, Ext.Can ? "The browser is still starting. Try again in a moment." : Ext.Why);

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
            return await CwsFail(f, Cws.Disk);
        }

        var why = await Task.Run(() => Cws.Fetch(f.Id, version, pack, store.PackBytes, ct));
        ct.ThrowIfCancellationRequested();
        if (why is not null)
            return await CwsFail(f, why);

        // 2. Whose package it is.
        why = await Task.Run(() => CrxSig.Check(pack, f.Id));
        ct.ThrowIfCancellationRequested();
        if (why is not null)
            return await CwsFail(f, why);

        // 3. Unpacked, and the file is not needed any more.
        var (stage, err) = await Task.Run(() => store.Stage(pack));
        f.Stage = stage;
        f.Pack = null;
        _ = ExtAdd.Keep(() => store.Release(pack));
        ct.ThrowIfCancellationRequested();
        if (stage is null)
            return await CwsFail(f, err ?? "The package could not be opened.");

        // 4. What it is and what it can do.
        var (meta, bad) = await Task.Run(() => ExtMeta.Read(store.StagePath(stage)));
        if (meta is null)
            return await CwsFail(f, bad ?? "The package is not an extension.");

        var icon = meta.Icon is { } rel ? await Task.Run(() => CwsIcon(store.StagePath(stage), rel)) : null;
        ct.ThrowIfCancellationRequested();

        // 5. The question. Nothing is added before the answer.
        return await CwsAsk(f, meta, icon);
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

    // ---- the dialogs ----

    private async Task<bool> CwsFail(CwsFlow f, string why)
    {
        Log.Write("ext", "store " + f.Id + ": " + why);
        await CwsError(f, why);
        return false;
    }

    /// <summary>A failure before the question, as a dialog: there is no other place in the window to say it.</summary>
    private async Task CwsError(CwsFlow f, string why)
    {
        if (f.Stop.IsCancellationRequested || _closed || root.XamlRoot is not { } xr)
            return;

        var skin = CwsSkin();
        var box = new ContentDialog
        {
            XamlRoot = xr,
            Title = "Could not add the extension",
            Content = new TextBlock { Text = why, FontSize = 14, Foreground = skin.Fg, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = skin.Dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = skin.Bg,
            Foreground = skin.Fg,
        };

        await CwsShow(f, box);
    }

    // The window's colours for a dialog (a Skin of its own, like every window that draws with one).
    private static Skin CwsSkin()
    {
        var skin = new Skin();
        skin.Apply(App.Look.Cur);
        return skin;
    }

    // Shows a dialog and says how it was answered. Only one dialog can be open in a window: if another one is,
    // ShowAsync throws, and the answer is "closed" (the install is dropped, and the line in the log says why).
    private static async Task<ContentDialogResult> CwsShow(CwsFlow f, ContentDialog box)
    {
        f.Box = box;

        try
        {
            return await box.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error("ext", ex, "store " + f.Id + ": the dialog could not be shown");
            return ContentDialogResult.None;
        }
        finally
        {
            f.Box = null;
        }
    }

    /// <summary>
    /// The question: Add "name" to NEAT? With what the extension can do, in the words of the Extensions page.
    /// True when the extension was added. Cancel, Esc, closing the tab and closing the window all say no.
    /// </summary>
    private async Task<bool> CwsAsk(CwsFlow f, ExtMeta meta, byte[]? iconBytes)
    {
        if (f.Stop.IsCancellationRequested || _closed || root.XamlRoot is not { } xr)
            return false;

        var skin = CwsSkin();
        var name = Cws.Tidy(meta.Name, 60);

        // The two pictures: NEAT, an arrow, the extension. A picture that cannot be had is a glyph.
        var pictures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 12) };
        pictures.Children.Add(CwsNeatIcon(skin));
        pictures.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = skin.Dim, VerticalAlignment = VerticalAlignment.Center });
        pictures.Children.Add(await CwsExtIcon(iconBytes, skin));

        var title = new StackPanel();
        title.Children.Add(pictures);
        title.Children.Add(new TextBlock
        {
            Text = "Add \"" + name + "\" to NEAT?",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = skin.Fg,
            TextWrapping = TextWrapping.Wrap,
        });

        // What the extension is, and what it can do. The same words as the Extensions page, from the same place.
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 12, 0) };

        var about = "Version " + Cws.Tidy(meta.Version, 30);
        if (Cws.Tidy(meta.Description, 300) is { Length: > 0 } words)
            about += "\n" + words;

        body.Children.Add(new TextBlock { Text = about, FontSize = 12, Foreground = skin.Dim, TextWrapping = TextWrapping.Wrap });

        if (meta.RiskText is { } risk)
        {
            var warn = Block.Warn(risk);
            warn.Margin = new Thickness(0);
            body.Children.Add(warn);
        }

        if (meta.CanText is { } can)
            body.Children.Add(new TextBlock { Text = can, FontSize = 13, Foreground = skin.Fg, TextWrapping = TextWrapping.Wrap });

        if (meta.LegacyText is { } legacy)
            body.Children.Add(new TextBlock { Text = legacy, FontSize = 12, Foreground = skin.Dim, TextWrapping = TextWrapping.Wrap });

        var fail = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(fail);

        var box = new ContentDialog
        {
            XamlRoot = xr,
            Title = title,
            Content = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body },
            PrimaryButtonText = "Add to NEAT",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = skin.Dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = skin.Bg,
            Foreground = skin.Fg,
        };

        // Adding takes a moment (the copy, then the browser). The dialog stays, says so, and cannot be closed meanwhile.
        box.PrimaryButtonClick += async (s, a) =>
        {
            var deferral = a.GetDeferral();

            try
            {
                f.Busy = true;
                fail.Visibility = Visibility.Collapsed;
                box.IsPrimaryButtonEnabled = false;
                box.PrimaryButtonText = "Adding...";

                var done = await ExtAdd.Install(f.Stage, string.Empty, meta, f.Id, () => f.Stage = null);

                if (done.Res.Ok)
                {
                    f.Added = true;
                    a.Cancel = false;
                }
                else
                {
                    a.Cancel = true;
                    fail.Text = done.Res.Err ?? "The extension could not be added.";
                    fail.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Log.Error("ext", ex, "store " + f.Id + ": adding failed");
                a.Cancel = true;
                fail.Text = "The extension could not be added.";
                fail.Visibility = Visibility.Visible;
            }
            finally
            {
                f.Busy = false;

                // After a failure the person may try again, as long as the staging folder is still there to add from.
                box.PrimaryButtonText = "Add to NEAT";
                box.IsPrimaryButtonEnabled = !f.Added && f.Stage is not null;
                deferral.Complete();
            }
        };

        box.Closing += (s, a) =>
        {
            if (f.Busy)
                a.Cancel = true;
        };

        await CwsShow(f, box);

        Log.Write("ext", "store " + f.Id + " (" + name + "): " + (f.Added ? "added" : "not added"));
        return f.Added;
    }

    // NEAT's own icon (the file next to neat.exe, which is the exe's icon too); a glyph when it is not there.
    private static UIElement CwsNeatIcon(Skin skin)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "neat.ico");

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
