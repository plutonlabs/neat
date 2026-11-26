namespace neat;

/// <summary>
/// The page script of the Chrome Web Store (host side: win.cws.cs). It is added to every tab like the
/// save-password script (win.pass.cs) and does nothing unless the page is the store, in the top frame.
/// tests/cws.check runs it against a made-up store page in jsdom.
///
/// What it does on an extension's page (/detail/name/id): when the person clicks the store's own install
/// button (a real click, not one made by a script), it stops the store's install, which does not work in
/// NEAT, and tells the window which extension was asked for. The window asks the question.
///
/// It changes nothing on the page: the button keeps the store's words, and no element is watched. The
/// button is judged at the moment of the click, starting from the button that was clicked, because the
/// store changes pages without loading them and can keep a page that was left in the document, hidden:
/// the first &lt;main&gt; is not always the one on show.
///
/// What it does not do: build a button of its own, read anything from the page but the address, or decide
/// anything. The extension's id comes from the address only. Everything is checked again by the window,
/// because a script of the page can post the same messages; the question the window asks the person is the
/// real protection.
///
/// Messages are strings in the manner of the other scripts: the prefix says whose they are and carries the
/// window's token, then JSON. To the window only: {kind: 'cws-loaded'} once, when the script is in place on
/// the store (so the log can say whether it ran), and {kind: 'cws-install', id}. The token is not a secret
/// (the page can read it); it keeps the scripts from mistaking each other's messages.
/// </summary>
internal static class CwsScript
{
    /// <summary>The prefix of the messages, once the token is known.</summary>
    public static string Prefix(string token) => "neat-cws:" + token + ":";

    /// <summary>The script added to every page, with the session token filled in.</summary>
    public static string For(string token) => Source.Replace("__TOKEN__", token);

    public const string Source = """
        (() => {
          'use strict';
          // The store, in the top page. Nothing else gets even a listener.
          if (window.top !== window || location.origin !== 'https://chromewebstore.google.com') return;
          const w = window.chrome && window.chrome.webview;
          if (!w) return;

          const TOKEN = '__TOKEN__';
          const PREFIX = 'neat-cws:' + TOKEN + ':';

          // The label of the store's install button. The only place that names it: the classes around it are
          // made up by a tool and change, and its words change with the language, so neither is used.
          const LABEL = 'span[jsname="V67aGc"]';

          // An extension's page: /detail/<name>/<32 letters a to p>. The id is read from here and nowhere else.
          const DETAIL = /^\/detail\/[^/]+\/([a-p]{32})(\/|$)/;

          const post = o => {
            try { w.postMessage(PREFIX + JSON.stringify(o)); } catch (e) { /* nobody to tell */ }
          };

          // The install button of a <main>: the first button on show in it that has a label of that kind. One
          // that is hidden is passed over.
          const installButton = main => {
            for (const label of main.querySelectorAll(LABEL)) {
              const button = label.closest('button');
              if (button && main.contains(button) && button.getClientRects().length > 0) return button;
            }
            return null;
          };

          // The click. It is looked at in the capture phase of the window, before the store's own handlers. Only a
          // real click (the person's) on the install button is taken; every other click is left alone.
          window.addEventListener('click', ev => {
            if (!ev.isTrusted) return;
            const m = DETAIL.exec(location.pathname);
            if (!m) return;

            const path = typeof ev.composedPath === 'function' ? ev.composedPath() : [ev.target];
            const button = path.find(n => n && n.tagName === 'BUTTON');
            const main = button && button.closest('main');
            if (!main || installButton(main) !== button) return;

            // The store's own install must not start.
            ev.preventDefault();
            ev.stopImmediatePropagation();
            post({ kind: 'cws-install', id: m[1] });
          }, true);

          post({ kind: 'cws-loaded' });
        })();
        """;
}
