'use strict';
// Runs the page script of the Chrome Web Store install button (src/neat/web/cwsjs.cs) against a made-up
// store page, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.
//
// Two things about how it is run:
//   - jsdom marks every script-made event as not trusted, so a "real" click is made the way
//     tests/capture.check does it: a wrapper reports isTrusted = true for the duration of one dispatch
//     (the script itself is not changed for the test). A click made by the page's own script is
//     dispatched without the wrapper, which is how the "ignored" case is checked.
//   - jsdom lays nothing out, so every element has no box. getClientRects is replaced: an element has a
//     box unless it, or something around it, carries the hidden attribute.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole, requestInterceptor } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'web', 'cwsjs.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in cwsjs.cs');
  const end = cs.findIndex((l, i) => i > start && l.trim() === '""";');
  const indent = cs[end].match(/^\s*/)[0].length;
  return cs.slice(start + 1, end).map(l => l.slice(indent)).join('\n');
}

const SOURCE = extract('Source');
const TOKEN = 'tok123';
const PREFIX = 'neat-cws:' + TOKEN + ':';

// The script as a page gets it (see CwsScript.For).
const src = SOURCE.replace('__TOKEN__', TOKEN);

const A = 'ddkjiahejlhfcafbddmgiahcphecmpfh';
const B = 'abcdefghijklmnopabcdefghijklmnop';
const C = 'ppppppppppppppppoooooooooooooooo';
const STORE = 'https://chromewebstore.google.com';

let ran = 0;
let failed = 0;
const later = [];

// A check may be async: it starts at once (each has a page and a clock of its own) and its result is
// reported after the plain ones, in order.
function check(name, fn) {
  ran++;
  const done = () => console.log('ok    ' + name);
  const bad = e => { failed++; console.log('FAIL  ' + name + '\n        ' + e.message); };
  try {
    const r = fn();
    if (r && typeof r.then === 'function') {
      r.catch(() => {});   // reported below, in order; not an unhandled rejection meanwhile
      later.push(() => r.then(done, bad));
    }
    else done();
  } catch (e) {
    bad(e);
  }
}

function expect(cond, what) {
  if (!cond) throw new Error(what);
}

function same(got, want, what) {
  const g = JSON.stringify(got);
  const w = JSON.stringify(want);
  if (g !== w) throw new Error(what + ': got ' + g + ', wanted ' + w);
}

// Makes the events of a window report isTrusted = true while the returned switch is on.
function trustSwitch(win) {
  const state = { real: false };
  const add = win.EventTarget.prototype.addEventListener;
  win.EventTarget.prototype.addEventListener = function (type, fn, o) {
    return add.call(this, type, function (e) {
      if (!state.real) return fn.call(this, e);
      const trusted = new Proxy(e, {
        get(t, p) {
          if (p === 'isTrusted') return true;
          const v = Reflect.get(t, p, t);
          return typeof v === 'function' ? v.bind(t) : v;
        },
      });
      return fn.call(this, trusted);
    }, o);
  };
  return state;
}

// A store page: a header with a button of the same kind (it is not the install button), and the extension's
// page in <main>, with the button, a link to another extension and an id in an attribute (neither is the id).
function storePage(label) {
  return `<header><button id="hdr"><span class="x" jsname="V67aGc">Sign in</span></button></header>
<main>
  <h1>uBlock Origin Lite</h1>
  <a id="rel" href="/detail/other/${C}">A related extension</a>
  <div id="card" data-id="${C}"></div>
  <button id="add" class="UywwFc-LgbsSe"><span class="UywwFc-vQzf8d" jsname="V67aGc">${label || 'Add to Chrome'}</span></button>
</main>`;
}

// Gives the elements of a window a box, except the hidden ones (see the notes at the top).
function layout(win) {
  win.Element.prototype.getClientRects = function () {
    return this.closest('[hidden]') ? [] : [{}];
  };
}

function load(html, opts = {}) {
  const dom = new JSDOM(html, {
    runScripts: 'outside-only',
    url: opts.url || STORE + '/detail/ublock-origin-lite/' + A,
    virtualConsole: new VirtualConsole(),
  });
  const win = dom.window;
  const posts = [];

  win.chrome = opts.nochrome ? undefined : {
    webview: { postMessage: m => posts.push(m) },
  };

  layout(win);
  const trust = trustSwitch(win);
  win.eval(src);

  const doc = win.document;
  const $ = s => doc.querySelector(s);

  // What the store's own code does with a click on a button: counted, to see whether it ever ran.
  win.flow = 0;
  doc.addEventListener('click', e => { if (e.target.closest && e.target.closest('button')) win.flow++; });

  // What was reported, as objects.
  const got = () => posts.map(m => {
    if (!m.startsWith(PREFIX)) throw new Error('message does not start with the prefix and token: ' + m);
    return JSON.parse(m.slice(PREFIX.length));
  });

  return {
    win,
    doc,
    $,
    posts,
    got,
    installs: () => got().filter(m => m.kind === 'cws-install'),
    // A genuine click by the person.
    click: el => { trust.real = true; try { (typeof el === 'string' ? $(el) : el).click(); } finally { trust.real = false; } },
    // The same click made by the page's own script.
    fakeClick: el => (typeof el === 'string' ? $(el) : el).click(),
    label: () => $('#add') ? $('#add').textContent : null,
    nav: url => { win.history.pushState({}, '', url); },
  };
}

// ---- the page ----

check('the page is left as it is: the button keeps the store\'s words', () => {
  const p = load(storePage());
  same(p.label(), 'Add to Chrome', 'label');
  p.click('#add');
  same(p.label(), 'Add to Chrome', 'label after a click');
});

check('the script says once that it is in place, and nothing else by itself', () => {
  const p = load(storePage());
  same(p.got(), [{ kind: 'cws-loaded' }], 'messages');
});

check('a real click is stopped, and the id from the address is sent', () => {
  const p = load(storePage());
  p.click('#add');
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.installs(), [{ kind: 'cws-install', id: A }], 'installs');
});

check('a click on the label inside the button counts as a click on the button', () => {
  const p = load(storePage());
  p.click('#add span');
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.installs().length, 1, 'installs');
});

check('the button is found by the jsname of its label, whatever the language or the classes', () => {
  for (const words of ['Thêm vào Chrome', 'Hinzufügen', 'Установить']) {
    const p = load(storePage(words));
    p.click('#add');
    same(p.installs().length, 1, 'installs for ' + words);
    same(p.label(), words, 'label');
  }
});

check('every click is taken: what to say about a second one is the window\'s business', () => {
  const p = load(storePage());
  p.click('#add');
  p.click('#add');
  same(p.installs().length, 2, 'installs');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

check('a click that the page\'s script made is left alone', () => {
  const p = load(storePage());
  p.fakeClick('#add');
  same(p.installs().length, 0, 'installs');
  same(p.win.flow, 1, 'the click went on to the page');
});

check('a click anywhere else is not touched, also on a button outside <main>', () => {
  const p = load(storePage());
  p.click('#rel');
  p.click('#hdr');
  same(p.installs().length, 0, 'installs');
  same(p.win.flow, 1, 'the header button went on to the page');
});

check('a button of <main> without that label is not the install button, wherever it stands', () => {
  const p = load('<main><button id="share"><span>Share</span></button><button id="add"><span jsname="V67aGc">Add to Chrome</span></button></main>');
  p.click('#share');
  same(p.installs().length, 0, 'installs');
  same(p.win.flow, 1, 'the click went on to the page');
  p.click('#add');
  same(p.installs().length, 1, 'installs');
});

check('a later button with the same kind of label is not the install button', () => {
  const p = load(storePage().replace('</main>', '<button id="more"><span jsname="V67aGc">Write a review</span></button></main>'));
  p.click('#more');
  same(p.installs().length, 0, 'installs');
  same(p.win.flow, 1, 'the click went on to the page');
});

// Seen on the real store (November 2026): a hidden view in the document, with a <main> and an install
// button of its own, while the script had taken that button for the one on show.
check('a page that was left, hidden in the document before this one, does not get in the way', () => {
  const left = '<div hidden><main><button id="old"><span jsname="V67aGc">Add to Chrome</span></button></main></div>';
  const p = load(left + storePage());
  p.click('#add');
  same(p.installs(), [{ kind: 'cws-install', id: A }], 'installs');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

check('a hidden button before the one on show, in the same <main>, is passed over', () => {
  const p = load(storePage().replace('<main>', '<main><button hidden id="old"><span jsname="V67aGc">Add to Chrome</span></button>'));
  p.click('#add');
  same(p.installs().length, 1, 'installs');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

check('a button that the store has just drawn is handled', () => {
  const p = load('<header></header>');
  p.doc.body.insertAdjacentHTML('beforeend', '<main><button id="add"><span jsname="V67aGc">Add to Chrome</span></button></main>');
  p.click('#add');
  same(p.installs().length, 1, 'installs');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

// ---- the pages around it ----

check('on a page of the store that is not an extension\'s page a click is left alone', () => {
  for (const url of [STORE + '/', STORE + '/category/extensions', STORE + '/detail/name', STORE + '/detail/name/short', STORE + '/search/adblock', STORE + '/detail/n/' + A.toUpperCase()]) {
    const p = load(storePage(), { url });
    p.click('#add');
    same(p.installs().length, 0, 'installs on ' + url);
    same(p.win.flow, 1, 'the click went on to the page on ' + url);
  }
});

check('other sites, and look-alike addresses, get nothing', () => {
  for (const url of [
    'https://example.com/detail/n/' + A,
    'http://chromewebstore.google.com/detail/n/' + A,
    'https://chromewebstore.google.com.evil.example/detail/n/' + A,
    'https://evil.example/chromewebstore.google.com/detail/n/' + A,
    'https://chrome.google.com/webstore/detail/n/' + A,
  ]) {
    const p = load(storePage(), { url });
    p.click('#add');
    same(p.posts.length, 0, 'messages on ' + url);
    same(p.win.flow, 1, 'the click went on to the page on ' + url);
  }
});

check('a frame inside a page gets nothing', async () => {
  // A real frame, loaded from the store's own address (the loader hands it a store page), so that the
  // address is right and only "not the top page" can be what keeps the script out.
  const dom = new JSDOM('<body><iframe id="f" src="' + STORE + '/detail/x/' + A + '"></iframe></body>', {
    runScripts: 'outside-only',
    url: STORE + '/',
    virtualConsole: new VirtualConsole(),
    resources: {
      interceptors: [
        requestInterceptor(() => new Response(storePage(), { headers: { 'Content-Type': 'text/html' } })),
      ],
    },
  });
  const frame = dom.window.document.getElementById('f');
  await new Promise(r => frame.addEventListener('load', r));

  const fw = frame.contentWindow;
  expect(fw.top !== fw, 'the frame is not a frame');
  same(fw.location.origin, STORE, 'the frame\'s origin');
  expect(fw.document.querySelector('main #add'), 'the frame has no store page');

  const posts = [];
  fw.chrome = { webview: { postMessage: m => posts.push(m) } };
  layout(fw);
  fw.eval(src);

  same(posts.length, 0, 'messages');
});

check('without the window\'s channel the script does nothing and does not throw', () => {
  const p = load(storePage(), { nochrome: true });
  p.click('#add');
  same(p.win.flow, 1, 'the click went on to the page');
});

check('after the store moves to another extension\'s page the new id is used', () => {
  const p = load(storePage());
  p.nav('/detail/other-name/' + B);
  p.click('#add');
  same(p.installs(), [{ kind: 'cws-install', id: B }], 'install');

  p.win.history.replaceState({}, '', '/category/extensions');
  p.click('#add');
  same(p.installs().length, 1, 'installs after leaving');
  same(p.win.flow, 1, 'the click went on to the page');
});

check('every message carries the prefix and the token, and nothing else of the page', () => {
  const p = load(storePage());
  p.click('#add');
  expect(p.posts.length === 2, 'messages: ' + p.posts.length);
  for (const m of p.posts) {
    expect(typeof m === 'string' && m.startsWith(PREFIX), 'bad prefix: ' + m);
    const o = JSON.parse(m.slice(PREFIX.length));
    for (const k of Object.keys(o)) expect(k === 'kind' || k === 'id', 'unexpected field ' + k);
    expect(!m.includes('uBlock') && !m.includes(C), 'the message holds something from the page');
  }
});

(async () => {
  for (const run of later) await run();
  console.log(ran - failed + ' of ' + ran + ' checks passed');
  process.exit(failed ? 1 : 0);
})();
