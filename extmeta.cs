using System.Text.Json;

namespace neat;

/// <summary>
/// What an unpacked extension's manifest.json says about it: its name, version, description and,
/// in plain words, what it is allowed to do. WebView2 reports none of this except the name, so it
/// is read from the extension's folder (see <see cref="ExtStore"/>). Nothing here depends on
/// WinUI or WebView2, so tests/ext.check runs it as it is.
///
/// The folder may come from anywhere, so nothing in the manifest is trusted: the file size is
/// capped, a bad file is an error message rather than an exception, and the locale that picks a
/// translation is checked before it becomes part of a path.
/// </summary>
internal sealed class ExtMeta
{
    // A manifest is a few kilobytes; anything past this is not one.
    private const long Max = 1024 * 1024;

    // An icon is a few kilobytes too. The biggest one that is allowed is read into memory to be shown.
    private const long MaxIcon = 1024 * 1024;

    // The widest or tallest an icon may be, in pixels (see Sane).
    private const uint MaxPixels = 1024;

    // The first 8 bytes of every PNG file.
    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly JsonDocumentOptions Lax = new()
    {
        // Chrome accepts comments and trailing commas in manifest.json, and extensions use them.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Permissions that Chrome does not warn about, so neither do we.
    private static readonly HashSet<string> Quiet = new(StringComparer.Ordinal)
    {
        "storage", "unlimitedStorage", "alarms", "activeTab", "contextMenus", "scripting", "idle",
        "offscreen", "sidePanel", "declarativeContent", "fontSettings", "power", "tts", "ttsEngine",
        "search", "favicon", "readingList", "webRequestAuthProvider", "background", "action",
    };

    // The permissions that let an extension see or change a great deal of what the person does, or
    // work behind the person's back. The person is told so in as many words, not just shown the line.
    // (declarativeNetRequest is here because its rules cannot be judged without running them: it may
    // block one ad or every request.) Wide host access counts too: see Everywhere.
    private static readonly HashSet<string> Risky = new(StringComparer.Ordinal)
    {
        "cookies", "webRequest", "webRequestBlocking", "declarativeNetRequest", "tabs", "history",
        "bookmarks", "downloads", "nativeMessaging", "debugger", "management", "proxy",
    };

    // The rest, as a sentence that finishes "It can: ...". Several permissions may say the same thing.
    private static readonly Dictionary<string, string> Says = new(StringComparer.Ordinal)
    {
        ["tabs"] = "See the addresses and titles of your open tabs",
        ["history"] = "Read and change your browsing history",
        ["webNavigation"] = "Read your browsing history",
        ["topSites"] = "See the sites you visit most",
        ["sessions"] = "See your recently closed tabs",
        ["bookmarks"] = "Read and change your bookmarks",
        ["downloads"] = "Manage your downloads",
        ["cookies"] = "Read and change your cookies",
        ["browsingData"] = "Clear your browsing data",
        ["clipboardRead"] = "Read data you copy and paste",
        ["clipboardWrite"] = "Change data you copy and paste",
        ["notifications"] = "Show notifications",
        ["nativeMessaging"] = "Talk to other programs on your PC",
        ["geolocation"] = "Find out where you are",
        ["management"] = "Manage your other extensions",
        ["privacy"] = "Change your privacy settings",
        ["proxy"] = "Control how the browser connects to the internet",
        ["contentSettings"] = "Change what sites are allowed to do, such as cookies and pop-ups",
        ["webRequest"] = "See the requests that pages make",
        ["webRequestBlocking"] = "Block or change the requests that pages make",
        ["declarativeNetRequest"] = "Block content on any page",
        ["declarativeNetRequestWithHostAccess"] = "Block content on the pages it can access",
        ["declarativeNetRequestFeedback"] = "See which requests it blocked",
        ["debugger"] = "Look inside the pages you open, like the developer tools",
        ["tabCapture"] = "Capture what is shown in your tabs",
        ["desktopCapture"] = "Capture what is shown on your screen",
        ["pageCapture"] = "Save pages as files",
        ["userScripts"] = "Run scripts you add on web pages",
        ["identity"] = "Know which accounts you are signed in with",
    };

    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>The manifest format: 2 or 3 (the older Manifest V2, or the current V3).</summary>
    public int Mv { get; init; }

    /// <summary>What the extension is allowed to do, one short sentence each; empty when it asks for nothing notable.</summary>
    public List<string> Can { get; init; } = new();

    /// <summary>The lines of <see cref="Can"/> that are high risk (see Risky), in the same words. Empty when none is.</summary>
    public List<string> Risk { get; init; } = new();

    /// <summary>
    /// The high risk warning in words (what the lines of <see cref="Risk"/> allow, and to trust where the
    /// extension came from), or null when there is none. The Extensions page and the Chrome Web Store
    /// question both say it this way, so it is made here, once.
    /// </summary>
    public string? RiskText => Risk.Count == 0
        ? null
        : "High risk. This extension can:\n" + Bullets(Risk) + "\nOnly add it if you trust where it came from.";

    /// <summary>
    /// What it can do besides that, as "It can:" (or "It can also:" after the warning) and the lines;
    /// or, for an extension that asks for nothing notable, a line that says so. Each line is said once:
    /// the high risk ones are in <see cref="RiskText"/>. Null when everything it can do is high risk.
    /// </summary>
    public string? CanText
    {
        get
        {
            var other = Can.Where(c => !Risk.Contains(c)).ToList();

            if (other.Count > 0)
                return (Risk.Count > 0 ? "It can also:\n" : "It can:\n") + Bullets(other);

            return Can.Count == 0 ? "It asks for no special permissions." : null;
        }
    }

    /// <summary>The note for an extension in the older Manifest V2 format; null for the others.</summary>
    public string? LegacyText => Mv == 2
        ? "This extension uses the older Manifest V2 format, which current versions of Chromium may refuse to run."
        : null;

    private static string Bullets(IEnumerable<string> lines)
    {
        return string.Join("\n", lines.Select(c => "• " + c));
    }

    /// <summary>
    /// The extension's best icon as a path inside its folder (with / between the parts), or null when it
    /// has none that can be used. The file has been checked: it is inside the folder, a PNG and not big.
    /// Join it to the folder to read it.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Reads manifest.json in a folder. Returns the facts, or null and a sentence for the person
    /// when the folder is not an extension.
    /// </summary>
    public static (ExtMeta? Meta, string? Err) Read(string folder)
    {
        var file = Path.Combine(folder, "manifest.json");

        try
        {
            if (!File.Exists(file))
                return (null, "That folder has no manifest.json. Pick the folder that holds it, not one around it.");

            if (new FileInfo(file).Length > Max)
                return (null, "manifest.json is too big to be an extension's.");

            using var doc = JsonDocument.Parse(File.ReadAllText(file), Lax);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return (null, "manifest.json could not be read.");

            var name = Str(root, "name");
            var version = Str(root, "version");
            var mv = root.TryGetProperty("manifest_version", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var n) ? n : 0;

            if (name.Length == 0 || version.Length == 0 || mv == 0)
                return (null, "manifest.json is missing the name, the version or the manifest version, so it is not an extension.");

            var (can, risk) = Wants(root);

            return (new ExtMeta
            {
                Name = Say(folder, root, name),
                Version = version,
                Description = Say(folder, root, Str(root, "description")),
                Mv = mv,
                Can = can,
                Risk = risk,
                Icon = BestIcon(folder, root),
            }, null);
        }
        catch (JsonException)
        {
            return (null, "manifest.json could not be read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "The folder could not be read.");
        }
    }

    /// <summary>
    /// The largest usable entry of the manifest's "icons"; failing that, the toolbar icon
    /// (action.default_icon, then browser_action.default_icon), again the largest. An entry that cannot be
    /// used (see <see cref="Usable"/>) is skipped, not an error: the next one is tried.
    /// </summary>
    private static string? BestIcon(string folder, JsonElement root)
    {
        try
        {
            foreach (var key in new[] { "icons", "action", "browser_action" })
            {
                if (!root.TryGetProperty(key, out var holder) || holder.ValueKind != JsonValueKind.Object)
                    continue;

                var list = key == "icons" ? holder : holder.TryGetProperty("default_icon", out var d) ? d : default;

                foreach (var path in Sized(list))
                {
                    if (Usable(folder, path) is { } found)
                        return found;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An icon is a nicety: no icon, not a failed install.
        }

        return null;
    }

    // The paths of an icon list, the biggest size first. The list is an object of size to path
    // ({"16": "a.png", "128": "b.png"}), or, for default_icon, one path.
    private static IEnumerable<string> Sized(JsonElement list)
    {
        if (list.ValueKind == JsonValueKind.String)
        {
            if (list.GetString() is { Length: > 0 } one)
                yield return one;

            yield break;
        }

        if (list.ValueKind != JsonValueKind.Object)
            yield break;

        var items = new List<(int Size, string Path)>();

        foreach (var p in list.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 } path)
                continue;

            // A key that is not a number is the least wanted, not an error.
            items.Add((int.TryParse(p.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : 0, path));
        }

        foreach (var item in items.OrderByDescending(i => i.Size))
            yield return item.Path;
    }

    /// <summary>
    /// The path of an icon as it will be used (cleaned, with / between the parts), or null when it must
    /// not be: it has to stay inside the folder (no "..", no drive, no way out of it), be a .png file that
    /// starts like a PNG, and not be big. The manifest comes from a stranger, so the path is not taken as it is.
    /// </summary>
    internal static string? Usable(string folder, string path)
    {
        // Chrome reads "/a.png" as a file of the extension, and either kind of slash.
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !parts[^1].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var part in parts)
        {
            // ".." goes out; "." does nothing; ":" is a drive or a stream name.
            if (part is "." or ".." || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
        }

        var clean = string.Join('/', parts);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, clean));

        // The same check again on the finished path, in case the parts above missed a way out.
        if (!full.StartsWith(root, StringComparison.Ordinal))
            return null;

        var info = new FileInfo(full);
        if (!info.Exists || info.Length is 0 or > MaxIcon || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;

        var head = new byte[PngMagic.Length];
        using var s = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);

        return s.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(PngMagic) ? clean : null;
    }

    /// <summary>
    /// True when a PNG that was read from a package is small in pixels as well as in bytes. A few kilobytes of
    /// PNG can describe a picture of a hundred million pixels, and the store's question draws the icon before the
    /// person has said anything, so the size is read from the header (the first chunk, IHDR) and judged first.
    /// Chrome's own icons are 128 pixels or less; the limit leaves room for the odd bigger one.
    /// </summary>
    internal static bool Sane(ReadOnlySpan<byte> png)
    {
        // 8 bytes of signature, then the chunk: 4 of length, "IHDR", 4 of width, 4 of height (big-endian).
        if (png.Length < 24 || !png[..PngMagic.Length].SequenceEqual(PngMagic))
            return false;

        if (!png.Slice(12, 4).SequenceEqual("IHDR"u8))
            return false;

        var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png[20..]);

        return w is > 0 and <= MaxPixels && h is > 0 and <= MaxPixels;
    }

    private static string Str(JsonElement obj, string key)
    {
        return obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
    }

    /// <summary>
    /// Turns "__MSG_key__" into the text from the extension's _locales folder, as Chrome does.
    /// Anything else is returned as it is; so is a placeholder that cannot be found.
    /// </summary>
    private static string Say(string folder, JsonElement root, string text)
    {
        if (text.Length < 8 || !text.StartsWith("__MSG_", StringComparison.Ordinal) || !text.EndsWith("__", StringComparison.Ordinal))
            return text;

        var key = text[6..^2];

        foreach (var locale in Locales(root))
        {
            var file = Path.Combine(folder, "_locales", locale, "messages.json");

            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > Max)
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(file), Lax);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                // Chrome compares the keys without regard to case.
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)
                        && p.Value.ValueKind == JsonValueKind.Object
                        && Str(p.Value, "message") is { Length: > 0 } message)
                        return message;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken translation file: try the next language.
            }
        }

        return text;
    }

    // The languages to look in, best first. The manifest names the default one; it ends up in a
    // path, so only plain locale names (en, pt_BR, zh-CN) are used and "..\x" is not one.
    private static IEnumerable<string> Locales(JsonElement root)
    {
        var def = Str(root, "default_locale");
        if (def.Length > 0 && def.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            yield return def;

        yield return "en";
        yield return "en_US";
    }

    /// <summary>
    /// What the manifest asks for, as sentences: the sites first, then the other permissions. The
    /// second list holds the ones among them that are high risk.
    /// </summary>
    private static (List<string> Can, List<string> Risk) Wants(JsonElement root)
    {
        var apis = new List<string>();
        var hosts = new List<string>();

        Sort(root, "permissions", apis, hosts);
        Sort(root, "host_permissions", apis, hosts);

        // A content script runs on the pages it matches, which is the same as asking for them.
        if (root.TryGetProperty("content_scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in scripts.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.Object)
                    Sort(s, "matches", apis, hosts);
            }
        }

        var can = new List<string>();
        var risk = new List<string>();

        var sites = Sites(hosts);
        if (sites is not null)
        {
            can.Add(sites);

            // Every site is the same as being able to read everything the person does on the web.
            if (hosts.Any(Everywhere))
                risk.Add(sites);
        }

        // Two permissions can say the same thing; it is shown once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var api in apis)
        {
            if (Quiet.Contains(api))
                continue;

            var line = Says.TryGetValue(api, out var said) ? said : "Use the \"" + api + "\" permission";
            if (seen.Add(line))
                can.Add(line);

            // Judged on the permission, not on whether its line was new: two may say the same thing.
            if (Risky.Contains(api) && !risk.Contains(line))
                risk.Add(line);
        }

        return (can, risk);
    }

    // A list of strings is split into site patterns (they have a scheme) and API permissions.
    private static void Sort(JsonElement obj, string key, List<string> apis, List<string> hosts)
    {
        if (!obj.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in list.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(text))
                continue;

            if (text == "<all_urls>" || text.Contains("://", StringComparison.Ordinal))
                hosts.Add(text);
            else
                apis.Add(text);
        }
    }

    // These patterns match every site.
    private static bool Everywhere(string p)
    {
        return p is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*" or "*://*/" or "http://*/" or "https://*/";
    }

    private static string? Sites(List<string> patterns)
    {
        if (patterns.Count == 0)
            return null;

        var names = new List<string>();

        foreach (var p in patterns)
        {
            if (Everywhere(p))
                return "Read and change all your data on all websites";

            var at = p.IndexOf("://", StringComparison.Ordinal);
            var rest = p[(at + 3)..];
            var host = rest.Split('/')[0];

            // A pattern for files on this PC has no host.
            var name = p.StartsWith("file:", StringComparison.Ordinal) ? "local files" : host;
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        if (names.Count == 0)
            return null;

        return names.Count <= 3
            ? "Read and change your data on " + string.Join(", ", names)
            : "Read and change your data on " + names.Count + " websites";
    }
}
