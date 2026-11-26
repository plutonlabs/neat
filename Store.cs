using neat;

// What the Chrome Web Store button adds to the store of copies (src/neat/web/extstore.cs), to the manifest reader
// (extmeta.cs: the icon, and the words of the question) and what stands between a stranger's manifest and the screen.

internal static partial class Program
{
    private static void StoreChecks()
    {
        Run("an old list (no store ids) still loads", StoreIdsOld);
        Run("a store id is remembered with the extension, and found again after a restart", StoreIdsLink);
        Run("a store id that is not one is not remembered", StoreIdsBad);
        Run("forgetting an extension forgets its store id", StoreIdsForget);
        Run("the same store id on a new extension replaces the old link", StoreIdsReplace);
        Run("a hand-edited list cannot make a store id up", StoreIdsEdited);

        Run("a download gets a .crx name in its own folder, held until it is released", DownloadFlow);
        Run("the sweep deletes the downloads an earlier run left, not the ones in use", DownloadSweep);
        Run("a release deletes only a file of the downloads folder", DownloadRelease);

        Run("the largest icon is chosen, by size and not by name", IconLargest);
        Run("the icon of the toolbar button is the next choice", IconFallback);
        Run("an icon that leads out of the folder is refused", IconTraversal);
        Run("an icon that is not a PNG, is empty, or is too big is skipped", IconBad);
        Run("an icon that is a link is skipped", IconLink);
        Run("a PNG is only drawn when its size in pixels is small", IconPixels);

        Run("the question says what the extension can do, each thing once", WordsOfTheQuestion);
    }

    // ---- store ids ----

    private static (ExtStore Store, string Root, string Dir) Copy(string manifest = "")
    {
        var root = Temp();
        var store = new ExtStore(root);
        return (store, root, store.Put(Src(manifest), "Tidy"));
    }

    private static Task StoreIdsOld()
    {
        var root = Temp();
        Write(root, "tidy-1234/manifest.json", Manifest());
        Write(root, "extensions.json", """{ "v": 1, "dirs": { "liveid": "tidy-1234" }, "gone": [] }""");

        var store = new ExtStore(root);
        Need(store.Meta("liveid")?.Name == "Tidy", "an extension on an old list is still found");
        Need(!store.Has(StoreId) && store.WebId(StoreId) is null, "and none of them came from the store");

        // Writing the list again brings it to the new form, and nothing is lost.
        store.Link("other", "tidy-1234", StoreId);
        var again = new ExtStore(root);
        Need(again.Meta("liveid")?.Name == "Tidy" && again.Has(StoreId), "the old entry and the new one are both there");
        Need(System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(root, "extensions.json")), "\"[vV]\"\\s*:\\s*2"), "the list is now the new version");
        return Task.CompletedTask;
    }

    private static Task StoreIdsLink()
    {
        var (store, root, dir) = Copy();

        store.Link("liveid", dir, StoreId);
        Need(store.Has(StoreId), "the extension is known by the store's id");
        Need(store.WebId(StoreId) == "liveid", "and the browser's id comes back for it");
        Need(!store.Has(new string('p', 32)), "another store id is not there");

        var next = new ExtStore(root);
        Need(next.Has(StoreId) && next.WebId(StoreId) == "liveid", "after a restart too");
        Need(next.Meta("liveid")?.Name == "Tidy", "with its details");

        // An extension that was added from a folder or a file has no store id, and the store page does not claim it.
        var other = store.Put(Src(), "Other");
        store.Link("otherid", other);
        Need(store.Has(StoreId) && store.WebId(StoreId) == "liveid", "a link without a store id changes nothing");
        return Task.CompletedTask;
    }

    // A folder with a manifest in it, to put into a store.
    private static string Src(string manifest = "")
    {
        var dir = Temp();
        Write(dir, "manifest.json", manifest.Length > 0 ? manifest : Manifest());
        return dir;
    }

    private static Task StoreIdsBad()
    {
        var (store, root, dir) = Copy();

        foreach (var bad in new[] { "", "abc", StoreId.ToUpperInvariant(), StoreId + "a", "../../x", "\"},\"x\":{\"", new string('a', 31) + "q" })
        {
            store.Link("liveid", dir, bad);
            Need(!store.Has(bad), "\"" + bad + "\" is not a store id and is not kept");
        }

        Need(!File.ReadAllText(Path.Combine(root, "extensions.json")).Contains("x\":{"), "nothing of it reached the list");
        Need(store.Meta("liveid")?.Name == "Tidy", "the extension itself is linked all the same");
        return Task.CompletedTask;
    }

    private static Task StoreIdsForget()
    {
        var (store, root, dir) = Copy();
        store.Link("liveid", dir, StoreId);

        store.Forget("unknown");
        Need(store.Has(StoreId), "forgetting another extension changes nothing");

        store.Forget("liveid");
        Need(!store.Has(StoreId) && store.WebId(StoreId) is null, "forgetting the extension forgets its store id, so the page offers it again");
        Need(!File.ReadAllText(Path.Combine(root, "extensions.json")).Contains(StoreId), "it is gone from the list");
        Need(!new ExtStore(root).Has(StoreId), "and after a restart");
        Need(!Directory.Exists(store.Full(dir)), "with its copy");
        return Task.CompletedTask;
    }

    private static Task StoreIdsReplace()
    {
        var (store, root, first) = Copy();
        var second = store.Put(Src(), "Tidy again");

        // WebView2 lost the first one, and the person added the extension again from the store.
        store.Link("one", first, StoreId);
        store.Link("two", second, StoreId);

        Need(store.WebId(StoreId) == "two", "the newer one is the one the store id is for");
        Need(store.Meta("one")?.Name == "Tidy", "the older one is still an extension of its own");

        store.Forget("one");
        Need(store.Has(StoreId), "forgetting the older one does not take the store id of the newer");
        Need(new ExtStore(root).WebId(StoreId) == "two", "after a restart too");
        return Task.CompletedTask;
    }

    private static Task StoreIdsEdited()
    {
        var root = Temp();
        Write(root, "tidy-1/manifest.json", Manifest());

        // A store id on an extension that is not on the list, and a value that is not an id: neither is believed.
        Write(root, "extensions.json", $$"""{ "v": 2, "dirs": { "a": "tidy-1" }, "gone": [], "stores": { "ghost": "{{StoreId}}", "a": "not an id" } }""");

        var store = new ExtStore(root);
        Need(!store.Has(StoreId), "an id for an extension nobody has is dropped");
        Need(!store.Has("not an id"), "and so is a value that is no id");
        Need(store.Meta("a")?.Name == "Tidy", "the extension that is on the list is still there");

        // The list itself is damaged: nothing is known, and nothing throws.
        Write(root, "extensions.json", "{ this is not json");
        var broken = new ExtStore(root);
        Need(!broken.Has(StoreId) && broken.WebId(StoreId) is null, "a damaged list knows nothing");
        return Task.CompletedTask;
    }

    // ---- the downloads folder ----

    private static Task DownloadFlow()
    {
        var root = Temp();
        var store = new ExtStore(root);

        var a = store.Download();
        var b = store.Download();

        Need(a != b && a.EndsWith(".crx") && b.EndsWith(".crx"), "each download has a name of its own that ends in .crx: " + a);
        Need(Path.GetFileName(Path.GetDirectoryName(a)!) == ".dl" && Path.GetDirectoryName(Path.GetDirectoryName(a)!) == root, "in the folder of downloads, inside the store: " + a);
        Need(Directory.Exists(Path.GetDirectoryName(a)), "which has been made");
        Need(!File.Exists(a), "the file is not made: the download makes it, and it must not exist yet");

        File.WriteAllText(a, "x");
        store.Release(a);
        Need(!File.Exists(a), "a released download is deleted");
        store.Release(a);
        Need(true, "and releasing it twice is harmless");
        return Task.CompletedTask;
    }

    private static Task DownloadSweep()
    {
        var root = Temp();
        var store = new ExtStore(root);

        var live = store.Download();
        File.WriteAllText(live, "in use");

        // What an earlier run left: a download that was never released, and something that is not ours at all.
        var left = Path.Combine(Path.GetDirectoryName(live)!, "0123456789ab.crx");
        File.WriteAllText(left, "old");

        var (ext, _, dir) = Copy();
        ext.Link("liveid", dir);

        store.Sweep();
        Need(File.Exists(live), "a download that is in use survives a sweep");
        Need(!File.Exists(left), "one that nobody holds (an earlier run's) is deleted");

        // The next start of the app holds nothing, so everything in the folder is old.
        new ExtStore(root).Sweep();
        Need(!File.Exists(live), "and the next run's sweep deletes the one that was in use");
        Need(Directory.Exists(Path.GetDirectoryName(live)!), "but not the folder");

        // Sweeping a store that has no downloads folder, and no folder at all, is nothing.
        new ExtStore(Temp()).Sweep();
        new ExtStore(Path.Combine(Temp(), "not-made-yet")).Sweep();
        return Task.CompletedTask;
    }

    private static Task DownloadRelease()
    {
        var root = Temp();
        var store = new ExtStore(root);
        var path = store.Download();

        // A file somewhere else, with the same name as the download: only the one in the downloads folder is meant.
        var elsewhere = Path.Combine(Temp(), Path.GetFileName(path));
        File.WriteAllText(elsewhere, "mine");
        File.WriteAllText(path, "theirs");

        store.Release(elsewhere);
        Need(File.Exists(elsewhere), "a path outside the downloads folder is not deleted, whatever its name");
        Need(!File.Exists(path), "the name is looked for in the downloads folder");

        var victim = Path.Combine(Temp(), "victim.txt");
        File.WriteAllText(victim, "keep");
        foreach (var odd in new[] { "", "..", "../victim.txt", victim, Path.Combine(root, "extensions.json"), ".dl", "a/../../victim.txt" })
        {
            store.Release(odd);
            Need(File.Exists(victim), "\"" + odd + "\" must not delete anything of mine");
        }

        Need(!File.Exists(Path.Combine(root, "extensions.json")) || File.ReadAllText(Path.Combine(root, "extensions.json")).Length > 0, "nor the list");
        return Task.CompletedTask;
    }

    // ---- the icon ----

    // The first bytes of a PNG of this size: the signature and the header chunk. What is drawn is not the point here.
    private static byte[] Png(uint w, uint h, int padding = 0)
    {
        static byte[] Be(uint n) => new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };

        return Join(
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A },
            Be(13), "IHDR"u8.ToArray(), Be(w), Be(h), new byte[] { 8, 6, 0, 0, 0 }, new byte[4],
            new byte[padding]);
    }

    private static void Put(string folder, string file, byte[] bytes)
    {
        var path = Path.Combine(folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // An extension folder with this manifest (the parts after the version) and these files; gives what was chosen.
    private static string? IconOf(string extra, params (string File, byte[] Bytes)[] files)
    {
        var dir = Temp();
        Write(dir, "manifest.json", """{ "manifest_version": 3, "name": "Tidy", "version": "1" """ + extra + "}");

        foreach (var (file, bytes) in files)
            Put(dir, file, bytes);

        var (meta, err) = ExtMeta.Read(dir);
        Need(meta is not null, "the manifest should be read: " + err);
        return meta!.Icon;
    }

    private static Task IconLargest()
    {
        var p = Png(16, 16);
        var files = new[] { ("i16.png", p), ("i48.png", p), ("i128.png", p), ("i9.png", p) };

        Need(IconOf(""" , "icons": { "16": "i16.png", "48": "i48.png", "128": "i128.png", "9": "i9.png" } """, files) == "i128.png", "the largest, 128, is chosen over 48 and 16 and 9 (as numbers: 9 is not above 128)");
        Need(IconOf(""" , "icons": { "128": "i128.png", "16": "i16.png" } """, files) == "i128.png", "whatever the order in the manifest");
        Need(IconOf(""" , "icons": { "16": "i16.png" } """, files) == "i16.png", "a single one is the one");
        Need(IconOf(""" , "icons": { "big": "i16.png", "48": "i48.png" } """, files) == "i48.png", "a size that is not a number is the least wanted");
        Need(IconOf("", files) is null, "no icons, no icon");
        Need(IconOf(""" , "icons": {} """, files) is null && IconOf(""" , "icons": [] """, files) is null && IconOf(""" , "icons": "i16.png" """, files) is null, "icons that are not a list of icons are none");
        Need(IconOf(""" , "icons": { "48": "sub/dir/i.png" } """, ("sub/dir/i.png", p)) == "sub/dir/i.png", "an icon in a folder is found, with / between the parts");
        Need(IconOf(""" , "icons": { "48": "sub\\dir\\i.png" } """, ("sub/dir/i.png", p)) == "sub/dir/i.png", "a path written with backslashes, which Chrome reads too");
        Need(IconOf(""" , "icons": { "48": "/i48.png" } """, files) == "i48.png", "a leading slash means the extension's own folder, as in Chrome");
        Need(IconOf(""" , "icons": { "48": "I48.PNG" } """, ("I48.PNG", p)) == "I48.PNG", "the file ending is not case sensitive");
        return Task.CompletedTask;
    }

    private static Task IconFallback()
    {
        var p = Png(32, 32);
        var files = new[] { ("a.png", p), ("b.png", p), ("c.png", p), ("big.png", p) };

        Need(IconOf(""" , "action": { "default_icon": { "16": "a.png", "32": "b.png" } } """, files) == "b.png", "the toolbar icon, the largest, when there are no icons");
        Need(IconOf(""" , "action": { "default_icon": "a.png" } """, files) == "a.png", "which may be a single path");
        Need(IconOf(""" , "browser_action": { "default_icon": { "19": "c.png" } } """, files) == "c.png", "and the older kind of toolbar icon");
        Need(IconOf(""" , "icons": { "128": "big.png" }, "action": { "default_icon": "a.png" } """, files) == "big.png", "icons come before the toolbar icon");
        Need(IconOf(""" , "action": { "default_icon": "a.png" }, "browser_action": { "default_icon": "c.png" } """, files) == "a.png", "action before browser_action");

        // The icons that cannot be used are skipped, and the next choice is tried.
        Need(IconOf(""" , "icons": { "128": "missing.png" }, "action": { "default_icon": "a.png" } """, files) == "a.png", "an icon that is not there is passed over, and the toolbar's is used");
        Need(IconOf(""" , "icons": { "128": "missing.png", "48": "a.png" } """, files) == "a.png", "and so is the next size");
        Need(IconOf(""" , "action": { "default_icon": 5 } """, files) is null && IconOf(""" , "action": 5 """, files) is null, "a toolbar icon of a strange kind is none");
        return Task.CompletedTask;
    }

    private static Task IconTraversal()
    {
        var p = Png(16, 16);

        // A real PNG that is outside the extension's folder: the manifest points at it in every way it can.
        var outer = Temp();
        var dir = Path.Combine(outer, "ext");
        Directory.CreateDirectory(dir);
        Put(outer, "secret.png", p);
        Put(dir, "ok.png", p);
        Write(dir, "manifest.json", """{ "manifest_version": 3, "name": "T", "version": "1", "icons": { "128": "../secret.png", "96": "..\\secret.png", "64": "a/../../secret.png", "48": "ok.png" } }""");
        Need(ExtMeta.Read(dir).Meta?.Icon == "ok.png", "every way out is passed over and the one inside is used");

        foreach (var way in new[]
        {
            "../secret.png", "..\\secret.png", "a/../../secret.png", "./../secret.png", "a/./../../secret.png",
            outer.Replace("\\", "\\\\") + "/secret.png", "C:\\Windows\\x.png", "c:/x.png", "//server/share/x.png", "\\\\server\\share\\x.png",
            "ok.png:stream.png", "ok.png::$DATA", "..", ".", "", "/", "nul.png/..", "a\0b.png",
        })
        {
            var escaped = way.Replace("\\", "\\\\").Replace("\0", "\\u0000");
            Write(dir, "manifest.json", $$"""{ "manifest_version": 3, "name": "T", "version": "1", "icons": { "128": "{{escaped}}" } }""");
            var icon = ExtMeta.Read(dir).Meta?.Icon;
            Need(icon is null || icon == "ok.png", "\"" + way + "\" must not lead out of the folder: " + icon);
            Need(icon is null, "\"" + way + "\" is not an icon of this extension: " + icon);
        }

        // The check on its own: what Usable says for the paths that matter.
        Need(ExtMeta.Usable(dir, "../secret.png") is null && ExtMeta.Usable(dir, "ok.png") == "ok.png", "Usable: out is refused, in is accepted");
        Need(ExtMeta.Usable(dir, "ok.png/../ok.png") is null, "Usable: a .. even when it comes back is refused");
        return Task.CompletedTask;
    }

    private static Task IconBad()
    {
        var p = Png(16, 16);
        var many = new[]
        {
            ("text.png", "this is not a picture"u8.ToArray()),
            ("empty.png", Array.Empty<byte>()),
            ("short.png", new byte[] { 0x89, (byte)'P', (byte)'N' }),
            ("huge.png", Png(16, 16, 1024 * 1024)),
            ("pic.jpg", p),
            ("pic.svg", "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()),
            ("pic.png.exe", p),
            ("good.png", p),
        };

        foreach (var (file, _) in many.Where(m => m.Item1 != "good.png"))
            Need(IconOf($$""" , "icons": { "128": "{{file}}", "48": "good.png" } """, many) == "good.png", file + " is skipped and the next one used");

        Need(IconOf(""" , "icons": { "128": "text.png" } """, many) is null, "a text file named .png is no icon");
        Need(IconOf(""" , "icons": { "128": "huge.png" } """, many) is null, "a file over a megabyte is no icon");

        // A folder with the name of a picture.
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "folder.png"));
        Write(dir, "manifest.json", """{ "manifest_version": 3, "name": "T", "version": "1", "icons": { "128": "folder.png" } }""");
        Need(ExtMeta.Read(dir).Meta?.Icon is null, "a folder is no icon");
        return Task.CompletedTask;
    }

    private static Task IconLink()
    {
        var p = Png(16, 16);
        var dir = Temp();
        var outside = Path.Combine(Temp(), "outside.png");
        File.WriteAllBytes(outside, p);

        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link.png"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No right to make a link here (Windows without developer mode): nothing to check on this machine.
            Console.WriteLine("      (no link could be made here, so a link as an icon was not tried)");
            return Task.CompletedTask;
        }

        Put(dir, "real.png", p);
        Write(dir, "manifest.json", """{ "manifest_version": 3, "name": "T", "version": "1", "icons": { "128": "link.png", "48": "real.png" } }""");
        Need(ExtMeta.Read(dir).Meta?.Icon == "real.png", "a link is passed over, and the file is used");
        return Task.CompletedTask;
    }

    private static Task IconPixels()
    {
        Need(ExtMeta.Sane(Png(16, 16)) && ExtMeta.Sane(Png(128, 128)) && ExtMeta.Sane(Png(1024, 1024)), "an icon of the usual sizes is drawn");
        Need(!ExtMeta.Sane(Png(1025, 16)) && !ExtMeta.Sane(Png(16, 1025)), "one that is wide or tall is not");
        Need(!ExtMeta.Sane(Png(100_000, 100_000)) && !ExtMeta.Sane(Png(uint.MaxValue, 1)) && !ExtMeta.Sane(Png(1, uint.MaxValue)), "a few kilobytes that say a hundred million pixels are not");
        Need(!ExtMeta.Sane(Png(0, 16)) && !ExtMeta.Sane(Png(16, 0)), "an icon of no size is not");

        var good = Png(16, 16);
        Need(!ExtMeta.Sane(good[..23]) && !ExtMeta.Sane(Array.Empty<byte>()), "a header that is cut short is not");
        Need(!ExtMeta.Sane(Flip(good, 0)), "one that does not start like a PNG is not");

        var notIhdr = (byte[])good.Clone();
        notIhdr[12] = (byte)'X';
        Need(!ExtMeta.Sane(notIhdr), "and one whose first chunk is not the header is not");
        return Task.CompletedTask;
    }

    // ---- the words of the question ----

    private static Task WordsOfTheQuestion()
    {
        ExtMeta Of(string extra, int mv = 3)
        {
            var dir = Temp();
            Write(dir, "manifest.json", $$"""{ "manifest_version": {{mv}}, "name": "Tidy", "version": "1" {{extra}} }""");
            var (meta, err) = ExtMeta.Read(dir);
            Need(meta is not null, "the manifest should be read: " + err);
            return meta!;
        }

        // Nothing notable.
        var none = Of(""" , "permissions": ["storage"] """);
        Need(none.RiskText is null && none.CanText == "It asks for no special permissions." && none.LegacyText is null, "an extension that asks for nothing says so: " + none.CanText);

        // Only things that are not high risk.
        var quiet = Of(""" , "permissions": ["notifications"] """);
        Need(quiet.RiskText is null && quiet.CanText == "It can:\n\u2022 Show notifications", "it can: " + quiet.CanText);

        // Only high risk things: they are in the warning, and not said again.
        var risky = Of(""" , "permissions": ["cookies"] """);
        Need(risky.RiskText is not null && risky.RiskText.StartsWith("High risk. This extension can:\n\u2022 Read and change your cookies"), "the warning: " + risky.RiskText);
        Need(risky.RiskText!.EndsWith("Only add it if you trust where it came from."), "and what to do about it: " + risky.RiskText);
        Need(risky.CanText is null, "what is in the warning is not said twice: " + risky.CanText);

        // Both: the warning first, then the rest, as "It can also".
        var both = Of(""" , "permissions": ["cookies", "notifications"] """);
        Need(both.RiskText is not null && both.RiskText.Contains("cookies") && !both.RiskText.Contains("notifications"), "the warning holds the risky one only: " + both.RiskText);
        Need(both.CanText == "It can also:\n\u2022 Show notifications", "the rest follows: " + both.CanText);

        // The older format is told so, and the new one is not.
        Need(Of("", 2).LegacyText is { } old && old.Contains("Manifest V2"), "Manifest V2 has its note");
        Need(Of("", 3).LegacyText is null, "Manifest V3 has none");
        return Task.CompletedTask;
    }
}
