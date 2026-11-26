using System.Net;

namespace neat;

/// <summary>
/// Downloads an extension's package (.crx) from the Chrome Web Store by its id. Nothing here
/// depends on WinUI or WebView2, so tests/ext.check runs it as it is, against a fake HTTP handler.
///
/// The address is NOT an official API: the store has no documented way to get a .crx by id. This is
/// the update-check address Chrome itself uses; it may change without notice, and when it does the
/// download fails with a sentence and nothing else is affected. It is built in ONE place
/// (<see cref="Url"/>), from an id that was checked (<see cref="IsId"/>), never from anything a page
/// said. Not verified against the live service when this was written: the form of prodversion (the
/// engine's full four-part version is sent; some programs send only "major.0") and the hosts the
/// redirect ends on (<see cref="Trusted"/> is a guess from what Chrome is known to use). If the real
/// store answers with something else, those are the two places to look, and the failure sentence
/// is what the person sees until they are fixed.
///
/// The download is somebody else's bytes, so every step assumes the worst:
///   - Only https, only the default port, no user name in the address, and only the hosts of Google
///     (<see cref="Trusted"/>). Redirects are followed by hand, at most <see cref="MaxRedirects"/>,
///     and every hop is judged like the first one. Only the host name of a refused hop is logged.
///   - No cookies and no credentials are sent.
///   - The size is capped twice: by the Content-Length the server announces and again while copying,
///     because the announced size is only what the server says about itself. The package is streamed
///     into a file; it is never held in memory.
///   - One time limit covers the whole download, not only the wait for the first answer.
///   - A failure is one short English sentence, never the text of an exception or a path, and the
///     file is deleted on every failure.
///
/// What comes out is a file, not a trusted package: <see cref="CrxSig"/> checks it, and ExtStore.Stage
/// unpacks it, before anything is shown to the person.
/// </summary>
internal static class Cws
{
    /// <summary>The page the store is on. Messages from the install button are accepted from this host only.</summary>
    public const string Host = "chromewebstore.google.com";

    /// <summary>The update-check address (see the remarks above).</summary>
    public const string DefaultBase = "https://clients2.google.com/service/update2/crx";

    /// <summary>How many redirects are followed.</summary>
    public const int MaxRedirects = 5;

    // What goes in prodversion when the engine does not say its version in a form that can be used.
    private const string FallbackVersion = "157.0.0.0";

    // The hosts a download may come from, and the hosts it may be sent on to: these names and what is under them.
    private static readonly string[] Trusted = { "google.com", "googleusercontent.com" };

    public const string Offline = "Could not reach the Chrome Web Store.";
    public const string Missing = "That extension was not found.";
    public const string Large = "The download was too large.";
    public const string Slow = "The Chrome Web Store took too long to answer.";
    public const string Stopped = "The download was cancelled.";
    public const string Untrusted = "The download came from an address NEAT does not trust.";
    public const string Loop = "The Chrome Web Store sent NEAT round in circles. Try again later.";
    public const string Disk = "NEAT could not save the download. Check that the disk has room.";
    public const string Failed = "The download failed.";

    // The one client. It never follows a redirect by itself, keeps no cookies and sends no
    // credentials; the time limit is ours (Fetch), because HttpClient.Timeout stops counting once
    // the headers are in, and a body that trickles in would then never end.
    private static readonly Lazy<HttpClient> Shared = new(() => Client(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    }));

    /// <summary>A client over this handler, set up as <see cref="Fetch(string, string, string, long, CancellationToken)"/> expects. For the check.</summary>
    internal static HttpClient Client(HttpMessageHandler handler) => new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>True for a store id: exactly 32 letters from a to p (a hex digit written with the letters a to p).</summary>
    public static bool IsId(string? id)
    {
        if (id is null || id.Length != 32)
            return false;

        foreach (var c in id)
        {
            if (c < 'a' || c > 'p')
                return false;
        }

        return true;
    }

    /// <summary>
    /// True for an ordinary address of the store: https, the store's host, the default port and no user name.
    /// What the window accepts a message from, and the only kind of page whose address is read for an id.
    /// </summary>
    public static bool IsStoreUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttps
            && u.IsDefaultPort
            && u.UserInfo.Length == 0
            && string.Equals(u.IdnHost, Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The extension's id in the address of its page on the store (/detail/&lt;name&gt;/&lt;id&gt;), or null for any
    /// other address. The page script reads the id from the same place the same way; the window checks again
    /// that a message is about the page the tab is on.
    /// </summary>
    public static string? PageId(string? url)
    {
        if (!IsStoreUrl(url))
            return null;

        // The path as the address parser gives it: "%61" is read as an "a" (the store does the same), and an
        // encoded slash stays encoded, so it is never taken for a separator.
        var parts = new Uri(url!).AbsolutePath.Split('/');
        return parts.Length >= 4 && parts[0].Length == 0 && parts[1] == "detail" && parts[2].Length > 0 && IsId(parts[3])
            ? parts[3]
            : null;
    }

    /// <summary>
    /// A name or a line from somebody else's manifest made fit to show and to log: control characters and the
    /// invisible ones that turn text round (Unicode category Cf) are dropped, runs of white space become one
    /// space, and what is longer than max letters is cut and ends in an ellipsis.
    /// </summary>
    public static string Tidy(string? text, int max)
    {
        var sb = new System.Text.StringBuilder();
        var space = false;

        foreach (var c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                continue;

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        var s = sb.ToString();
        return s.Length <= max ? s : s[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The download address for an extension. x is the encoded form of "id=&lt;id&gt;&amp;uc". The version is
    /// the engine's own (what Chrome would say), cut down to digits and dots so that nothing odd can end up in the address.
    /// </summary>
    internal static string Url(string baseUrl, string id, string version)
    {
        return baseUrl + "?response=redirect&acceptformat=crx2,crx3&prodversion=" + Version(version) + "&x=id%3D" + id + "%26uc";
    }

    // "157.0.4341.42" stays as it is; "157.0.4341.42 canary" loses the word; nothing usable gives the fallback.
    private static string Version(string? v)
    {
        var s = (v ?? string.Empty).Trim();
        var end = 0;

        while (end < s.Length && (char.IsAsciiDigit(s[end]) || s[end] == '.'))
            end++;

        var parts = s[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 4 || parts.Any(p => p.Length > 9))
            return FallbackVersion;

        return string.Join('.', parts);
    }

    /// <summary>
    /// Downloads the package for a store id into <paramref name="target"/>, a file that must not exist yet
    /// (its name should end in .crx: that is how the unpacker knows what it is). Returns null when it worked,
    /// or a sentence for the person; then the file is gone again. Never throws.
    /// </summary>
    public static Task<string?> Fetch(string id, string version, string target, long maxBytes, CancellationToken ct)
    {
        return Fetch(Shared.Value, DefaultBase, id, version, target, maxBytes, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>The same, with the client, the base address and the time limit given. For the check.</summary>
    internal static async Task<string?> Fetch(HttpClient http, string baseUrl, string id, string version, string target, long maxBytes, TimeSpan limit, CancellationToken ct)
    {
        // Never reaches the network with anything that is not an id.
        if (!IsId(id))
            return Missing;

        string? err;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        try
        {
            err = await Go(http, new Uri(Url(baseUrl, id, version)), target, maxBytes, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ours (the person closed the tab) or the clock's.
            err = ct.IsCancellationRequested ? Stopped : Slow;
        }
        catch (Full full)
        {
            // A file that was there before the download is not ours to delete.
            if (!full.Made)
                return Disk;

            err = Disk;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The kind only: the message of a network error may hold the address.
            Log.Write("ext", "a download failed (" + ex.GetType().Name + ")");
            err = Offline;
        }
        catch (Exception ex)
        {
            Log.Write("ext", "a download failed (" + ex.GetType().Name + ")");
            err = Failed;
        }

        if (err is not null)
            Delete(target);

        return err;
    }

    // The disk said no (as opposed to the network). Made is true when the file had been created by then.
    private sealed class Full : Exception
    {
        public Full(bool made) => Made = made;

        public bool Made { get; }
    }

    private static async Task<string?> Go(HttpClient http, Uri uri, string target, long maxBytes, CancellationToken ct)
    {
        var moved = 0;

        while (true)
        {
            if (!Trusts(uri))
            {
                // Only the host name: the rest of an address may hold anything.
                Log.Write("ext", "a download hop was refused, host " + Shown(uri));
                return Untrusted;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;

            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var to = resp.Headers.Location;
                if (to is null)
                    return Failed;

                if (++moved > MaxRedirects)
                    return Loop;

                uri = to.IsAbsoluteUri ? to : new Uri(uri, to);
                continue;
            }

            // The store answers an id it does not have in one of these ways.
            if (code is 204 or 400 or 404 or 410)
                return Missing;

            if (!resp.IsSuccessStatusCode)
                return "The Chrome Web Store could not give that extension (error " + code + ").";

            return await Save(resp, target, maxBytes, ct);
        }
    }

    private static async Task<string?> Save(HttpResponseMessage resp, string target, long maxBytes, CancellationToken ct)
    {
        if (resp.Content.Headers.ContentLength > maxBytes)
            return Large;

        await using var from = await resp.Content.ReadAsStreamAsync(ct);

        FileStream to;
        try
        {
            // CreateNew: never over a file that is already there.
            to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Full(false);
        }

        await using (to)
        {
            var buf = new byte[81920];
            long total = 0;
            int n;

            while ((n = await from.ReadAsync(buf, ct)) > 0)
            {
                // Counted as it arrives: the length in the header is only a promise.
                total += n;
                if (total > maxBytes)
                    return Large;

                try
                {
                    await to.WriteAsync(buf.AsMemory(0, n), ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new Full(true);
                }
            }
        }

        return null;
    }

    // https on its own port, no user name, a name (not a number) under one of the trusted names.
    private static bool Trusts(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length > 0 || u.HostNameType != UriHostNameType.Dns)
            return false;

        var host = u.IdnHost.TrimEnd('.').ToLowerInvariant();
        return Trusted.Any(t => host == t || host.EndsWith("." + t, StringComparison.Ordinal));
    }

    private static string Shown(Uri u)
    {
        var host = u.HostNameType == UriHostNameType.Dns ? u.IdnHost : "(not a name)";
        return host.Length > 100 ? host[..100] : host;
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is in the downloads folder of the store, which is swept at the next start (see ExtStore).
        }
    }
}
