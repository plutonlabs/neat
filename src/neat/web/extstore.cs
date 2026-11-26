using System.Text.Json;

namespace neat;

/// <summary>
/// NEAT's own copies of the extensions, in one folder (Extensions in the data folder), and the
/// list that says which copy belongs to which extension.
///
/// WebView2 does not copy an extension into the profile: it remembers the folder it was added
/// from, and it drops the extension when that folder's contents change or the folder goes away.
/// So an extension must be added from a folder that NEAT owns and never touches again. Each copy
/// lives in a folder of its own, named from the extension's name and a random part, and a copy is
/// never moved or renamed once it has been added (a move makes the extension vanish at the next
/// start). The id WebView2 gives an extension is not the one the Chrome Web Store uses, so a small
/// file (extensions.json) keeps the link from that id to its folder; it is how the version and the
/// description are found again later.
///
/// A package (.crx or .zip) is unpacked into a staging folder first (<see cref="Stage"/>), so that the
/// person can be asked about what it can do before it becomes an extension. Answering yes moves it
/// into place (<see cref="Settle"/>), answering no, or leaving the page, deletes it (<see cref="Drop"/>).
/// A staging folder that is still there at the next start (the app was killed in between) is
/// deleted by <see cref="Sweep"/>; nothing outside the staging folder is ever deleted that way,
/// because a folder of an extension that is installed must never be taken for a leftover.
///
/// A package that is downloaded from the Chrome Web Store is a file first: <see cref="Download"/> gives it
/// a name in a folder of its own (next to the staging folder), and <see cref="Release"/> deletes it when
/// it has been unpacked or refused. A file there that is still there at the next start is deleted by
/// <see cref="Sweep"/> in the same way as a staging folder.
///
/// An extension that was installed from the store is remembered by its store id as well (the list says
/// which extension WebView2 knows by which store id), so the store page can say "Added" for it
/// (<see cref="Has"/>). The folder of a copy is never named after the store id and never changes.
///
/// Used from the UI thread, and from a pool thread for the slow parts (copying, deleting); every
/// access to the list takes a lock. Nothing here depends on WinUI or WebView2, so tests/ext.check
/// runs it as it is.
/// </summary>
internal sealed class ExtStore
{
    private const string MapFile = "extensions.json";

    // Where packages are unpacked until the person has answered. Not a name Put or Settle can make
    // (theirs end in a random part), so it can never be the folder of an extension.
    private const string StageDir = ".stage";

    // Where a package from the store is kept while it is downloaded and checked. Like StageDir, a name
    // that can never be the folder of an extension.
    private const string DownDir = ".dl";

    // The version of the list on disk. 1 had no store ids. A newer file is read as far as it is understood.
    private const int MapVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly object _gate = new();
    private MapData? _map;

    // The staging folders that are in use right now, so that Sweep leaves them alone. Under _gate.
    private readonly HashSet<string> _hold = new(StringComparer.Ordinal);

    // The same for the downloads: the names of the files that are in use right now. Under _gate.
    private readonly HashSet<string> _holdDown = new(StringComparer.Ordinal);

    public ExtStore(string root) => _root = root;

    /// <summary>The folder that holds the copies.</summary>
    public string Root => _root;

    /// <summary>The most a copied extension may weigh, and how many files it may have. Settable for tests.</summary>
    public long MaxBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxFiles { get; init; } = 20000;

    /// <summary>The most a .crx or .zip may weigh once unpacked, and how many files it may hold. Settable for tests.</summary>
    public long PackBytes { get; init; } = 200L * 1024 * 1024;

    public int PackFiles { get; init; } = 5000;

    /// <summary>
    /// The list on disk. Dirs: extension id to the folder name. Gone: folders still to delete. Stores:
    /// extension id to the id the Chrome Web Store uses for it (only for extensions installed from there).
    /// </summary>
    private sealed class MapData
    {
        public int V { get; set; } = MapVersion;
        public Dictionary<string, string> Dirs { get; set; } = new(StringComparer.Ordinal);
        public List<string> Gone { get; set; } = new();
        public Dictionary<string, string> Stores { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Copies an extension's folder into the store and returns the name of the new folder. The copy
    /// is what gets added to WebView2 (see <see cref="Full"/>). Runs on a pool thread; a failure leaves
    /// nothing behind.
    /// </summary>
    public string Put(string source, string name)
    {
        Directory.CreateDirectory(_root);

        var dir = Slug(name) + "-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Copy(Path.GetFullPath(source), Path.Combine(_root, dir));
        }
        catch
        {
            Discard(dir);
            throw;
        }

        return dir;
    }

    /// <summary>The full path of a folder in the store.</summary>
    public string Full(string dir) => Path.Combine(_root, dir);

    /// <summary>
    /// Unpacks a .crx or .zip into a staging folder. Returns the name of the staging folder, or a
    /// sentence for the person when the package is not acceptable (then nothing is left behind).
    /// Runs on a pool thread. Whoever gets a name back owes a <see cref="Settle"/> or a
    /// <see cref="Drop"/> for it.
    /// </summary>
    public (string? Stage, string? Err) Stage(string package)
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        // Held before the folder exists, so that no Sweep can see it without also seeing the hold.
        lock (_gate)
            _hold.Add(id);

        var err = ExtPack.Open(package, Path.Combine(_root, StageDir, id), PackBytes, PackFiles);
        if (err is not null)
        {
            Drop(id);
            return (null, err);
        }

        return (id, null);
    }

    /// <summary>The full path of a staging folder (what to read the manifest from).</summary>
    public string StagePath(string stage) => Path.Combine(_root, StageDir, Plain(stage) ? stage : "x");

    /// <summary>
    /// Moves a staging folder into the store as the copy of an extension and returns its name. This
    /// is the last move the folder makes: it happens before WebView2 is told about it, because
    /// WebView2 remembers the place and loses the extension if the place changes. On a failure the
    /// staging folder is still there and still held, for the caller to <see cref="Drop"/>.
    /// </summary>
    public string Settle(string stage, string name)
    {
        var dir = Slug(name) + "-" + Guid.NewGuid().ToString("N")[..8];

        // Same drive, so it is a rename, not a copy.
        Directory.Move(StagePath(stage), Path.Combine(_root, dir));

        lock (_gate)
            _hold.Remove(stage);

        return dir;
    }

    /// <summary>Deletes a staging folder that nobody wants. One that cannot be deleted now is left for <see cref="Sweep"/>.</summary>
    public void Drop(string stage)
    {
        lock (_gate)
            _hold.Remove(stage);

        if (Plain(stage))
            TryDelete(StagePath(stage));
    }

    /// <summary>
    /// The path for a package that is about to be downloaded: a new name in the downloads folder (which is
    /// made here), ending in .crx because that is how <see cref="ExtPack"/> knows a .crx. The name is held, so
    /// no <see cref="Sweep"/> deletes the file while it is in use. Whoever gets a path owes a
    /// <see cref="Release"/> for it.
    /// </summary>
    public string Download()
    {
        var name = Guid.NewGuid().ToString("N")[..12] + ".crx";

        // Held before the file exists, as with Stage, so that no Sweep can see the file without the hold.
        lock (_gate)
            _holdDown.Add(name);

        try
        {
            var dir = Path.Combine(_root, DownDir);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }
        catch
        {
            lock (_gate)
                _holdDown.Remove(name);

            throw;
        }
    }

    /// <summary>
    /// Lets go of a downloaded package and deletes its file. Only the file's name is used, and it is looked
    /// for in the downloads folder, so whatever path comes in cannot delete anything else. A file that
    /// cannot be deleted now is left for <see cref="Sweep"/>.
    /// </summary>
    public void Release(string path)
    {
        var name = Path.GetFileName(path);
        if (!Plain(name))
            return;

        lock (_gate)
            _holdDown.Remove(name);

        try
        {
            File.Delete(Path.Combine(_root, DownDir, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Records that the extension WebView2 knows by this id lives in this folder. For an extension that
    /// came from the Chrome Web Store, storeId is the id the store knows it by, so that <see cref="Has"/>
    /// can find it. Another extension that was linked to the same store id before loses that link (it
    /// is the same extension, installed again after WebView2 had lost the first one); its folder is left alone.
    /// </summary>
    public void Link(string id, string dir, string? storeId = null)
    {
        lock (_gate)
        {
            var map = Load();
            map.Dirs[id] = dir;

            if (storeId is not null && Cws.IsId(storeId))
            {
                foreach (var other in map.Stores.Where(x => x.Value == storeId && x.Key != id).Select(x => x.Key).ToList())
                    map.Stores.Remove(other);

                map.Stores[id] = storeId;
            }

            Save(map);
        }
    }

    /// <summary>True when an extension that came from the Chrome Web Store under this store id is on the list.</summary>
    public bool Has(string storeId)
    {
        lock (_gate)
            return Load().Stores.ContainsValue(storeId);
    }

    /// <summary>The id WebView2 knows the extension by, for a store id; null when it is not on the list.</summary>
    public string? WebId(string storeId)
    {
        lock (_gate)
            return Load().Stores.FirstOrDefault(x => x.Value == storeId).Key;
    }

    /// <summary>The facts about an extension from its copy; null when there is no copy or it cannot be read.</summary>
    public ExtMeta? Meta(string id)
    {
        string? dir;
        lock (_gate)
            Load().Dirs.TryGetValue(id, out dir);

        if (dir is null || !Plain(dir))
            return null;

        return ExtMeta.Read(Path.Combine(_root, dir)).Meta;
    }

    /// <summary>
    /// Forgets an extension and deletes its copy. A folder that cannot be deleted right now (the
    /// browser may still hold a file open) is kept on a list and deleted by <see cref="Sweep"/> later.
    /// </summary>
    public void Forget(string id)
    {
        string? dir;
        lock (_gate)
        {
            var map = Load();

            // The store id goes with the extension, so the store page offers it again.
            var had = map.Stores.Remove(id);

            if (!map.Dirs.Remove(id, out dir))
            {
                if (had)
                    Save(map);

                return;
            }

            if (Plain(dir))
                map.Gone.Add(dir);

            Save(map);
        }

        Sweep();
    }

    /// <summary>Deletes a copy whose registration failed, so no folder is left that nothing knows about.</summary>
    public void Discard(string dir)
    {
        if (Plain(dir))
            TryDelete(Path.Combine(_root, dir));
    }

    /// <summary>
    /// Deletes the copies that Forget could not delete at the time, and the staging folders and downloaded
    /// packages that nobody is using. The app calls it at every start (win.cws.cs) and the Extensions page
    /// calls it when it opens.
    /// </summary>
    public void Sweep()
    {
        SweepStage();
        SweepDownloads();

        List<string> todo;
        lock (_gate)
            todo = Load().Gone.ToList();

        if (todo.Count == 0)
            return;

        var done = todo.Where(d => Plain(d) && TryDelete(Path.Combine(_root, d))).ToList();

        // Names that are not plain are dropped without being touched: they are not ours to delete.
        var bad = todo.Where(d => !Plain(d));

        lock (_gate)
        {
            var map = Load();
            map.Gone.RemoveAll(d => done.Contains(d) || bad.Contains(d));
            Save(map);
        }
    }

    // Deletes what is in the staging folder and is not held: the rest of a run that ended before its
    // question was answered.
    private void SweepStage()
    {
        var dir = Path.Combine(_root, StageDir);
        string[] found;

        try
        {
            if (!Directory.Exists(dir))
                return;

            found = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // The holds are read AFTER the folder was listed. Stage holds before it makes a folder, so
        // a folder in the list that is not in the holds really is nobody's.
        HashSet<string> held;
        lock (_gate)
            held = _hold.ToHashSet();

        foreach (var d in found)
        {
            if (!held.Contains(Path.GetFileName(d)))
                TryDelete(d);
        }
    }

    // Deletes the files in the downloads folder that are not held: what a run that was killed in the
    // middle of a download or a check left behind. Nothing else is looked at.
    private void SweepDownloads()
    {
        var dir = Path.Combine(_root, DownDir);
        string[] found;

        try
        {
            if (!Directory.Exists(dir))
                return;

            found = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Read after the folder was listed, for the same reason as in SweepStage.
        HashSet<string> held;
        lock (_gate)
            held = _holdDown.ToHashSet();

        foreach (var f in found)
        {
            if (held.Contains(Path.GetFileName(f)))
                continue;

            try
            {
                File.Delete(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- the folder ----

    private void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        // Links are not followed: a folder that points out of itself, or round to itself, is not copied.
        var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

        long bytes = 0;
        var files = 0;

        foreach (var path in Directory.EnumerateFileSystemEntries(from, "*", opt))
        {
            var rel = Path.GetRelativePath(from, path);

            // Chrome puts _metadata (what it checked when it downloaded the extension) in the folders
            // it keeps, and WebView2 refuses a folder that holds one: names that start with an
            // underscore are reserved, apart from _locales.
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.Equals("_metadata", StringComparison.OrdinalIgnoreCase)))
                continue;

            var target = Path.Combine(to, rel);

            if (Directory.Exists(path))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            bytes += new FileInfo(path).Length;
            files++;

            if (bytes > MaxBytes || files > MaxFiles)
                throw new InvalidOperationException("That folder is too big to be an extension.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The name of a folder that this store made: no path in it, so it cannot lead out of the store.
    private static bool Plain(string? dir)
    {
        return !string.IsNullOrEmpty(dir) && dir != "." && dir != ".." && Path.GetFileName(dir) == dir;
    }

    // Letters and digits of the name, for a folder a person may one day look at; never empty.
    private static string Slug(string name)
    {
        var s = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (s.Length > 24)
            s = s[..24].Trim('-');

        return s.Length == 0 ? "ext" : s;
    }

    // ---- the list ----

    // Called with the lock held. An unreadable list is the same as none: the extensions are still
    // in the profile, only their details are missing.
    private MapData Load()
    {
        if (_map is not null)
            return _map;

        var map = new MapData();
        var file = Path.Combine(_root, MapFile);

        try
        {
            if (File.Exists(file))
            {
                var read = JsonSerializer.Deserialize<MapData>(File.ReadAllText(file), Json);
                if (read is not null)
                {
                    foreach (var (id, dir) in read.Dirs ?? new())
                    {
                        if (Plain(dir))
                            map.Dirs[id] = dir;
                    }

                    map.Gone = (read.Gone ?? new()).Where(d => Plain(d)).ToList();

                    // Version 1 has no store ids: the list is simply empty then. A store id that is not
                    // one, or that belongs to an extension that has no folder on the list, is dropped.
                    foreach (var (id, store) in read.Stores ?? new())
                    {
                        if (Cws.IsId(store) && map.Dirs.ContainsKey(id))
                            map.Stores[id] = store;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return _map = map;
    }

    // Called with the lock held. Written to a temporary file first, so a crash cannot leave half a list.
    private void Save(MapData map)
    {
        Directory.CreateDirectory(_root);

        var file = Path.Combine(_root, MapFile);
        var temp = file + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(map, Json));
        File.Move(temp, file, true);
    }
}
