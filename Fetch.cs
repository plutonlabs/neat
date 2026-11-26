using System.Net;
using neat;

// The downloader of the Chrome Web Store package (src/neat/web/cws.cs), against a fake HTTP handler: no network.
// What each check proves is one thing a hostile or broken server could do, and that it comes out as a sentence,
// with the file gone.

internal static partial class Program
{
    private static void FetchChecks()
    {
        Run("a download is saved, with the address and the headers the store expects", FetchWorks);
        Run("the version in the address is only digits and dots", FetchVersion);
        Run("redirects are followed by hand, up to five", FetchRedirects);
        Run("more than five redirects is refused", FetchLoop);
        Run("a hop that is not https, not Google's, or not a plain address is refused", FetchHops);
        Run("a size over the cap is refused by the header and while it is being copied", FetchSize);
        Run("a download that takes too long is stopped, even when it trickles", FetchSlow);
        Run("a download that is cancelled stops, and leaves nothing", FetchCancel);
        Run("what the store says for an id it does not have, and for an error, is a sentence", FetchStatus);
        Run("a network failure is a sentence that does not quote the address", FetchNetwork);
        Run("something that is not an id never reaches the network", FetchNotAnId);
        Run("a place that cannot be written to is a sentence", FetchDisk);
        Run("the one client follows no redirect, keeps no cookies and sends no credentials", FetchClient);
        Run("the page's address is the store's, https and plain, and the id is read from it", FetchPage);
        Run("a name from a manifest is made fit to show", FetchTidy);
    }

    private const string StoreId = "abcdefghijklmnopabcdefghijklmnop";

    private const string Good = "https://clients2.google.com/service/update2/crx";

    // A handler that answers by a function. Seen holds every request it was given.
    private sealed class Net : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        public Net(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public Net(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((r, ct) => Task.FromResult(answer(r)))
        {
        }

        public List<HttpRequestMessage> Seen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return _answer(request, ct);
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Go(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    // A body with no length in its header (the kind a server streams), read as it comes: what the downloader reads is
    // what this stream gives, a piece at a time, so a check can have it give a lot, give it slowly, or break off.
    private sealed class Raw : HttpContent
    {
        private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>> _read;

        public Raw(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) => _read = read;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Pull(_read));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class Pull : Stream
        {
            private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>> _read;

            public Pull(Func<Memory<byte>, CancellationToken, ValueTask<int>> read) => _read = read;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _read(buffer, ct);

            public override int Read(byte[] buffer, int offset, int count) => _read(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // Runs the downloader the way the window does, on a target in a folder of its own. Gives the sentence, and the target.
    private static async Task<(string? Err, string Target)> Fetched(Net fake, long cap = 1 << 20, TimeSpan? limit = null, string baseUrl = Good, CancellationToken ct = default, string version = "157.0.4341.42", string id = StoreId)
    {
        var target = Path.Combine(Temp(), "pack.crx");
        var err = await Cws.Fetch(Cws.Client(fake), baseUrl, id, version, target, cap, limit ?? TimeSpan.FromSeconds(20), ct);
        return (err, target);
    }

    private static string Query(HttpRequestMessage r) => r.RequestUri!.Query;

    // ---- the checks ----

    private static async Task FetchWorks()
    {
        var body = "Cr24 and then what the server sends"u8.ToArray();
        var fake = new Net(_ => Ok(body));

        var (err, target) = await Fetched(fake);
        Need(err is null, "should work: " + err);
        Need(File.ReadAllBytes(target).SequenceEqual(body), "the file holds what the server sent");
        Need(fake.Seen.Count == 1 && fake.Seen[0].Method == HttpMethod.Get, "one GET");

        var uri = fake.Seen[0].RequestUri!;
        Need(uri.Scheme == "https" && uri.Host == "clients2.google.com" && uri.AbsolutePath == "/service/update2/crx", "the address is the update service's: " + uri);
        Need(Query(fake.Seen[0]) == "?response=redirect&acceptformat=crx2,crx3&prodversion=157.0.4341.42&x=id%3D" + StoreId + "%26uc", "the query is made in one place: " + Query(fake.Seen[0]));

        // Nothing of the person goes along: no cookie, no credentials, nothing of the page.
        var h = fake.Seen[0].Headers;
        Need(!h.Contains("Cookie") && !h.Contains("Authorization") && !h.Contains("Referer") && h.Authorization is null, "no cookie, no credentials, no referrer");
    }

    private static async Task FetchVersion()
    {
        // What the engine says is cut down to digits and dots; whatever else it says is not put in the address.
        var cases = new (string Said, string Sent)[]
        {
            ("157.0.4341.42", "157.0.4341.42"),
            ("157.0.4341.42 canary", "157.0.4341.42"),
            ("157.0", "157.0"),
            ("", "157.0.0.0"),
            ("canary", "157.0.0.0"),
            ("1.2.3.4.5", "157.0.0.0"),
            ("1234567890.1", "157.0.0.0"),
            ("1&x=id%3Devil", "1"),
            ("../../etc", "157.0.0.0"),
        };

        foreach (var (said, sent) in cases)
        {
            var fake = new Net(_ => Ok(new byte[] { 1 }));
            await Fetched(fake, version: said);

            var q = fake.Seen.Count == 1 ? Query(fake.Seen[0]) : "(no request)";
            Need(q.Contains("&prodversion=" + sent + "&x=id%3D"), $"\"{said}\" should be sent as {sent}: {q}");
            Need(!q.Contains("evil") && q.Count(c => c == '&') == 3, $"\"{said}\" must not add to the address: {q}");
        }
    }

    private static async Task FetchRedirects()
    {
        // The store sends the request on to the place the file is served from, as it does: two hops, then the file.
        var body = new byte[] { 1, 2, 3 };
        var fake = new Net(r => r.RequestUri!.Host switch
        {
            "clients2.google.com" => Go("https://clients2.googleusercontent.com/crx/blobs/abc/file.crx"),
            "clients2.googleusercontent.com" when r.RequestUri.AbsolutePath != "/crx/real.crx" => Go("/crx/real.crx"),
            _ => Ok(body),
        });

        var (err, target) = await Fetched(fake);
        Need(err is null, "two hops should work: " + err);
        Need(File.ReadAllBytes(target).SequenceEqual(body), "and the file is the last answer");
        Need(fake.Seen.Count == 3, "three requests: " + fake.Seen.Count);
        Need(fake.Seen[2].RequestUri!.AbsolutePath == "/crx/real.crx" && fake.Seen[2].RequestUri!.Host == "clients2.googleusercontent.com", "a relative address is taken from the hop it came from");

        // Exactly five redirects is still allowed.
        var count = 0;
        var five = new Net(_ => ++count <= Cws.MaxRedirects ? Go("https://dl.google.com/next" + count) : Ok(body));
        var (e5, _) = await Fetched(five);
        Need(e5 is null, "five redirects should work: " + e5);

        // Every status that is a redirect, and a redirect with nowhere to go.
        foreach (var code in new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
        {
            var asked = 0;
            var one = new Net(_ => asked++ == 0 ? Go("https://dl.google.com/x", code) : Ok(body));
            var (ec, _) = await Fetched(one);
            Need(ec is null, code + " should be followed: " + ec);
        }

        var nowhere = new Net(_ => new HttpResponseMessage(HttpStatusCode.Found));
        var (en, tn) = await Fetched(nowhere);
        Need(en == Cws.Failed && !File.Exists(tn), "a redirect without an address fails");
    }

    private static async Task FetchLoop()
    {
        var fake = new Net(_ => Go("https://dl.google.com/again"));
        var (err, target) = await Fetched(fake);

        Need(err == Cws.Loop, "a loop is refused: " + err);
        Need(fake.Seen.Count == Cws.MaxRedirects + 1, "after five hops and no more: " + fake.Seen.Count);
        Need(!File.Exists(target), "and leaves nothing");
    }

    private static async Task FetchHops()
    {
        var hops = new[]
        {
            "http://clients2.google.com/crx",                 // not https
            "https://evil.example/crx",                       // not Google's
            "https://google.com.evil.example/crx",            // Google's name at the front
            "https://notgoogle.com/crx",                      // ends like it, is not under it
            "https://evilgoogleusercontent.com/crx",
            "https://142.250.1.1/crx",                        // a number, not a name
            "https://[2a00:1450:4001::2]/crx",
            "https://user:pass@dl.google.com/crx",            // a user name in the address
            "https://dl.google.com:8443/crx",                 // not the usual port
            "ftp://dl.google.com/crx",
            "file:///C:/Windows/win.ini",
            "//evil.example/crx",                             // an address that borrows the scheme
        };

        foreach (var hop in hops)
        {
            var fake = new Net(r => r.RequestUri!.Host == "clients2.google.com" ? Go(hop) : Ok(new byte[] { 1 }));
            var (err, target) = await Fetched(fake);

            Need(err == Cws.Untrusted, hop + " should be refused: " + err);
            Need(fake.Seen.Count == 1, hop + " must not be asked: " + fake.Seen.Count + " requests");
            Need(!File.Exists(target), hop + " leaves nothing");
        }

        // A first address that is not Google's is refused before a request is made.
        foreach (var start in new[] { "http://clients2.google.com/crx", "https://evil.example/crx" })
        {
            var fake = new Net(_ => Ok(new byte[] { 1 }));
            var (err, _) = await Fetched(fake, baseUrl: start);
            Need(err == Cws.Untrusted && fake.Seen.Count == 0, start + " is not a place to start from");
        }

        // Google's names, in any case, and with a dot at the end, are fine; so is a name under them.
        foreach (var ok in new[] { "https://DL.GOOGLE.COM/crx", "https://dl.google.com./crx", "https://a.b.googleusercontent.com/crx", "https://google.com/crx" })
        {
            var fake = new Net(r => r.RequestUri!.Host == "clients2.google.com" ? Go(ok) : Ok(new byte[] { 1 }));
            var (err, _) = await Fetched(fake);
            Need(err is null, ok + " should be allowed: " + err);
        }
    }

    private static async Task FetchSize()
    {
        // By the header: refused before a byte is read.
        var big = new Net(_ => Ok(new byte[5000]));
        var (e1, t1) = await Fetched(big, cap: 1000);
        Need(e1 == Cws.Large && !File.Exists(t1), "the header says too much: " + e1);

        // A header that says less than what comes.
        var lying = new Net(_ =>
        {
            var r = Ok(new byte[5000]);
            r.Content.Headers.ContentLength = 10;
            return r;
        });
        var (e2, t2) = await Fetched(lying, cap: 1000);
        Need(e2 == Cws.Large && !File.Exists(t2), "the header lies: " + e2);

        // No length at all: only counting stops it, and it must stop soon after the cap, not at the end of an endless body.
        long given = 0;
        var endless = new Net(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new Raw((buffer, ct) =>
            {
                buffer.Span.Fill(7);
                given += buffer.Length;
                return ValueTask.FromResult(buffer.Length);
            }),
        });
        var (e3, t3) = await Fetched(endless, cap: 10_000);
        Need(e3 == Cws.Large && !File.Exists(t3), "no length, too much: " + e3);
        Need(given < 10_000 + 2 * 81920, "and stopped soon after the cap: " + given + " bytes were read");

        // Exactly the cap is allowed.
        var exact = new Net(_ => Ok(new byte[1000]));
        var (e4, t4) = await Fetched(exact, cap: 1000);
        Need(e4 is null && new FileInfo(t4).Length == 1000, "exactly the cap is fine: " + e4);
    }

    private static async Task FetchSlow()
    {
        // No answer at all.
        var silent = new Net(async (r, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(Array.Empty<byte>());
        });
        var (e1, t1) = await Fetched(silent, limit: TimeSpan.FromMilliseconds(300));
        Need(e1 == Cws.Slow && !File.Exists(t1), "no answer: " + e1);

        // The headers come at once and the body trickles: HttpClient.Timeout would not stop this one.
        var trickle = new Net(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new Raw(async (buffer, ct) =>
            {
                await Task.Delay(50, ct);
                buffer.Span[0] = 1;
                return 1;
            }),
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (e2, t2) = await Fetched(trickle, limit: TimeSpan.FromMilliseconds(400));
        watch.Stop();
        Need(e2 == Cws.Slow && !File.Exists(t2), "a body that trickles: " + e2);
        Need(watch.Elapsed < TimeSpan.FromSeconds(10), "and is stopped in time: " + watch.Elapsed);
    }

    private static async Task FetchCancel()
    {
        using var stop = new CancellationTokenSource();
        var fake = new Net(async (r, ct) =>
        {
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(Array.Empty<byte>());
        });

        var (err, target) = await Fetched(fake, ct: stop.Token);
        Need(err == Cws.Stopped, "a cancel is not a timeout: " + err);
        Need(!File.Exists(target), "and leaves nothing");

        // Cancelled before it starts.
        using var done = new CancellationTokenSource();
        done.Cancel();
        var (e2, t2) = await Fetched(new Net(_ => Ok(new byte[] { 1 })), ct: done.Token);
        Need(e2 == Cws.Stopped && !File.Exists(t2), "already cancelled: " + e2);
    }

    private static async Task FetchStatus()
    {
        foreach (var code in new[] { HttpStatusCode.NotFound, HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.Gone })
        {
            var (err, target) = await Fetched(new Net(_ => new HttpResponseMessage(code)));
            Need(err == Cws.Missing && !File.Exists(target), (int)code + " is an extension that is not there: " + err);
        }

        foreach (var code in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            var (err, target) = await Fetched(new Net(_ => new HttpResponseMessage(code) { Content = new StringContent("secret detail of the server") }));
            Need(err is not null && err.Contains("error " + (int)code) && !err.Contains("secret"), (int)code + " is a sentence with the number only: " + err);
            Need(!File.Exists(target), (int)code + " leaves nothing");
        }

        // An empty 200 is not an error here (the signature check says what it is); the file is made.
        var (e, t) = await Fetched(new Net(_ => Ok(Array.Empty<byte>())));
        Need(e is null && File.Exists(t), "an empty answer is left to the next check");
    }

    private static async Task FetchNetwork()
    {
        foreach (Exception boom in new Exception[]
        {
            new HttpRequestException("no route to https://clients2.google.com/service/update2/crx?x=id%3Dabc"),
            new IOException("the connection was reset by https://secret.example/"),
        })
        {
            var (err, target) = await Fetched(new Net((r, ct) => throw boom));
            Need(err == Cws.Offline, boom.GetType().Name + " is the sentence for no network: " + err);
            Need(!err!.Contains("google") && !err.Contains("secret") && !File.Exists(target), "it does not quote the address, and leaves nothing");
        }

        // Anything else that goes wrong is a sentence too, and the program goes on.
        var (e2, t2) = await Fetched(new Net((r, ct) => throw new InvalidOperationException("odd")));
        Need(e2 == Cws.Failed && !File.Exists(t2), "an unexpected failure: " + e2);

        // The body breaks off in the middle: the file that was begun (2000 bytes of it) is deleted.
        var sent = 0;
        var cut = new Net(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new Raw((buffer, ct) =>
            {
                if (sent >= 2000)
                    throw new IOException("reset by https://secret.example/");

                var n = Math.Min(buffer.Length, 2000 - sent);
                sent += n;
                return ValueTask.FromResult(n);
            }),
        });
        var (e3, t3) = await Fetched(cut);
        Need(sent == 2000, "the file was begun before the body broke off: " + sent);
        Need(e3 == Cws.Offline && !e3.Contains("secret") && !File.Exists(t3), "a body that breaks off leaves no file: " + e3);
    }

    private static async Task FetchNotAnId()
    {
        foreach (var id in new[] { "", "abc", StoreId + "a", StoreId[..31] + "q", StoreId.ToUpperInvariant(), StoreId[..31] + "1", "../../" + StoreId[..26], StoreId[..30] + "%2f" })
        {
            var fake = new Net(_ => Ok(new byte[] { 1 }));
            var (err, target) = await Fetched(fake, id: id);

            Need(err == Cws.Missing && fake.Seen.Count == 0, "\"" + id + "\" is not an id and is not asked for");
            Need(!File.Exists(target), "and nothing is written");
        }
    }

    private static async Task FetchDisk()
    {
        var fake = new Net(_ => Ok(new byte[] { 1, 2, 3 }));

        // The folder is not there.
        var target = Path.Combine(Temp(), "no-such-folder", "pack.crx");
        var err = await Cws.Fetch(Cws.Client(fake), Good, StoreId, "157.0.1.2", target, 1 << 20, TimeSpan.FromSeconds(10), CancellationToken.None);
        Need(err == Cws.Disk, "no place to write: " + err);

        // The file is already there: it is never written over by a download.
        var taken = Path.Combine(Temp(), "pack.crx");
        File.WriteAllText(taken, "mine");
        var again = await Cws.Fetch(Cws.Client(fake), Good, StoreId, "157.0.1.2", taken, 1 << 20, TimeSpan.FromSeconds(10), CancellationToken.None);
        Need(again == Cws.Disk, "a name that is taken: " + again);
        Need(File.Exists(taken) && File.ReadAllText(taken) == "mine", "and what was there is left as it was");
    }

    private static Task FetchClient()
    {
        // The client the app uses, by reflection: it is private and made on first use. Its handler is what matters.
        var shared = typeof(Cws).GetField("Shared", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Need(shared is not null, "the one client should be a field called Shared");

        var http = ((Lazy<HttpClient>)shared!.GetValue(null)!).Value;
        Need(http.Timeout == Timeout.InfiniteTimeSpan, "HttpClient's own timeout is off: the limit is the downloader's");

        var handler = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(http);
        var sockets = handler as SocketsHttpHandler;
        Need(sockets is not null, "the handler should be a SocketsHttpHandler");
        Need(!sockets!.AllowAutoRedirect, "no redirect is followed by the handler");
        Need(!sockets.UseCookies, "no cookies");
        Need(sockets.Credentials is null && !sockets.PreAuthenticate, "no credentials");
        Need(sockets.AutomaticDecompression == DecompressionMethods.None, "the bytes are the file's, not a decoded form of them");
        return Task.CompletedTask;
    }

    private static Task FetchPage()
    {
        var id = StoreId;

        foreach (var url in new[]
        {
            "https://chromewebstore.google.com/detail/ublock-origin-lite/" + id,
            "https://chromewebstore.google.com/detail/ublock-origin-lite/" + id + "/",
            "https://chromewebstore.google.com/detail/ublock-origin-lite/" + id + "?hl=en&utm_source=x",
            "https://chromewebstore.google.com/detail/ublock-origin-lite/" + id + "/reviews",
            "https://CHROMEWEBSTORE.google.com/detail/x/" + id,
            "https://chromewebstore.google.com:443/detail/x/" + id,
            "https://chromewebstore.google.com/detail/x/%61" + id[1..],   // the address parser reads %61 as a, as the store does
        })
        {
            Need(Cws.IsStoreUrl(url), url + " is a page of the store");
            Need(Cws.PageId(url) == id, url + " says the id");
        }

        foreach (var url in new[]
        {
            "http://chromewebstore.google.com/detail/x/" + id,
            "https://chromewebstore.google.com.evil.example/detail/x/" + id,
            "https://evil.example/chromewebstore.google.com/detail/x/" + id,
            "https://chromewebstore.google.com@evil.example/detail/x/" + id,
            "https://user@chromewebstore.google.com/detail/x/" + id,
            "https://chromewebstore.google.com:8443/detail/x/" + id,
            "https://chrome.google.com/webstore/detail/x/" + id,
            "chromewebstore.google.com/detail/x/" + id,
            "",
            "about:blank",
        })
            Need(Cws.PageId(url) is null && !Cws.IsStoreUrl(url), url + " is not a page of the store");

        // Pages of the store that are not an extension's, and addresses that only look like one.
        foreach (var url in new[]
        {
            "https://chromewebstore.google.com/",
            "https://chromewebstore.google.com/category/extensions",
            "https://chromewebstore.google.com/search/" + id,
            "https://chromewebstore.google.com/detail/" + id,
            "https://chromewebstore.google.com/detail//" + id,
            "https://chromewebstore.google.com/detail/x/" + id[..31],
            "https://chromewebstore.google.com/detail/x/" + id + "a",
            "https://chromewebstore.google.com/detail/x/" + id.ToUpperInvariant(),
            "https://chromewebstore.google.com/other/detail/x/" + id,
            "https://chromewebstore.google.com/Detail/x/" + id,
        })
            Need(Cws.PageId(url) is null, url + " has no extension id to read");

        Need(Cws.IsStoreUrl("https://chromewebstore.google.com/") && !Cws.IsStoreUrl("https://google.com/") && !Cws.IsStoreUrl(null), "the store's host, and no other");
        return Task.CompletedTask;
    }

    private static Task FetchTidy()
    {
        Need(Cws.Tidy("Plain Name", 60) == "Plain Name", "a plain name stays");
        Need(Cws.Tidy("  a \t b\r\nc  ", 60) == "a b c", "white space is one space and none at the ends");
        Need(Cws.Tidy("a\u0000b\u001bc\u007Fd", 60) == "abcd", "control characters are dropped");
        Need(Cws.Tidy("two\nlines", 60) == "two lines", "a line break is a space, so a name cannot make a line of the log");

        // The characters that turn text round, or hide in it, are not shown or logged.
        Need(Cws.Tidy("good\u202Eevil\u202C", 60) == "goodevil", "right-to-left override and pop are dropped");
        Need(Cws.Tidy("a\u200Bb\u2066c\uFEFFd", 60) == "abcd", "zero width and isolates are dropped");

        Need(Cws.Tidy(new string('x', 100), 10) == new string('x', 9) + "\u2026", "a long one is cut and ends in an ellipsis");
        Need(Cws.Tidy("ten chars!!", 11) == "ten chars!!" && Cws.Tidy("ten chars!!!", 11) == "ten chars!\u2026", "at the limit it stays, one over it is cut");
        Need(Cws.Tidy(null, 10) == "" && Cws.Tidy("", 10) == "" && Cws.Tidy("\u202E\n", 10) == "", "nothing is nothing");
        Need(Cws.Tidy("abc", 0) == "\u2026" || Cws.Tidy("abc", 0) == "", "a limit of nothing does not throw");
        return Task.CompletedTask;
    }
}
