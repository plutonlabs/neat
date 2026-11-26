using System.IO.Compression;
using System.Runtime.InteropServices;
using neat;

// Self-check of the extension code that needs no WebView2: the wrappers (src/neat/web/ext.cs), the
// manifest reader (extmeta.cs), the folder store (extstore.cs) and the .crx/.zip unpacker (extpack.cs). Plain asserts, no test package: it
// prints one line per check and exits with 1 if any failed, which fails the CI build.
//
// The wrappers reach a profile only through IExtProf, so a fake stands in for it here. What this
// proves is the contract the Settings page will rely on: a fresh profile lists nothing, nothing
// throws, and a failure comes back as a sentence.
//
// The Chrome Web Store button's parts are checked in the files next to this one (the same class, in pieces):
// Crx.cs the signature check, Fetch.cs the downloader, Store.cs the store ids, the downloads folder and the icon.

internal static partial class Program
{
    private static int _ran;
    private static int _failed;

    // The temporary folders the checks made; they are deleted at the end.
    private static readonly List<string> Made = new();

    private static int Main()
    {
        Run("a fresh profile lists no extensions", FreshProfile);
        Run("nothing works, and nothing throws, while extensions are off", WhileOff);
        Run("the reason is kept, and cleared when extensions come on", Reason);
        Run("without a live tab there is nothing to list and a clear message", NoTab);
        Run("add, switch and remove an extension", AddSwitchRemove);
        Run("an unknown id is reported, not thrown", UnknownId);
        Run("a failing add comes back as a sentence", FailingAdd);
        Run("a failing list is an empty list", FailingList);
        Run("a resolver that throws is the same as no tab", ThrowingResolver);
        Run("an empty folder name is refused", EmptyFolder);
        Run("Live needs extensions on and a tab", LiveState);

        Run("a manifest is read: name, version, description, format", ManifestBasics);
        Run("a translated name comes from _locales, and a hostile locale is not followed", ManifestTranslated);
        Run("a folder that is not an extension is explained", ManifestBad);
        Run("permissions come out as plain words, the sites first", PermissionWords);
        Run("sites are named, or counted when there are many", PermissionSites);

        Run("a copy skips _metadata, keeps _locales, and does not follow links", StoreCopy);
        Run("a copy that is too big is refused and leaves nothing", StoreTooBig);
        Run("a copy is linked, read back and forgotten with its folder", StoreLink);
        Run("a list entry that leads out of the store is ignored", StoreEscape);
        Run("a folder that was left over is deleted by the sweep", StoreSweep);

        Run("high risk permissions are called out, the others are not", RiskWords);
        Run("every permission on the high risk list is called out", RiskEach);

        Run("a zip is unpacked: files and folders, _locales kept, _metadata left out", PackZip);
        Run("a folder zipped with its own name round it is unpacked from inside", PackWrapped);
        Run("a zip with a ../ entry is refused and nothing is written anywhere", PackSlip);
        Run("absolute, drive, stream, device and trailing-dot names are refused", PackNames);
        Run("a package without a manifest.json at its top is refused", PackNoManifest);
        Run("a file that is not a zip, and a damaged zip, are refused with a sentence", PackNotZip);
        Run("too many files, or too much data, is refused and leaves nothing", PackCaps);
        Run("a zip that lies about its size is held to the cap", PackLies);
        Run("a .crx (version 2 and 3) is unpacked from after its header", PackCrx);
        Run("a .crx with a header that does not add up is refused", PackCrxBad);
        Run("a package is staged, settled into the store, or dropped", StageFlow);
        Run("a refused package leaves nothing in staging", StageRefused);
        Run("the sweep deletes old staging folders, not held ones, not installed extensions", StageSweep);

        CrxChecks();
        FetchChecks();
        StoreChecks();

        foreach (var dir in Made)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temporary folder that stays is not a failure.
            }
        }

        Console.WriteLine($"{_ran - _failed} of {_ran} checks passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<Task> body)
    {
        _ran++;
        try
        {
            // The state is static, so every check starts from the same one.
            Ext.Set(true, null);
            Ext.Src = null;
            body().GetAwaiter().GetResult();
            Console.WriteLine("ok    " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name + ": " + ex.Message);
        }
    }

    private static void Need(bool ok, string what)
    {
        if (!ok)
            throw new Exception(what);
    }

    private static async Task FreshProfile()
    {
        Ext.Src = () => new Fake();
        var all = await Ext.All();
        Need(all.Count == 0, "expected an empty list, got " + all.Count);
    }

    private static async Task WhileOff()
    {
        Ext.Set(false, "off for the check");
        Ext.Src = () => new Fake();

        Need((await Ext.All()).Count == 0, "list should be empty while off");

        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err == "off for the check", "add should say why it cannot");

        var on = await Ext.Enable("a", true);
        var gone = await Ext.Remove("a");
        Need(!on.Ok && !gone.Ok, "enable and remove should fail while off");
    }

    private static Task Reason()
    {
        Ext.Set(false, null);
        Need(!Ext.Can && Ext.Why.Length > 0, "a reason is needed even when none was given");

        Ext.Set(false, "because");
        Need(Ext.Why == "because", "the given reason should be kept");

        Ext.Set(true, "ignored");
        Need(Ext.Can && Ext.Why == "", "the reason should be cleared when extensions are on");
        return Task.CompletedTask;
    }

    private static async Task NoTab()
    {
        Need((await Ext.All()).Count == 0, "no tab means an empty list");

        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && !string.IsNullOrEmpty(add.Err), "add without a tab should explain");
    }

    private static async Task AddSwitchRemove()
    {
        var fake = new Fake();
        Ext.Src = () => fake;

        var add = await Ext.Add(@"C:\ext\one");
        Need(add.Ok && add.Item is not null && add.Item.Name == "one" && add.Item.On, "added extension should be on");

        var all = await Ext.All();
        Need(all.Count == 1 && all[0].Id == add.Item!.Id, "the list should hold the new extension");

        var off = await Ext.Enable(add.Item!.Id, false);
        Need(off.Ok && !(await Ext.All())[0].On, "extension should be off after switching");

        var gone = await Ext.Remove(add.Item!.Id);
        Need(gone.Ok && (await Ext.All()).Count == 0, "extension should be gone after removing");
    }

    private static async Task UnknownId()
    {
        Ext.Src = () => new Fake();

        var on = await Ext.Enable("nope", true);
        var gone = await Ext.Remove("nope");
        Need(!on.Ok && !gone.Ok && on.Err is not null, "an unknown id should be an error result");
    }

    private static async Task FailingAdd()
    {
        // The HRESULT that WebView2 gives for a folder without a manifest.
        Ext.Src = () => new Fake { Boom = new COMException("x", unchecked((int)0x80070002)) };
        var add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err!.Contains("manifest.json"), "a missing manifest should be named: " + add.Err);

        Ext.Src = () => new Fake { Boom = new InvalidOperationException("odd") };
        add = await Ext.Add(@"C:\x");
        Need(!add.Ok && add.Err == "The extension could not be added.", "an unknown failure gets the plain sentence");
    }

    private static async Task FailingList()
    {
        Ext.Src = () => new Fake { Boom = new InvalidOperationException("closed") };
        Need((await Ext.All()).Count == 0, "a failing list should be empty");
    }

    private static async Task ThrowingResolver()
    {
        Ext.Src = () => throw new ObjectDisposedException("tab");
        Need((await Ext.All()).Count == 0, "list should be empty");
        Need(!(await Ext.Add(@"C:\x")).Ok, "add should fail without throwing");
    }

    private static async Task EmptyFolder()
    {
        Ext.Src = () => new Fake();
        Need(!(await Ext.Add("  ")).Ok, "a blank folder name should be refused");
    }

    private static async Task LiveState()
    {
        Ext.Set(false, "off");
        Ext.Src = () => new Fake();
        Need(!Ext.Live, "not live while extensions are off");

        Ext.Set(true, null);
        Ext.Src = null;
        Need(!Ext.Live, "not live without a tab");

        Ext.Src = () => new Fake();
        Need(Ext.Live, "live with extensions on and a tab");
        await Task.CompletedTask;
    }

    // ---- the manifest and the store (they work on real folders, so each check has a temporary one) ----

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "neat-extcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Made.Add(dir);
        return dir;
    }

    private static void Write(string folder, string file, string text)
    {
        var path = Path.Combine(folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Manifest(string extra = "") =>
        """{ "manifest_version": 3, "name": "Tidy", "version": "1.2.3", "description": "Tidies pages" """ + extra + "}";

    private static Task ManifestBasics()
    {
        var dir = Temp();
        // Chrome allows comments and a trailing comma in a manifest, and extensions use them.
        Write(dir, "manifest.json", "{ // the name\n \"manifest_version\": 3, \"name\": \"Tidy\", \"version\": \"1.2.3\", \"description\": \"Tidies pages\", }");

        var (m, err) = ExtMeta.Read(dir);
        Need(m is not null && err is null, "should read: " + err);
        Need(m!.Name == "Tidy" && m.Version == "1.2.3" && m.Description == "Tidies pages" && m.Mv == 3, "wrong facts");
        Need(m.Can.Count == 0, "no permissions means nothing to warn about");
        return Task.CompletedTask;
    }

    private static Task ManifestTranslated()
    {
        var dir = Temp();
        Write(dir, "manifest.json", Manifest().Replace("\"Tidy\"", "\"__MSG_extName__\"").Replace("}", ", \"default_locale\": \"vi\" }"));
        Write(dir, "_locales/vi/messages.json", """{ "EXTNAME": { "message": "Gọn gàng" } }""");

        var (m, _) = ExtMeta.Read(dir);
        Need(m is not null && m.Name == "Gọn gàng", "the name should come from _locales, any case: " + m?.Name);

        // A locale that tries to leave the folder is not used; the placeholder stays as it is.
        var evil = Temp();
        Write(evil, "manifest.json", Manifest().Replace("\"Tidy\"", "\"__MSG_extName__\"").Replace("}", ", \"default_locale\": \"..\\\\..\" }"));
        var (e, _) = ExtMeta.Read(evil);
        Need(e is not null && e.Name == "__MSG_extName__", "a hostile locale must not be followed");
        return Task.CompletedTask;
    }

    private static Task ManifestBad()
    {
        var none = Temp();
        var (m1, e1) = ExtMeta.Read(none);
        Need(m1 is null && e1!.Contains("manifest.json"), "no manifest should be named: " + e1);

        var broken = Temp();
        Write(broken, "manifest.json", "{ not json");
        var (m2, e2) = ExtMeta.Read(broken);
        Need(m2 is null && !string.IsNullOrEmpty(e2), "broken json should be explained");

        var empty = Temp();
        Write(empty, "manifest.json", """{ "name": "x" }""");
        var (m3, e3) = ExtMeta.Read(empty);
        Need(m3 is null && !string.IsNullOrEmpty(e3), "a manifest without a version is not an extension");

        var array = Temp();
        Write(array, "manifest.json", "[1,2]");
        Need(ExtMeta.Read(array).Meta is null, "a manifest that is not an object is refused");
        return Task.CompletedTask;
    }

    private static Task PermissionWords()
    {
        var dir = Temp();
        Write(dir, "manifest.json", Manifest(""", "permissions": ["storage", "tabs", "history", "webNavigation", "declarativeNetRequest", "madeUp"], "host_permissions": ["<all_urls>"] """));

        var can = ExtMeta.Read(dir).Meta!.Can;
        Need(can[0] == "Read and change all your data on all websites", "all sites should come first: " + can[0]);
        Need(!can.Any(c => c.Contains("storage")), "storage is not warned about");
        Need(can.Contains("Block content on any page"), "declarativeNetRequest should be explained");
        Need(can.Count(c => c == "Read your browsing history") == 1 && can.Contains("Read and change your browsing history"), "history lines");
        Need(can.Contains("Use the \"madeUp\" permission"), "an unknown permission is named as it is");
        return Task.CompletedTask;
    }

    private static Task PermissionSites()
    {
        var some = Temp();
        Write(some, "manifest.json", Manifest(""", "host_permissions": ["https://a.com/*", "https://*.b.org/x", "https://a.com/other"], "content_scripts": [{ "matches": ["https://c.net/*"] }] """));
        var can = ExtMeta.Read(some).Meta!.Can;
        Need(can.Count == 1 && can[0] == "Read and change your data on a.com, *.b.org, c.net", "named sites: " + string.Join("|", can));

        var many = Temp();
        Write(many, "manifest.json", Manifest(""", "host_permissions": ["https://a.com/*", "https://b.com/*", "https://c.com/*", "https://d.com/*"] """));
        Need(ExtMeta.Read(many).Meta!.Can[0] == "Read and change your data on 4 websites", "counted sites");

        // A content script on every page is the same as asking for every site.
        var all = Temp();
        Write(all, "manifest.json", Manifest(""", "content_scripts": [{ "matches": ["*://*/*"] }] """));
        Need(ExtMeta.Read(all).Meta!.Can[0] == "Read and change all your data on all websites", "content scripts count");
        return Task.CompletedTask;
    }

    private static Task StoreCopy()
    {
        var src = Temp();
        Write(src, "manifest.json", Manifest());
        Write(src, "_locales/en/messages.json", "{}");
        Write(src, "_metadata/verified_contents.json", "{}");
        Write(src, "js/a.js", "1");

        var outside = Temp();
        Write(outside, "secret.txt", "no");
        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(src, "link"), outside);
            linked = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No permission to make links here: the rest is still checked.
        }

        var store = new ExtStore(Temp());
        var dir = store.Put(src, "Tidy Up!");
        var to = store.Full(dir);

        Need(dir.StartsWith("tidy-up-"), "folder name should come from the extension's name: " + dir);
        Need(File.Exists(Path.Combine(to, "manifest.json")) && File.Exists(Path.Combine(to, "js", "a.js")), "files should be copied");
        Need(File.Exists(Path.Combine(to, "_locales", "en", "messages.json")), "_locales must be kept");
        Need(!Directory.Exists(Path.Combine(to, "_metadata")), "_metadata must be left out");
        Need(!linked || !File.Exists(Path.Combine(to, "link", "secret.txt")), "links must not be followed");
        return Task.CompletedTask;
    }

    private static Task StoreTooBig()
    {
        var src = Temp();
        Write(src, "manifest.json", Manifest());
        Write(src, "big.bin", new string('x', 5000));

        var root = Temp();
        var store = new ExtStore(root) { MaxBytes = 1000 };

        try
        {
            store.Put(src, "Big");
            throw new Exception("should have been refused");
        }
        catch (InvalidOperationException ex)
        {
            Need(ex.Message.Contains("too big"), "the refusal should say why: " + ex.Message);
        }

        Need(Directory.GetDirectories(root).Length == 0, "a refused copy must leave no folder behind");
        return Task.CompletedTask;
    }

    private static Task StoreLink()
    {
        var src = Temp();
        Write(src, "manifest.json", Manifest());
        var root = Temp();

        var store = new ExtStore(root);
        var dir = store.Put(src, "Tidy");
        store.Link("liveid", dir);

        // A second store on the same folder is what the next start of the app sees.
        var again = new ExtStore(root);
        Need(again.Meta("liveid")?.Version == "1.2.3", "the version should be found again after a restart");
        Need(again.Meta("other") is null, "an extension without a copy has no details");

        again.Forget("liveid");
        Need(!Directory.Exists(again.Full(dir)), "forgetting deletes the copy");
        Need(again.Meta("liveid") is null, "and the link");
        Need(Directory.Exists(src), "the folder it was copied from is never touched");
        return Task.CompletedTask;
    }

    private static Task StoreEscape()
    {
        var root = Temp();
        var victim = Temp();
        Write(victim, "keep.txt", "mine");

        // A list that was edited by hand (or by something else) to point out of the store.
        Write(root, "extensions.json", $$"""{ "v": 1, "dirs": { "a": "..", "b": "{{victim.Replace("\\", "\\\\")}}" }, "gone": ["..", "{{victim.Replace("\\", "\\\\")}}"] }""");

        var store = new ExtStore(root);
        Need(store.Meta("a") is null && store.Meta("b") is null, "entries that are not plain folder names are ignored");

        store.Forget("a");
        store.Forget("b");
        store.Sweep();
        Need(File.Exists(Path.Combine(victim, "keep.txt")), "nothing outside the store may be deleted");
        return Task.CompletedTask;
    }

    private static Task StoreSweep()
    {
        var root = Temp();
        Directory.CreateDirectory(Path.Combine(root, "old-1234"));
        Write(root, "old-1234/manifest.json", "{}");
        Write(root, "extensions.json", """{ "v": 1, "dirs": {}, "gone": ["old-1234"] }""");

        new ExtStore(root).Sweep();
        Need(!Directory.Exists(Path.Combine(root, "old-1234")), "a folder on the gone list should be deleted");

        var left = File.ReadAllText(Path.Combine(root, "extensions.json"));
        Need(!left.Contains("old-1234"), "and taken off the list");
        return Task.CompletedTask;
    }

    // ---- high risk permissions ----

    private static Task RiskWords()
    {
        var dir = Temp();
        Write(dir, "manifest.json", Manifest(""", "permissions": ["storage", "cookies", "notifications", "tabs"], "host_permissions": ["<all_urls>"] """));

        var m = ExtMeta.Read(dir).Meta!;
        Need(m.Risk.Contains("Read and change all your data on all websites"), "all sites is high risk");
        Need(m.Risk.Contains("Read and change your cookies") && m.Risk.Contains("See the addresses and titles of your open tabs"), "cookies and tabs are high risk");
        Need(!m.Risk.Contains("Show notifications") && m.Can.Contains("Show notifications"), "notifications are listed but not high risk");
        Need(m.Risk.All(r => m.Can.Contains(r)), "every high risk line is also one of the lines of what it can do");

        // A few named sites, and permissions that are not on the list, are not high risk.
        var calm = Temp();
        Write(calm, "manifest.json", Manifest(""", "permissions": ["storage", "notifications"], "host_permissions": ["https://a.com/*"] """));
        Need(ExtMeta.Read(calm).Meta!.Risk.Count == 0, "named sites and quiet permissions are not high risk");

        // A content script on every page counts as every site.
        var scripts = Temp();
        Write(scripts, "manifest.json", Manifest(""", "content_scripts": [{ "matches": ["https://*/*"] }] """));
        Need(ExtMeta.Read(scripts).Meta!.Risk.Count == 1, "a content script on every page is high risk");
        return Task.CompletedTask;
    }

    private static Task RiskEach()
    {
        var names = new[] { "cookies", "webRequest", "webRequestBlocking", "declarativeNetRequest", "tabs", "history", "bookmarks", "downloads", "nativeMessaging", "debugger", "management", "proxy" };

        foreach (var name in names)
        {
            var dir = Temp();
            Write(dir, "manifest.json", Manifest(", \"permissions\": [\"" + name + "\"]"));
            Need(ExtMeta.Read(dir).Meta!.Risk.Count == 1, name + " should be high risk");
        }

        foreach (var wide in new[] { "<all_urls>", "*://*/*", "http://*/*", "https://*/*" })
        {
            var dir = Temp();
            Write(dir, "manifest.json", Manifest(", \"host_permissions\": [\"" + wide + "\"]"));
            Need(ExtMeta.Read(dir).Meta!.Risk.Count == 1, wide + " should be high risk");
        }

        return Task.CompletedTask;
    }

    // ---- packages (.zip and .crx) ----

    // A zip in memory. A name that ends in "/" is a folder. The names are written as they are given,
    // which is the point: a hostile package is one that names things it should not.
    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var mem = new MemoryStream();

        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in files)
            {
                var e = zip.CreateEntry(name);
                if (name.EndsWith('/'))
                    continue;

                using var w = new StreamWriter(e.Open());
                w.Write(text);
            }
        }

        return mem.ToArray();
    }

    private static string Save(string file, byte[] bytes)
    {
        var path = Path.Combine(Temp(), file);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Le(int n) => BitConverter.GetBytes(n);

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // The header of a version 3 .crx: magic, version, the size of the header block, the block.
    private static byte[] Crx3(byte[] zip, int block = 40) =>
        Join("Cr24"u8.ToArray(), Le(3), Le(block), new byte[block], zip);

    // The header of a version 2 .crx: magic, version, the sizes of the key and the signature, both.
    private static byte[] Crx2(byte[] zip, int key = 30, int sig = 20) =>
        Join("Cr24"u8.ToArray(), Le(2), Le(key), Le(sig), new byte[key + sig], zip);

    // Tries a package and says what came of it: the sentence it was refused with, or null.
    private static string? TryOpen(string package, out string target, long bytes = 1 << 20, int files = 100)
    {
        target = Path.Combine(Temp(), "out");
        return ExtPack.Open(package, target, bytes, files);
    }

    private static long SizeOf(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;

    private static Task PackZip()
    {
        var pkg = Save("tidy.zip", Zip(
            ("manifest.json", Manifest()),
            ("js/", ""),
            ("js/a.js", "1"),
            ("_locales/en/messages.json", "{}"),
            ("_metadata/verified_contents.json", "{}")));

        var err = TryOpen(pkg, out var to);
        Need(err is null, "should unpack: " + err);
        Need(File.Exists(Path.Combine(to, "manifest.json")) && File.Exists(Path.Combine(to, "js", "a.js")), "files should be written");
        Need(File.Exists(Path.Combine(to, "_locales", "en", "messages.json")), "_locales must be kept");
        Need(!Directory.Exists(Path.Combine(to, "_metadata")), "_metadata must be left out");
        Need(ExtMeta.Read(to).Meta?.Name == "Tidy", "the unpacked folder should read as an extension");
        return Task.CompletedTask;
    }

    private static Task PackWrapped()
    {
        // What Explorer's "send to compressed folder" makes: everything inside one folder.
        var pkg = Save("tidy.zip", Zip(("tidy/", ""), ("tidy/manifest.json", Manifest()), ("tidy/js/a.js", "1")));

        var err = TryOpen(pkg, out var to);
        Need(err is null, "should unpack: " + err);
        Need(File.Exists(Path.Combine(to, "manifest.json")) && File.Exists(Path.Combine(to, "js", "a.js")), "the wrapping folder should be dropped");
        Need(!Directory.Exists(Path.Combine(to, "tidy")), "and not appear inside");

        // Two folders at the top are not a wrapper: there is no manifest.json at the top, so it is refused.
        var two = Save("two.zip", Zip(("a/manifest.json", Manifest()), ("b/x.js", "1")));
        Need(TryOpen(two, out _) is not null, "two top folders are not a wrapper");
        return Task.CompletedTask;
    }

    private static Task PackSlip()
    {
        var home = Temp();
        var pkg = Path.Combine(home, "evil.zip");
        File.WriteAllBytes(pkg, Zip(("manifest.json", Manifest()), ("../evil.txt", "x")));

        var target = Path.Combine(home, "out");
        var err = ExtPack.Open(pkg, target, 1 << 20, 100);

        Need(err is not null && err.Contains("not allowed"), "a ../ entry should be refused: " + err);
        Need(!Directory.Exists(target), "the folder should be deleted");
        Need(!File.Exists(Path.Combine(home, "evil.txt")), "nothing may be written outside the folder");

        // The same, hidden in the middle of a path, and with a backslash.
        foreach (var name in new[] { "a/../../evil.txt", "..\\evil.txt", "a\\..\\..\\evil.txt", "js/../../evil.txt" })
        {
            var p = Save("e.zip", Zip(("manifest.json", Manifest()), (name, "x")));
            Need(TryOpen(p, out var t) is not null, name + " should be refused");
            Need(!Directory.Exists(t), name + " should leave no folder");
        }

        return Task.CompletedTask;
    }

    private static Task PackNames()
    {
        foreach (var name in new[] { "/etc/evil", "//server/share/x", "C:/evil.txt", "C:\\evil.txt", "a.txt:stream", "con.js", "NUL", "js/aux.txt", "manifest.json.", "a b ", "x?.js", "a//b.js" })
        {
            var p = Save("n.zip", Zip(("manifest.json", Manifest()), (name, "x")));
            var err = TryOpen(p, out var t);
            Need(err is not null, "'" + name + "' should be refused");
            Need(!Directory.Exists(t), "'" + name + "' should leave no folder");
        }

        // Names that look odd but are fine.
        var ok = Save("ok.zip", Zip(("./manifest.json", Manifest()), ("js/console.js", "1"), ("a..b/c.js", "1"), (".hidden", "1")));
        Need(TryOpen(ok, out var to) is null, "ordinary names should pass");
        Need(File.Exists(Path.Combine(to, "js", "console.js")) && File.Exists(Path.Combine(to, "a..b", "c.js")), "and be written");
        return Task.CompletedTask;
    }

    private static Task PackNoManifest()
    {
        var p = Save("x.zip", Zip(("readme.txt", "hello")));
        var err = TryOpen(p, out var to);
        Need(err is not null && err.Contains("manifest.json"), "should name the manifest: " + err);
        Need(!Directory.Exists(to), "and leave nothing");

        var empty = Save("empty.zip", Zip(("only/", "")));
        Need(TryOpen(empty, out _) is not null, "a zip of nothing is refused");
        return Task.CompletedTask;
    }

    private static Task PackNotZip()
    {
        var text = Save("a.zip", "this is not a zip"u8.ToArray());
        var err = TryOpen(text, out var t1);
        Need(err is not null && err.Contains(".zip"), "plain text should be refused: " + err);
        Need(!Directory.Exists(t1), "and leave nothing");

        // A zip cut off in the middle: the start is right and the end is gone.
        var whole = Zip(("manifest.json", Manifest()), ("big.js", new string('x', 5000)));
        var cut = Save("cut.zip", whole[..(whole.Length / 2)]);
        var err2 = TryOpen(cut, out var t2);
        Need(err2 is not null && err2.Length > 0, "a cut zip should be refused");
        Need(!Directory.Exists(t2), "and leave nothing");

        Need(TryOpen(Path.Combine(Temp(), "missing.zip"), out _) is not null, "a missing file is a message, not an exception");
        Need(TryOpen(Save("empty.zip", Array.Empty<byte>()), out _) is not null, "an empty file is refused");
        return Task.CompletedTask;
    }

    private static Task PackCaps()
    {
        var many = Save("many.zip", Zip(("manifest.json", Manifest()), ("1.js", "1"), ("2.js", "1"), ("3.js", "1")));
        var err = TryOpen(many, out var t1, files: 3);
        Need(err is not null && err.Contains("too many"), "too many files: " + err);
        Need(!Directory.Exists(t1), "should leave nothing");

        var big = Save("big.zip", Zip(("manifest.json", Manifest()), ("big.bin", new string('x', 200_000))));
        var err2 = TryOpen(big, out var t2, bytes: 50_000);
        Need(err2 is not null && err2.Contains("too big"), "too much data: " + err2);
        Need(!Directory.Exists(t2), "should leave nothing");

        // The same package is fine under a cap that fits it.
        Need(TryOpen(big, out _, bytes: 1 << 20) is null, "and passes under a bigger cap");
        return Task.CompletedTask;
    }

    private static Task PackLies()
    {
        // A zip whose headers claim the big file is 10 bytes. The pre-check sees 10 and lets it in; the
        // copy counts what is really written.
        var zip = Zip(("manifest.json", Manifest()), ("big.bin", new string('x', 200_000)));

        for (var i = 0; i + 46 < zip.Length; i++)
        {
            if (zip[i] != 'P' || zip[i + 1] != 'K' || zip[i + 2] != 1 || zip[i + 3] != 2)
                continue;

            var len = BitConverter.ToUInt16(zip, i + 28);
            if (System.Text.Encoding.ASCII.GetString(zip, i + 46, len) == "big.bin")
                BitConverter.GetBytes(10).CopyTo(zip, i + 24);
        }

        var pkg = Save("lie.zip", zip);
        string? err;
        string to;

        try
        {
            err = TryOpen(pkg, out to, bytes: 50_000);
        }
        catch (Exception ex)
        {
            throw new Exception("a lying header must not throw: " + ex.Message);
        }

        // Either the package is refused, or what was written still fits the cap. Both keep the promise.
        Need(err is not null ? !Directory.Exists(to) : SizeOf(to) <= 50_000, "the cap must hold when the header lies");
        return Task.CompletedTask;
    }

    private static Task PackCrx()
    {
        var zip = Zip(("manifest.json", Manifest()), ("js/a.js", "1"));

        foreach (var (label, bytes) in new[] { ("crx3", Crx3(zip)), ("crx2", Crx2(zip)), ("crx3 with a big header", Crx3(zip, 4000)) })
        {
            var err = TryOpen(Save("tidy.crx", bytes), out var to);
            Need(err is null, label + " should unpack: " + err);
            Need(File.Exists(Path.Combine(to, "manifest.json")) && File.Exists(Path.Combine(to, "js", "a.js")), label + " files should be written");
        }

        // The checks on names do not depend on how the package is wrapped.
        var evil = Save("evil.crx", Crx3(Zip(("manifest.json", Manifest()), ("../evil.txt", "x"))));
        Need(TryOpen(evil, out var t) is not null && !Directory.Exists(t), "a slip in a .crx is refused too");
        return Task.CompletedTask;
    }

    private static Task PackCrxBad()
    {
        var zip = Zip(("manifest.json", Manifest()));

        void Refused(string what, byte[] bytes, string name = "x.crx")
        {
            var err = TryOpen(Save(name, bytes), out var to);
            Need(err is not null && err.Length > 0, what + " should be refused");
            Need(!Directory.Exists(to), what + " should leave nothing");
        }

        Refused("an unknown version", Join("Cr24"u8.ToArray(), Le(4), Le(0), zip));
        Refused("a header longer than the file", Join("Cr24"u8.ToArray(), Le(3), Le(1_000_000), zip));
        Refused("sizes that overflow", Join("Cr24"u8.ToArray(), Le(2), Le(-1), Le(-1), zip));
        Refused("no zip behind the header", Join("Cr24"u8.ToArray(), Le(3), Le(10), new byte[10], "not a zip at all, just text"u8.ToArray()));
        Refused("a file that ends inside the header", Join("Cr24"u8.ToArray(), Le(3)));
        Refused("a wrong magic", Join("Cr25"u8.ToArray(), Le(3), Le(0), zip));
        Refused("a plain zip named .crx", zip);
        Refused("a .crx named .zip", Crx3(zip), "x.zip");
        return Task.CompletedTask;
    }

    private static Task StageFlow()
    {
        var root = Temp();
        var store = new ExtStore(root);
        var pkg = Save("tidy.zip", Zip(("manifest.json", Manifest()), ("a.js", "1")));

        var (stage, err) = store.Stage(pkg);
        Need(stage is not null && err is null, "should stage: " + err);
        Need(File.Exists(Path.Combine(store.StagePath(stage!), "manifest.json")), "the manifest should be readable from staging");
        Need(ExtMeta.Read(store.StagePath(stage!)).Meta?.Name == "Tidy", "and read as an extension");

        // While the person is being asked, a sweep must not take the folder away.
        store.Sweep();
        Need(Directory.Exists(store.StagePath(stage!)), "a held staging folder survives a sweep");

        var dir = store.Settle(stage!, "Tidy Up");
        Need(dir.StartsWith("tidy-up-"), "the folder is named from the extension: " + dir);
        Need(File.Exists(Path.Combine(store.Full(dir), "a.js")), "it moved with everything in it");
        Need(!Directory.Exists(store.StagePath(stage!)), "and is no longer in staging");

        // No: the person said Cancel.
        var (second, _) = store.Stage(pkg);
        store.Drop(second!);
        Need(!Directory.Exists(store.StagePath(second!)), "a dropped staging folder is gone");

        // The path of a name that is not plain cannot lead out of staging.
        Need(!store.StagePath("..").Contains(".."), "a staging name with .. is not followed");
        return Task.CompletedTask;
    }

    private static Task StageRefused()
    {
        var root = Temp();
        var store = new ExtStore(root);

        var (stage, err) = store.Stage(Save("evil.zip", Zip(("manifest.json", Manifest()), ("../x.txt", "x"))));
        Need(stage is null && err is not null, "a slip is refused");

        var big = new ExtStore(root) { PackBytes = 100 };
        var (s2, e2) = big.Stage(Save("big.zip", Zip(("manifest.json", Manifest()), ("big.bin", new string('x', 5000)))));
        Need(s2 is null && e2 is not null, "too much data is refused");

        var left = Path.Combine(root, ".stage");
        Need(!Directory.Exists(left) || Directory.GetDirectories(left).Length == 0, "nothing is left in staging");
        Need(!File.Exists(Path.Combine(root, "x.txt")), "nothing escaped");
        return Task.CompletedTask;
    }

    private static Task StageSweep()
    {
        var root = Temp();
        var store = new ExtStore(root);

        // An installed extension (settled, linked) and a staging folder that an earlier run left.
        var pkg = Save("tidy.zip", Zip(("manifest.json", Manifest())));
        var (stage, _) = store.Stage(pkg);
        var dir = store.Settle(stage!, "Tidy");
        store.Link("liveid", dir);

        Directory.CreateDirectory(Path.Combine(root, ".stage", "oldrun"));
        Write(Path.Combine(root, ".stage", "oldrun"), "manifest.json", "{}");

        // A new store on the same folder is what the next start of the app sees: it holds nothing.
        var next = new ExtStore(root);
        next.Sweep();

        Need(!Directory.Exists(Path.Combine(root, ".stage", "oldrun")), "a staging folder from an earlier run is deleted");
        Need(Directory.Exists(next.Full(dir)) && next.Meta("liveid")?.Name == "Tidy", "an installed extension is never swept");
        return Task.CompletedTask;
    }

    // A profile in memory. Boom, when set, is thrown by every call.
    private sealed class Fake : IExtProf
    {
        private readonly List<ExtItem> _all = new();

        public Exception? Boom;

        public Task<List<ExtItem>> All()
        {
            if (Boom is not null)
                throw Boom;
            return Task.FromResult(_all.ToList());
        }

        public Task<ExtItem> Add(string folder)
        {
            if (Boom is not null)
                throw Boom;
            var x = new ExtItem { Id = Guid.NewGuid().ToString("N"), Name = Path.GetFileName(folder.Replace('\\', '/')), On = true };
            _all.Add(x);
            return Task.FromResult(x);
        }

        public Task<bool> Enable(string id, bool on)
        {
            if (Boom is not null)
                throw Boom;
            var i = _all.FindIndex(x => x.Id == id);
            if (i < 0)
                return Task.FromResult(false);
            _all[i] = new ExtItem { Id = id, Name = _all[i].Name, On = on };
            return Task.FromResult(true);
        }

        public Task<bool> Remove(string id)
        {
            if (Boom is not null)
                throw Boom;
            return Task.FromResult(_all.RemoveAll(x => x.Id == id) > 0);
        }
    }
}
