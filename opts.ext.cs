using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The Extensions page: the extensions that are installed, each with a switch and a remove button,
/// and a way to add one from a folder or from a .crx or .zip file. What is installed and what it
/// may do come from web/ext.cs (the profile) and web/extmeta.cs (the manifest); where the copies
/// live is web/extstore.cs, and the unpacking of a package is web/extpack.cs. This file only draws
/// them and passes on what the person does.
///
/// It keeps the rules of the Passwords page (opts.pass.cs): every handler goes through Safe, slow
/// work runs on a pool thread, and after every wait the page checks it is still on show before it
/// touches the controls. A task (adding, removing) opens as a card on the page, like there, and the
/// page shares that page's feedback line and card (_note, _task) because only one is on show.
/// </summary>
public sealed partial class Opts
{
    // Where the page stands in Pages (opts.xaml.cs).
    private const int ExtAt = 3;

    // What the person is told, at the top of the page, about what an extension is and can do here.
    private const string ExtLimits =
        "An extension can read and change the pages you visit, so only install ones you trust. "
        + "NEAT runs an extension's pages, its content scripts and its background code in every tab. "
        + "It supports only part of the Chrome extension API, though, and it does not draw an extension's own "
        + "toolbar button or popup, so whatever needs those cannot be used.";

    // Said after every change. WebView2 applies a change at once; a page that is open may still show the old state.
    private const string Reload = "Open pages may need a reload for this to apply.";

    // True while an add, a switch or a removal is running. The page is switched off meanwhile (see Work).
    private bool _xBusy;

    // The folder a package was unpacked into while the person decides whether to add it; null when none.
    private string? _xStage;

    // The copies NEAT keeps of the extensions. One for the whole app (web/extadd.cs), shared with the
    // Chrome Web Store button, used from the UI thread and, for the slow parts, a pool thread.
    private static ExtStore Xs => ExtAdd.Store;

    /// <summary>
    /// Runs bookkeeping on a pool thread: writing the list of copies, deleting a folder. A failure
    /// is logged and goes no further, because the extension itself has already been added or
    /// removed by then and the page must go on.
    /// </summary>
    private static Task Keep(Action work) => ExtAdd.Keep(work);

    // ---- building ----

    private UIElement BuildExt()
    {
        _note = new TextBlock
        {
            FontSize = 12,
            Foreground = _skin.Dim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 12, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _task = new StackPanel();
        _body = new StackPanel();

        _ = Safe(RefreshExt);

        return Block.Page(_note, _task, _body);
    }

    /// <summary>Leaves the page and lets go of its controls.</summary>
    private void LeaveExt()
    {
        // A package that was unpacked and not answered is not wanted any more.
        Unstage();

        _body = null;
        _task = null;
        _note = null;
        _taskMsg = null;
    }

    /// <summary>Reads what is installed and draws the page again.</summary>
    private async Task RefreshExt()
    {
        var body = _body;
        if (body is null)
            return;

        // Copies left over from an earlier removal that the browser still held are deleted now.
        await Keep(() => Xs.Sweep());

        var live = Ext.Live;
        var all = live ? await Ext.All() : new List<ExtItem>();

        // The details come from the manifests, which are files: read off the UI thread.
        var found = await Task.Run(() => all.Select(x => (Item: x, Meta: Xs.Meta(x.Id))).ToList());

        // The person may have gone to another page while this was reading.
        if (_body != body)
            return;

        body.Children.Clear();

        // Without extensions there is nothing to manage: the reason is all the page says.
        if (!Ext.Can || !live)
        {
            body.Children.Add(Block.Head(_skin, "Extensions"));
            body.Children.Add(Block.Card(_skin, Explain(Ext.Can
                ? "The browser is still starting. Open this page again in a moment."
                : Ext.Why)));
            return;
        }

        // What an extension is comes before the way to add one.
        body.Children.Add(Block.Head(_skin, "Before you add one"));
        body.Children.Add(Block.Card(_skin, Explain(ExtLimits)));

        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Children.Add(Btn("From folder...", () => Work(AddFolder)));
        add.Children.Add(Btn("From file...", () => Work(AddFile)));

        var rows = new List<UIElement>
        {
            Block.Row(_skin, "Add an extension",
                "Pick the folder that holds the extension's manifest.json, or a .crx or .zip file. NEAT keeps its own copy, so the original can be moved or deleted afterwards.",
                add),
        };

        if (found.Count == 0)
            rows.Add(Explain("No extensions installed"));

        foreach (var (item, meta) in found)
            rows.Add(ExtRow(item, meta));

        body.Children.Add(Block.Head(_skin, "Extensions"));
        body.Children.Add(Block.Card(_skin, rows.ToArray()));
    }

    private UIElement ExtRow(ExtItem x, ExtMeta? m)
    {
        var name = x.Name.Length > 0 ? x.Name : m?.Name ?? "Extension";

        // The set-before-handler rule of the other pages: building the row switches nothing.
        var sw = new ToggleSwitch { IsOn = x.On, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0 };
        var quiet = false;
        sw.Toggled += async (s, e) =>
        {
            if (quiet)
                return;

            await Safe(() => Work(async () =>
            {
                var on = sw.IsOn;
                var res = await Ext.Enable(x.Id, on);
                if (res.Ok)
                {
                    Log.Write("ext", (on ? "turned on " : "turned off ") + x.Id + " (" + name + ")");
                    Say((on ? "Turned on " : "Turned off ") + name + ". " + Reload);
                    return;
                }

                // It did not change, so the switch goes back to what the browser has.
                quiet = true;
                sw.IsOn = !on;
                quiet = false;
                Say(res.Err ?? "That did not work.");
            }));
        };

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(sw);
        tools.Children.Add(IconBtn("\uE74D", "Remove", () => Start(() => TaskRemoveExt(x, name, m is not null)), true));

        return Block.Row(_skin, name, Describe(m), tools);
    }

    // "Version 1.2.3 · what it does", or nothing when NEAT has no copy to read.
    private static string? Describe(ExtMeta? m)
    {
        if (m is null)
            return null;

        var desc = m.Description.Length > 160 ? m.Description[..160].TrimEnd() + "..." : m.Description;
        return desc.Length == 0 ? "Version " + m.Version : "Version " + m.Version + Sep + desc;
    }

    // ---- adding ----

    /// <summary>Asks for a folder, reads its manifest and shows what the extension is and what it may do.</summary>
    private async Task AddFolder()
    {
        var body = _body;

        // A picker in an unpackaged app needs the window it belongs to.
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || _body != body)
            return;

        // Whatever was waiting for an answer is replaced by this one.
        CloseAdd();

        var path = folder.Path;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            Say("NEAT can only read a folder on this PC.");
            return;
        }

        var (meta, err) = await Task.Run(() => ExtMeta.Read(path));
        if (_body != body)
            return;

        if (meta is null)
        {
            Say(err ?? "That folder is not an extension.");
            return;
        }

        Say(string.Empty);
        TaskAddExt(path, meta, null);
    }

    /// <summary>
    /// Asks for a .crx or .zip file, unpacks it into NEAT's own staging folder (nothing in it is
    /// run), reads its manifest and shows what the extension is and what it may do. Saying no, or
    /// leaving the page, deletes what was unpacked.
    /// </summary>
    private async Task AddFile()
    {
        var body = _body;

        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".crx");
        picker.FileTypeFilter.Add(".zip");

        var file = await picker.PickSingleFileAsync();
        if (file is null || _body != body)
            return;

        CloseAdd();

        var path = file.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Say("NEAT can only read a file on this PC.");
            return;
        }

        Say("Opening " + file.Name + "...");

        var (stage, err) = await Task.Run(() => Xs.Stage(path));
        if (stage is null)
        {
            if (_body == body)
                Say(err ?? "That file could not be opened.");

            return;
        }

        // The person may have gone to another page while it was unpacking. Nobody is left to answer.
        if (_body != body)
        {
            Toss(stage);
            return;
        }

        var folder = Xs.StagePath(stage);
        var (meta, why) = await Task.Run(() => ExtMeta.Read(folder));

        if (meta is null || _body != body)
        {
            Toss(stage);

            if (_body == body)
                Say(why ?? "That package is not an extension.");

            return;
        }

        _xStage = stage;
        Say(string.Empty);
        TaskAddExt(folder, meta, stage);
    }

    /// <summary>
    /// Deletes the package that was unpacked and not answered, if there is one. The deleting is
    /// done off the UI thread, and by the sweep at the next start if it cannot be done now.
    /// </summary>
    private void Unstage()
    {
        var stage = _xStage;
        _xStage = null;

        if (stage is not null)
            Toss(stage);
    }

    private static void Toss(string stage)
    {
        _ = Keep(() => Xs.Drop(stage));
    }

    /// <summary>Closes the question about an extension that is waiting, and deletes what it had unpacked.</summary>
    private void CloseAdd()
    {
        CloseTask();
        Unstage();
    }

    /// <summary>
    /// Runs a change (add, switch, remove) with the page's buttons and switches switched off, so
    /// that a second click cannot start a second change while the first is still running. Only one
    /// runs at a time, which holds even for a key press that gets past a switched-off button.
    /// </summary>
    private async Task Work(Func<Task> job)
    {
        if (_xBusy || _body is null)
            return;

        // WinUI has IsEnabled on a control but not on a panel, so the buttons and switches are
        // found and switched off one by one. Only the ones that were on are touched, so that they
        // go back exactly as they were. What the job adds meanwhile (a new card) is not in the list.
        var off = new List<Control>();
        Controls(_body, off);
        Controls(_task, off);

        _xBusy = true;
        foreach (var c in off)
            c.IsEnabled = false;

        try
        {
            await job();
        }
        finally
        {
            // Controls that the page has dropped meanwhile are switched back too; nobody sees that.
            _xBusy = false;
            foreach (var c in off)
                c.IsEnabled = true;
        }
    }

    // Collects the buttons and switches that are on, anywhere under a part of the page. A card is a
    // Border round a StackPanel, a row is a Grid: those are the only containers the page is made of.
    private static void Controls(UIElement? at, List<Control> into)
    {
        if (at is Button or ToggleSwitch)
        {
            var c = (Control)at;
            if (c.IsEnabled)
                into.Add(c);
        }
        else if (at is Panel panel)
        {
            foreach (var child in panel.Children)
                Controls(child, into);
        }
        else if (at is Border border)
        {
            Controls(border.Child, into);
        }
    }

    /// <summary>
    /// The question before anything is added: what the extension is and, in plain words, what it
    /// can do, with the high risk part said first and said as such. Cancel changes nothing. For a
    /// package, stage is the folder it was unpacked into; for a folder it is null and path is the
    /// folder to copy.
    /// </summary>
    private void TaskAddExt(string path, ExtMeta meta, string? stage)
    {
        async Task Go()
        {
            var body = _body;

            // The copy, WebView2 and the list are the shared core's (web/extadd.cs). A package that has
            // been moved is no longer ours to drop, which the core tells us the moment it happens.
            var done = await ExtAdd.Install(stage, path, meta, null, () => _xStage = null);
            var res = done.Res;

            if (!res.Ok || res.Item is null)
            {
                var why = res.Err ?? "The extension could not be added.";

                // No copy could be made: a package stays unpacked, so the person can try again or say Cancel.
                // A package has been moved by now otherwise, so the question cannot be asked again: the person starts over.
                if (stage is null || !done.Copied)
                {
                    Fail(why);
                }
                else if (_body == body)
                {
                    CloseTask();
                    Say(why);
                }

                return;
            }

            var shown = res.Item.Name.Length > 0 ? res.Item.Name : meta.Name;

            if (_body != body)
                return;

            CloseTask();
            Say("Added " + shown + ". " + Reload);
            await RefreshExt();
        }

        var rows = new List<UIElement>
        {
            Block.Row(_skin, meta.Name, meta.Description.Length > 0 ? meta.Description : null, Block.Value(_skin, "Version " + meta.Version)),
        };

        // The words are ExtMeta's, so that the Chrome Web Store question says the same.
        if (meta.RiskText is { } risk)
            rows.Add(Block.Warn(risk));

        if (meta.CanText is { } can)
            rows.Add(Explain(can));

        if (meta.LegacyText is { } legacy)
            rows.Add(Explain(legacy));

        ShowTask("Add an extension", rows, Btn("Add extension", () => Work(Go)), Btn("Cancel", () => Start(CloseAdd)));
    }

    // ---- removing ----

    private void TaskRemoveExt(ExtItem x, string name, bool hasCopy)
    {
        // This card takes the place of a question that was waiting about a package.
        Unstage();

        async Task Go()
        {
            var body = _body;

            var res = await Ext.Remove(x.Id);
            if (!res.Ok)
            {
                Fail(res.Err ?? "The extension could not be removed.");
                return;
            }

            // It is gone from the browser, so its copy goes too, whether or not the page is still on show.
            await Keep(() => Xs.Forget(x.Id));
            Log.Write("ext", "removed " + x.Id + " (" + name + ")");

            if (_body != body)
                return;

            CloseTask();
            Say("Removed " + name + ". " + Reload);
            await RefreshExt();
        }

        ShowTask(
            "Remove an extension",
            new UIElement[]
            {
                Explain(hasCopy
                    ? "Remove " + name + "? NEAT deletes its own copy of the extension. This cannot be undone."
                    : "Remove " + name + "? This cannot be undone."),
            },
            Btn("Remove", () => Work(Go)),
            Btn("Cancel", () => Start(CloseTask)));
    }
}
