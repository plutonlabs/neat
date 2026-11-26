namespace neat;

/// <summary>
/// The page script of the "Add to NEAT" button on the Chrome Web Store (host side: win.cws.cs). It is
/// added to every tab like the save-password script (win.pass.cs) and does nothing unless the page is the
/// store, in the top frame. tests/cws.check runs it against a made-up store page in jsdom.
///
/// What it does on an extension's page (/detail/name/id):
///   - finds the store's own install button, writes "Add to NEAT" on it, and keeps writing it when the store
///     draws the label again (the store is one page that changes without loading, so the page is watched);
///   - when the person clicks that button (a real click, not one made by a script), stops the store's own
///     install, which does not work in NEAT, and tells the window which extension was asked for;
///   - shows what the window answers: "Adding..." while it works, "Added" when the extension is installed.
///
/// What it does not do: build a button of its own (a page without the button is only reported once, so that
/// the person can say so), read anything from the page but the address, or decide anything. The extension's id
/// comes from the address only. Everything is checked again by the window, because a script of the page can
/// post the same messages; the question the window asks the person is the real protection.
///
/// Messages are strings in the manner of the other scripts: the prefix says whose they are and carries the
/// window's token, then JSON. To the window: {kind: 'cws-ready' | 'cws-install', id} and
/// {kind: 'cws-nobutton'}; back: {state: 'add' | 'busy' | 'added', id}. The token is not a secret (the
/// page can read it); it keeps the scripts from mistaking each other's messages.
/// </summary>
internal static class CwsScript
{
    /// <summary>The prefix of the messages in both directions, once the token is known.</summary>
    public static string Prefix(string token) => "neat-cws:" + token + ":";

    /// <summary>How long a page without the button is given to draw it before it is reported (ms).</summary>
    private const int Grace = 6000;

    /// <summary>The script added to every page, with the session token filled in.</summary>
    public static string For(string token)
    {
        return Source.Replace("__TOKEN__", token).Replace("__GRACE__", Grace.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public const string Source = """
        (() => {
          'use strict';
          // The store, in the top page. Nothing else gets even a listener.
          if (window.top !== window || location.origin !== 'https://chromewebstore.google.com') return;
          const w = window.chrome && window.chrome.webview;
          if (!w) return;

          const TOKEN = '__TOKEN__';
          const PREFIX = 'neat-cws:' + TOKEN + ':';
          // How long a page gets to draw its button before the window is told there is none (ms).
          const GRACE = __GRACE__;

          // The label of the store's install button. The only place that names it: the classes around it are
          // made up by a tool and change, and its words change with the language, so neither is used.
          const LABEL = 'span[jsname="V67aGc"]';

          // An extension's page: /detail/<name>/<32 letters a to p>. The id is read from here and nowhere else.
          const DETAIL = /^\/detail\/[^/]+\/([a-p]{32})(\/|$)/;

          const WORDS = { add: 'Add to NEAT', busy: 'Adding...', added: 'Added' };

          let cur = null;        // the id of the page that is shown, or null when it is not an extension's page
          let state = 'add';     // what the window last said about cur
          let queued = false;
          let timer = 0;
          let missing = false;   // the window has been told there is no button (once for this page)
          let asked = 0;         // when the window was last asked about an added extension

          const post = o => {
            try { w.postMessage(PREFIX + JSON.stringify(o)); } catch (e) { /* nobody to tell */ }
          };

          const idOf = () => {
            const m = DETAIL.exec(location.pathname);
            return m ? m[1] : null;
          };

          // The install button: the first label of that kind inside <main>, and the button around it. A
          // button outside <main> (the header of the store, say) is not the one.
          const find = () => {
            const main = document.querySelector('main');
            if (!main) return null;
            for (const label of main.querySelectorAll(LABEL)) {
              const button = label.closest('button');
              if (button && main.contains(button)) return { button, label };
            }
            return null;
          };

          // Our own change of the label is not news: it is dropped from what the observer has seen, so
          // that writing the label can never start another round.
          const obs = new MutationObserver(() => later());

          const show = found => {
            const want = WORDS[state];
            if (found.label.textContent !== want) {
              found.label.textContent = want;
              obs.takeRecords();
            }
          };

          const disarm = () => {
            if (timer) { clearTimeout(timer); timer = 0; }
          };

          // A page that still has no button after the grace is reported, once, so that the log says so.
          const arm = () => {
            if (missing || timer) return;
            timer = setTimeout(() => {
              timer = 0;
              if (cur && !missing && !find()) {
                missing = true;
                post({ kind: 'cws-nobutton' });
              }
            }, GRACE);
          };

          // Brings everything in step with the page as it is now. Cheap, and safe to call at any time.
          const scan = () => {
            queued = false;
            const id = idOf();
            if (id !== cur) {
              cur = id;
              state = 'add';
              disarm();
              if (id) post({ kind: 'cws-ready', id });
            }
            if (!cur) return;

            const found = find();
            if (!found) { arm(); return; }
            disarm();
            show(found);
          };

          function later() {
            if (queued) return;
            queued = true;
            setTimeout(scan, 30);
          }

          // The click. It is looked at in the capture phase of the window, before the store's own handlers. Only a
          // real click (the person's) on the button is taken; a click that a script made is left alone.
          window.addEventListener('click', ev => {
            if (!ev.isTrusted) return;
            scan();
            if (!cur) return;

            const found = find();
            if (!found) return;
            const path = typeof ev.composedPath === 'function' ? ev.composedPath() : [ev.target];
            if (!path.includes(found.button)) return;

            // The store's own install must not start, whatever the button says at the moment.
            ev.preventDefault();
            ev.stopImmediatePropagation();

            // One flow at a time: while one runs, or when the extension is in already, the click is only swallowed.
            if (state !== 'add') return;
            state = 'busy';
            show(found);
            post({ kind: 'cws-install', id: cur });
          }, true);

          // What the window says: the state of the button for an extension. An answer for a page that has
          // been left (the store moves on without loading) is not for this one.
          w.addEventListener('message', ev => {
            const d = ev.data;
            if (typeof d !== 'string' || !d.startsWith(PREFIX)) return;
            let m;
            try { m = JSON.parse(d.slice(PREFIX.length)); } catch (e) { return; }
            if (!m || typeof m.id !== 'string' || m.id !== cur) return;
            if (!Object.prototype.hasOwnProperty.call(WORDS, m.state)) return;
            state = m.state;
            const found = find();
            if (found) show(found);
          });

          // The store changes pages without loading them.
          for (const name of ['pushState', 'replaceState']) {
            const orig = history[name];
            if (typeof orig !== 'function') continue;
            history[name] = function () {
              const r = orig.apply(this, arguments);
              later();
              return r;
            };
          }
          window.addEventListener('popstate', later);
          window.addEventListener('hashchange', later);
          obs.observe(document, { childList: true, subtree: true, characterData: true });

          // An extension that was removed in the settings is not "Added" any more once the person is back.
          const again = () => {
            if (!cur || state !== 'added' || Date.now() - asked < 1500) return;
            asked = Date.now();
            post({ kind: 'cws-ready', id: cur });
          };
          document.addEventListener('visibilitychange', () => { if (!document.hidden) again(); });
          window.addEventListener('focus', again);

          later();
        })();
        """;
}
