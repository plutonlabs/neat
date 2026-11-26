'use strict';
// Runs the page script of the Chrome Web Store install button (src/neat/web/cwsjs.cs) against a made-up
// store page, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.
//
// Two things about how it is run:
//   - jsdom marks every script-made event as not trusted, so a "real" click is made the way
//     tests/capture.check does it: a wrapper reports isTrusted = true for the duration of one dispatch
//     (the script itself is not changed for the test). A click made by the page's own script is
//     dispatched without the wrapper, which is how the "ignored" case is checked.
//   - The script waits on timers (a short delay before it looks at the page again, and a grace period
//     before it reports a missing button). They are replaced by a clock that only moves when the check
//     says so, so nothing here depends on how fast the machine is.

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
const GRACE = 5000;
const PREFIX = 'neat-cws:' + TOKEN + ':';

// The script as a page gets it (see CwsScript.For).
const src = SOURCE.replace('__TOKEN__', TOKEN).replace('__GRACE__', String(GRACE));

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

function load(html, opts = {}) {
  const dom = new JSDOM(html, {
    runScripts: 'outside-only',
    url: opts.url || STORE + '/detail/ublock-origin-lite/' + A,
    virtualConsole: new VirtualConsole(),
  });
  const win = dom.window;
  const posts = [];
  const hear = [];

  win.chrome = opts.nochrome ? undefined : {
    webview: {
      postMessage: m => posts.push(m),
      addEventListener: (type, fn) => { if (type === 'message') hear.push(fn); },
    },
  };

  // The clock: timers run only when the check moves it.
  const queue = [];
  let now = 0;
  let seq = 0;
  win.setTimeout = (fn, ms) => { queue.push({ id: ++seq, at: now + (ms || 0), fn }); return seq; };
  win.clearTimeout = id => { const i = queue.findIndex(t => t.id === id); if (i >= 0) queue.splice(i, 1); };
  const advance = ms => {
    const end = now + ms;
    for (;;) {
      queue.sort((a, b) => a.at - b.at || a.id - b.id);
      const t = queue[0];
      if (!t || t.at > end) break;
      queue.shift();
      now = t.at;
      t.fn();
    }
    now = end;
  };

  const trust = trustSwitch(win);
  win.eval(src);

  const doc = win.document;
  const $ = s => doc.querySelector(s);

  // What the store's own code does with a click on its button: counted, to see whether it ever ran.
  win.flow = 0;
  doc.addEventListener('click', e => { if (e.target.closest && e.target.closest('button')) win.flow++; });

  const flush = () => new Promise(r => setImmediate(r));

  return {
    win,
    doc,
    $,
    posts,
    // What was reported, as objects.
    got: () => posts.map(m => {
      if (!m.startsWith(PREFIX)) throw new Error('message does not start with the prefix and token: ' + m);
      return JSON.parse(m.slice(PREFIX.length));
    }),
    // Lets the page's observers and timers run: the time asked for, plus a moment for what that sets off.
    settle: async (ms = 100) => { await flush(); advance(ms); await flush(); advance(40); },
    // A genuine click by the person.
    click: el => { trust.real = true; try { (typeof el === 'string' ? $(el) : el).click(); } finally { trust.real = false; } },
    // The same click made by the page's own script.
    fakeClick: el => (typeof el === 'string' ? $(el) : el).click(),
    // The window speaks.
    say: (state, id, tok) => {
      const m = (tok === undefined ? PREFIX : 'neat-cws:' + tok + ':') + JSON.stringify({ state, id });
      hear.forEach(fn => fn({ data: m }));
    },
    sayRaw: data => hear.forEach(fn => fn({ data })),
    label: () => $('#add') ? $('#add').textContent : null,
    nav: url => { win.history.pushState({}, '', url); },
  };
}

const text = p => p.label();

// ---- the page ----

check('the label of the install button is rewritten', async () => {
  const p = load(storePage());
  await p.settle();
  same(text(p), 'Add to NEAT', 'label');
});

check('the label is found by its jsname, whatever the language or the classes', async () => {
  const p = load(storePage('Zu Chrome hinzufügen'));
  await p.settle();
  same(text(p), 'Add to NEAT', 'label');
  same(p.$('#add span').className, 'UywwFc-vQzf8d', 'its classes are left alone');
});

check('a button outside <main> is not the install button', async () => {
  const p = load(storePage());
  await p.settle();
  same(p.$('#hdr span').textContent, 'Sign in', 'header button');
});

check('the page is asked about the extension once, with the id from the address', async () => {
  const p = load(storePage());
  await p.settle();
  same(p.got(), [{ kind: 'cws-ready', id: A }], 'messages');
});

check('a real click is stopped, and the id from the address is sent', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#add');
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.got().slice(1), [{ kind: 'cws-install', id: A }], 'messages after ready');
  same(text(p), 'Adding...', 'label');
});

check('a click on the label inside the button counts as a click on the button', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#add span');
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.got().filter(m => m.kind === 'cws-install').length, 1, 'installs');
});

check('a click that the page\'s script made is left alone', async () => {
  const p = load(storePage());
  await p.settle();
  p.fakeClick('#add');
  same(p.got().filter(m => m.kind === 'cws-install').length, 0, 'installs');
  same(p.win.flow, 1, 'the click went on to the page');
  same(text(p), 'Add to NEAT', 'label');
});

check('a click anywhere else is not touched', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#rel');
  p.click('#hdr');
  same(p.got().length, 1, 'only the ready message');
});

check('a second click while it works is stopped and sends nothing', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#add');
  p.click('#add');
  p.click('#add');
  same(p.got().filter(m => m.kind === 'cws-install').length, 1, 'installs');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

check('the answers of the window change the label, and clicks are ignored while it works or when added', async () => {
  const p = load(storePage());
  await p.settle();
  p.say('busy', A);
  same(text(p), 'Adding...', 'busy');
  p.click('#add');
  same(p.got().filter(m => m.kind === 'cws-install').length, 0, 'install while busy');
  same(p.win.flow, 0, 'store flow while busy');

  p.say('added', A);
  same(text(p), 'Added', 'added');
  p.click('#add');
  same(p.got().filter(m => m.kind === 'cws-install').length, 0, 'install when added');
  same(p.win.flow, 0, 'store flow when added');

  p.say('add', A);
  same(text(p), 'Add to NEAT', 'add');
  p.click('#add');
  same(p.got().filter(m => m.kind === 'cws-install').length, 1, 'install after add');
});

check('the answer stays on the button when the store draws the label again', async () => {
  const p = load(storePage());
  await p.settle();
  p.say('added', A);
  p.$('#add span').textContent = 'Add to Chrome';
  await p.settle();
  same(text(p), 'Added', 'label');
});

check('answers that are not for this page are ignored', async () => {
  const p = load(storePage());
  await p.settle();
  p.say('added', B);                      // another extension
  same(text(p), 'Add to NEAT', 'other id');
  p.say('added', A, 'other-token');       // not this window's token
  same(text(p), 'Add to NEAT', 'other token');
  p.say('exploded', A);                   // not a state
  same(text(p), 'Add to NEAT', 'other state');
  p.say('constructor', A);                // a name on every object, not a state
  same(text(p), 'Add to NEAT', 'name from the prototype');
  p.sayRaw('neat-cws:' + TOKEN + ':{not json');
  p.sayRaw({ state: 'added', id: A });
  p.sayRaw(42);
  p.sayRaw(null);
  same(text(p), 'Add to NEAT', 'garbage');
});

check('nothing grows into a loop: the label is written once and then left alone', async () => {
  const p = load(storePage());
  let changes = 0;
  const watch = new p.win.MutationObserver(r => { changes += r.length; });
  watch.observe(p.$('#add'), { childList: true, subtree: true, characterData: true });
  for (let i = 0; i < 6; i++) await p.settle(200);
  expect(changes >= 1 && changes <= 2, 'the label changed ' + changes + ' times');
  const seen = changes;
  for (let i = 0; i < 6; i++) await p.settle(200);
  same(changes, seen, 'changes after it settled');
});

// ---- the pages around it ----

check('a page of the store that is not an extension\'s page does nothing', async () => {
  for (const url of [STORE + '/', STORE + '/category/extensions', STORE + '/detail/name', STORE + '/detail/name/short', STORE + '/search/adblock', STORE + '/detail/n/' + A.toUpperCase()]) {
    const p = load(storePage(), { url });
    await p.settle();
    same(p.posts.length, 0, 'messages on ' + url);
    same(text(p), 'Add to Chrome', 'label on ' + url);
    p.click('#add');
    same(p.posts.length, 0, 'messages after a click on ' + url);
    same(p.win.flow, 1, 'the click went on to the page on ' + url);
  }
});

check('other sites, and look-alike addresses, get nothing', async () => {
  for (const url of [
    'https://example.com/detail/n/' + A,
    'http://chromewebstore.google.com/detail/n/' + A,
    'https://chromewebstore.google.com.evil.example/detail/n/' + A,
    'https://evil.example/chromewebstore.google.com/detail/n/' + A,
    'https://chrome.google.com/webstore/detail/n/' + A,
  ]) {
    const p = load(storePage(), { url });
    await p.settle();
    same(p.posts.length, 0, 'messages on ' + url);
    same(text(p), 'Add to Chrome', 'label on ' + url);
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
  fw.chrome = { webview: { postMessage: m => posts.push(m), addEventListener: () => {} } };
  fw.eval(src);
  await new Promise(r => setTimeout(r, 120));

  same(posts.length, 0, 'messages');
  same(fw.document.querySelector('#add').textContent, 'Add to Chrome', 'label');
});

check('without the window\'s channel the script does nothing and does not throw', async () => {
  const p = load(storePage(), { nochrome: true });
  await p.settle();
  same(text(p), 'Add to Chrome', 'label');
});

check('after the store moves to another extension\'s page the new id is used', async () => {
  const p = load(storePage());
  await p.settle();
  p.say('added', A);
  p.nav('/detail/other-name/' + B);
  await p.settle();

  same(p.got().slice(-1), [{ kind: 'cws-ready', id: B }], 'asked about the new page');
  same(text(p), 'Add to NEAT', 'the state of the old page is not carried over');
  p.click('#add');
  same(p.got().slice(-1), [{ kind: 'cws-install', id: B }], 'install');
  same(p.win.flow, 0, 'the store\'s own handler ran');
});

check('an answer for the page that was left does not change the new one', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#add');
  p.nav('/detail/other-name/' + B);
  await p.settle();
  p.say('added', A);
  same(text(p), 'Add to NEAT', 'label');
});

check('replaceState and a step back are followed too', async () => {
  const p = load(storePage());
  await p.settle();
  p.win.history.replaceState({}, '', '/detail/x/' + B);
  await p.settle();
  same(p.got().slice(-1), [{ kind: 'cws-ready', id: B }], 'replaceState');
  p.win.history.replaceState({}, '', '/detail/x/' + A);
  await p.settle();
  p.win.dispatchEvent(new p.win.PopStateEvent('popstate'));
  await p.settle();
  same(p.got().slice(-1), [{ kind: 'cws-ready', id: A }], 'back again');
});

check('a page that is not an extension\'s any more is left alone', async () => {
  const p = load(storePage());
  await p.settle();
  p.nav('/category/extensions');
  await p.settle();
  const n = p.posts.length;
  p.$('#add span').textContent = 'Add to Chrome';
  await p.settle();
  same(text(p), 'Add to Chrome', 'label');
  p.click('#add');
  same(p.posts.length, n, 'messages');
  same(p.win.flow, 1, 'the click went on to the page');
});

check('history.pushState still works as the page expects', async () => {
  const p = load(storePage());
  p.win.history.pushState({ a: 1 }, '', '/detail/x/' + B);
  same(p.win.location.pathname, '/detail/x/' + B, 'address');
  same(p.win.history.state.a, 1, 'state');
});

check('the button is replaced by the store: the new one is rewritten and handled', async () => {
  const p = load(storePage());
  await p.settle();
  const old = p.$('#add');
  const fresh = p.doc.createElement('button');
  fresh.id = 'add';
  fresh.innerHTML = '<span jsname="V67aGc">Add to Chrome</span>';
  old.replaceWith(fresh);
  await p.settle();
  same(text(p), 'Add to NEAT', 'label');

  p.click('#add');
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.got().filter(m => m.kind === 'cws-install').length, 1, 'installs');
});

check('a click on a button that the store has just drawn is stopped before the page has looked', async () => {
  const p = load(storePage());
  await p.settle();
  p.$('#add').replaceWith(Object.assign(p.doc.createElement('button'), { id: 'add', innerHTML: '<span jsname="V67aGc">Add to Chrome</span>' }));
  p.click('#add');           // no time has passed: the page has not looked at the new button
  same(p.win.flow, 0, 'the store\'s own handler ran');
  same(p.got().filter(m => m.kind === 'cws-install').length, 1, 'installs');
});

check('the page is drawn late: the button is found when it comes', async () => {
  const p = load('<header></header>');
  await p.settle();
  p.doc.body.insertAdjacentHTML('beforeend', '<main><button id="add"><span jsname="V67aGc">Add to Chrome</span></button></main>');
  await p.settle();
  same(text(p), 'Add to NEAT', 'label');
  same(p.got().filter(m => m.kind === 'cws-nobutton').length, 0, 'reported missing');
});

check('without a button nothing changes, and the window is told once after the grace', async () => {
  const p = load('<header><button><span jsname="V67aGc">Sign in</span></button></header><main><h1>x</h1><a href="/">home</a></main>');
  await p.settle();
  same(p.got(), [{ kind: 'cws-ready', id: A }], 'before the grace');

  await p.settle(GRACE + 100);
  same(p.got().filter(m => m.kind === 'cws-nobutton').length, 1, 'after the grace');

  // More changes, a second page, much more time: still the one report.
  p.doc.querySelector('main').insertAdjacentHTML('beforeend', '<p>more</p>');
  p.nav('/detail/x/' + B);
  await p.settle(GRACE * 3);
  same(p.got().filter(m => m.kind === 'cws-nobutton').length, 1, 'reports in all');
  same(p.doc.querySelector('header span').textContent, 'Sign in', 'header button');
  expect(!p.doc.querySelector('#add'), 'a button of its own was made');
});

check('a button that has come before the grace ends is not reported missing', async () => {
  const p = load('<main></main>');
  await p.settle(GRACE - 1000);
  p.doc.querySelector('main').innerHTML = '<button id="add"><span jsname="V67aGc">Add to Chrome</span></button>';
  await p.settle(GRACE * 2);
  same(p.got().filter(m => m.kind === 'cws-nobutton').length, 0, 'reported missing');
  same(text(p), 'Add to NEAT', 'label');
});

check('when the person comes back to an added extension the window is asked again', async () => {
  const p = load(storePage());
  await p.settle();
  p.say('added', A);
  const before = p.got().length;
  p.win.dispatchEvent(new p.win.Event('focus'));
  same(p.got().length, before + 1, 'messages');
  same(p.got().slice(-1), [{ kind: 'cws-ready', id: A }], 'the question');

  // Not for an extension that is not added: nothing to find out.
  const q = load(storePage());
  await q.settle();
  q.win.dispatchEvent(new q.win.Event('focus'));
  same(q.got().length, 1, 'messages for one that is not added');
});

check('every message carries the prefix and the token, and nothing else of the page', async () => {
  const p = load(storePage());
  await p.settle();
  p.click('#add');
  expect(p.posts.length >= 2, 'no messages');
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
