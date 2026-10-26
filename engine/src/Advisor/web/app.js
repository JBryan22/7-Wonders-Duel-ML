// The Advisor Window page (spec A8). It holds no game rules: the server sends the whole view (/api/state), and each
// click posts one Move Log line that the server checks, appends and replays. Advice is searched on the server in the
// background; the page polls for it and draws the % badges and the compare table.

const $ = (id) => document.getElementById(id);
let view = null;          // the last StateResponse
let showNewGame = false;  // the new-game screen was opened over a game in progress
let guildPick = [];       // slots chosen for the Age III Guild back pattern
let multiPick = [];       // options chosen in a multi-pick Reveal (tokens, draft, library, returned cards)
let fieldPick = {};       // Pantheon Reveal forms: the option chosen per field (by field index)

// ------------------------------------------------------------------ server

async function call(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
  });
  let data;
  try { data = await res.json(); } catch { data = { error: `The Advisor server answered ${res.status}` }; }
  view = data.game !== undefined ? data : { ...view, error: data.error };
  render();
  return !data.error;
}

// ------------------------------------------------------------------ advice (computed in the background on the server)

let advicePoll = null;
const ADVICE_POLL_MS = 200;

// Shows a fresh advice box; the board is redrawn too, since its % badges come from the same advice.
function setAdvice(advice) {
  if (!view) return;
  const before = adviceSignature(view.advice);
  view.advice = advice;
  if (adviceSignature(advice) !== before) render();
  else watchAdvice();
}
const adviceSignature = (a) => a ? `${a.status}|${a.simulations}|${a.rows.length}|${a.auto}|${a.message}|${a.defaultSimulations}` : '';

async function adviceCall(method, path, body) {
  try {
    const res = await fetch(path, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
    setAdvice(await res.json());
  } catch {
    setAdvice({ ...view.advice, status: 'failed', message: 'The Advisor server did not answer.' });
  }
}
const askAdvice = (longer) => adviceCall('POST', '/api/advice', { longer });

// While a search runs, ask the server for the advice box every 200 ms (the page never blocks on a search).
function watchAdvice() {
  clearTimeout(advicePoll);
  if (view?.advice?.status === 'thinking') advicePoll = setTimeout(() => adviceCall('GET', '/api/advice'), ADVICE_POLL_MS);
}

const pct = (x) => `${(x * 100).toFixed(1)}%`;
const sharePct = (x) => x >= 0.995 ? '100%' : x < 0.005 ? '<1%' : `${Math.round(x * 100)}%`;

// The share of the search for one option (card menus and tiles) by its key (its Move Log line; a Statue pair: both), or null.
function shareOf(line) {
  const a = view?.advice;
  if (!a || !a.rows.length || a.shares[line] === undefined) return null;
  return a.shares[line];
}
const isBestKey = (key) => view?.advice?.rows[0]?.key === key;

function advicePanel(a) {
  const panel = h('div', { class: 'panel advice', id: 'advice' }, h('h2', {}, 'Advice'));
  if (!a) return panel;
  const thinking = a.status === 'thinking';
  const auto = h('label', { class: 'small muted auto' },
    h('input', { type: 'checkbox', checked: a.auto, onchange: (e) => adviceCall('POST', '/api/advice/auto', { on: e.target.checked }) }),
    ' Advise by itself on my turns');
  if (a.notice) panel.append(h('div', { class: 'note small' }, a.notice));

  if (a.status === 'off') {
    panel.append(h('p', { class: 'muted small' }, a.message));
    return panel;
  }
  if (a.status === 'unavailable') {
    panel.append(h('p', { class: 'muted small' }, a.message), auto);
    return panel;
  }
  const ask = h('button', { class: 'btn', id: 'ask', disabled: thinking || a.rows.length > 0, onclick: () => askAdvice(false) },
    `Ask for advice (${a.defaultSimulations.toLocaleString('en')} simulations)`);
  const longer = h('button', {
    class: 'btn ghost', id: 'longer', disabled: thinking || a.simulations >= a.thinkLongerSimulations,
    title: `Search ${a.thinkLongerSimulations.toLocaleString('en')} simulations: slower, and the whiskers get narrower`,
    onclick: () => askAdvice(true),
  }, `Think longer (${a.thinkLongerSimulations.toLocaleString('en')})`);
  panel.append(h('div', { class: 'advice-actions' }, a.rows.length ? null : ask, a.rows.length ? longer : null,
    thinking ? h('span', { class: 'thinking' }, h('span', { class: 'spinner' }), a.message) : null));
  if (a.status === 'failed') panel.append(h('div', { class: 'small', style: 'color:var(--err)' }, a.message));
  if (a.rows.length) panel.append(compareTable(a));
  panel.append(auto);
  return panel;
}


// The compare table (spec A8): up to 5 options, best first; win % with ±2 standard-error whiskers and a 50% guide;
// the gap to the best, "≈ same" when inside the combined noise; the outcome bar (a network with an outcome head only);
// every reason tag. Hovering a row shows its expected line under the table and as numbered markers on the board;
// clicking pins it (the best row by default).
let pinnedRow = null;   // key of the clicked row
let hoverRow = null;    // key of the hovered row

const OUTCOME_LABELS = {
  'win-military': 'You win: military', 'win-science': 'You win: science', 'win-civilian': 'You win: civilian', draw: 'Draw',
  'loss-military': 'They win: military', 'loss-science': 'They win: science', 'loss-civilian': 'They win: civilian',
};

function shownRow(a) {
  if (!a?.rows.length) return null;
  return a.rows.find(r => r.key === hoverRow) ?? a.rows.find(r => r.key === pinnedRow) ?? a.rows[0];
}

function outcomeBar(a, r) {
  if (!r.outcome) return h('span', { class: 'muted small' }, '—');
  const parts = a.outcomeClasses.map((c, i) => ({ c, p: r.outcome[i] }));
  return h('div', { class: 'outcome', title: parts.filter(x => x.p >= 0.005).map(x => `${OUTCOME_LABELS[x.c] ?? x.c}: ${pct(x.p)}`).join('\n') },
    parts.map(x => h('span', { class: `oc-${x.c}`, style: `width:${x.p * 100}%` })));
}

function compareTable(a) {
  const hasOutcome = a.hasOutcome;
  const table = h('table', { class: 'compare' },
    h('tr', {}, h('th', {}, 'Move'), h('th', {}, 'Win % (±2 SE)'), h('th', {}, 'vs best'), hasOutcome ? h('th', {}, 'How it ends') : null));
  const shown = shownRow(a);
  for (const r of a.rows) {
    const lo = Math.max(0, r.win - 2 * r.stdErr), hi = Math.min(1, r.win + 2 * r.stdErr);
    const gap = r.best ? h('span', { class: 'muted' }, '—')
      : r.same ? h('span', { class: 'same', title: `Within the noise: the gap ${pct(Math.abs(r.gap))} is under ${pct(r.noise)}` }, '≈ same')
      : h('span', { class: 'gap', title: `Outside the noise (${pct(r.noise)})` }, `${r.gap > 0 ? '−' : '+'}${pct(Math.abs(r.gap))}`);
    const select = {
      'data-key': r.key,
      onmouseenter: () => { hoverRow = r.key; showLine(); },
      onmouseleave: () => { hoverRow = null; showLine(); },
      onclick: () => { pinnedRow = r.key; showLine(); },
    };
    const cls = `${r.best ? 'best' : ''}${r === shown ? ' shown' : ''}`;
    table.append(
      h('tr', { class: `main ${cls}`, ...select },
        h('td', {}, h('b', {}, r.label), h('br'), h('span', { class: 'muted small' }, `${sharePct(r.share)} of search`)),
        h('td', { class: 'wincell' },
          h('div', { class: 'whisk', title: `${pct(r.win)} ± ${pct(2 * r.stdErr)} (${r.visits} simulations)` },
            h('div', { class: 'fifty' }),
            h('div', { class: 'bar', style: `width:${r.win * 100}%` }),
            h('div', { class: 'err', style: `left:${lo * 100}%;width:${(hi - lo) * 100}%` })),
          h('span', { class: 'winpct' }, pct(r.win))),
        h('td', {}, gap),
        hasOutcome ? h('td', {}, outcomeBar(a, r)) : null),
      h('tr', { class: `tags ${cls}`, ...select },
        h('td', { colspan: hasOutcome ? 4 : 3 }, r.reasons.map(t => h('span', { class: 'tag' }, t)))));
  }
  return h('div', {},
    table,
    hasOutcome ? h('div', { class: 'small outcome-legend' }, a.outcomeClasses.map(c =>
      h('span', {}, h('i', { class: `oc-${c}` }), OUTCOME_LABELS[c] ?? c))) : null,
    h('div', { id: 'line', class: 'line' }, expectedLine(shown)),
    h('div', { class: 'small muted', style: 'margin-top:6px' },
      `${a.simulations.toLocaleString('en')} simulations · ${a.seconds.toFixed(2)} s · ${a.agent}`),
    h('div', { class: 'small muted legend' },
      'Bar: your chance to win after this Move (a draw counts half); whiskers: ±2 standard errors; dotted line: 50%. ',
      '"≈ same": the gap to the best Move is inside the noise, so treat them as equally good. ',
      hasOutcome ? 'How it ends: the network\'s guess of how the game finishes after this Move. ' : '',
      'Hover a row to see its expected line; click to keep it.'));
}

// The expected line of one row: its own Move(s), then what the search expects next, through chance steps.
function expectedLine(r) {
  if (!r) return null;
  const box = [h('div', { class: 'line-title' }, 'Expected line: ', h('b', {}, r.label))];
  if (!r.hasLine) {
    box.push(h('div', { class: 'muted small no-line' },
      'No line: the search did not look far enough past this Move to show one (Think longer may help).'));
    return box;
  }
  const list = h('ol', { class: 'steps' });
  let n = 0;
  for (const s of r.expected) {
    if (s.afterReveal) list.append(h('li', { class: 'chance' }, 'face-down card revealed'));
    n++;
    list.append(h('li', { class: `${s.you ? 'you' : 'them'}${s.own ? ' own' : ''}`, value: n },
      h('span', { class: `mark ${s.you ? 'you' : 'them'}` }, n),
      `${s.you ? 'You' : 'They'}: ${s.label}`));
  }
  box.push(list);
  return box;
}

// Redraws the line list and the board's numbered markers for the shown row (no full render, so hovering stays smooth).
function showLine() {
  const a = view?.advice;
  const r = shownRow(a);
  document.querySelectorAll('table.compare tr[data-key]').forEach(tr => tr.classList.toggle('shown', tr.dataset.key === r?.key));
  const line = $('line');
  if (line) line.replaceChildren(...[expectedLine(r)].flat().filter(Boolean));
  drawMarkers(r);
}

function drawMarkers(r) {
  document.querySelectorAll('.pyramid .mark').forEach(m => m.remove());
  if (!r?.hasLine) return;
  r.expected.forEach((s, i) => {
    if (s.slot == null) return;
    const card = document.querySelector(`.pyramid .card[data-slot="${s.slot}"]`);
    if (card) card.append(h('span', { class: `mark ${s.you ? 'you' : 'them'}`, title: `Step ${i + 1}: ${s.you ? 'you' : 'they'}` }, i + 1));
  });
}

const enter = (line, then) => {
  guildPick = []; multiPick = []; fieldPick = {};
  return call('POST', '/api/entry', then ? { line, then } : { line });
};

// ------------------------------------------------------------------ DOM helper

function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'class') el.className = v;
    else if (k === 'style') el.style.cssText = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}


// ------------------------------------------------------------------ readable cards (ticket 37)
// Every card, Wonder and Progress token in icons and words comes from the server's catalog (built from the card data),
// so the page holds no per-card text. Elements about one carry data-ref="card:baths" (or wonder:..., token:...); hovering
// (or tapping) one shows its full details, with the prices right now when the element has them (el._prices).

let catalog = null;
async function loadCatalog() {
  try { catalog = await (await fetch('/api/catalog')).json(); } catch { catalog = null; }
}

function info(ref) {
  if (!catalog || !ref) return null;
  const [kind, id] = ref.split(':');
  const pan = catalog.pantheon;
  if (kind === 'divinity') return pan?.divinities[id] ?? null;
  const base = kind === 'card' ? catalog.cards : kind === 'wonder' ? catalog.wonders : catalog.tokens;
  const extra = !pan ? null : kind === 'card' ? pan.cards : kind === 'wonder' ? pan.wonders : pan.tokens;
  return base[id] ?? extra?.[id] ?? null;
}
const hasCost = (i) => i.kind !== 'token' && i.kind !== 'divinity';

// One compact icon line: effect, then cost (the card faces have their own layout).
function iconLine(ref, { cost = true } = {}) {
  const i = info(ref);
  if (!i) return null;
  return h('span', { class: 'icons' },
    i.effectIcons ? h('span', { class: 'eff' }, i.effectIcons) : null,
    cost && hasCost(i) ? h('span', { class: 'cost', title: `Cost: ${i.costText}` }, i.costIcons || 'free') : null);
}

// A small chip for a card, Wonder or token: colour, name and effect icons; details on hover.
function chip(ref, name, cls = '') {
  const i = info(ref);
  const colour = i?.kind === 'card' ? `c-${i.color}` : 'light';
  return h('span', { class: `chip ${colour} ${cls}`, 'data-ref': ref }, name ?? i?.name ?? ref,
    i?.effectIcons ? h('span', { class: 'chip-icons' }, i.effectIcons) : null);
}

// The full details of one card, Wonder or token, and the prices right now if known.
function infoBlock(ref, prices) {
  const i = info(ref);
  if (!i) return null;
  const box = h('div', { class: 'info' },
    h('div', { class: 'info-head' }, h('b', {}, i.name), h('span', { class: `info-type t-${i.color}` }, i.type)));
  if (hasCost(i)) box.append(h('div', {}, h('span', { class: 'k' }, 'Cost '),
    i.costIcons ? h('span', { class: 'big-icons' }, i.costIcons) : null, h('span', { class: 'muted' }, ` ${i.costText}`)));
  box.append(h('div', {}, h('span', { class: 'k' }, 'Effect '), h('span', { class: 'big-icons' }, i.effectIcons)));
  box.append(h('ul', {}, i.effect.map(e => h('li', {}, e))));
  if (prices?.length) box.append(h('div', { class: 'info-prices' }, prices.map(p => h('div', { class: p.affordable ? '' : 'cant' },
    h('b', {}, priceText(p).replace(/^[a-z]/, c => c.toUpperCase())),
    p.detail.length ? h('div', { class: 'small muted' }, p.detail.join(' ')) : null))));
  return box;
}

// Hover/tap details.
function showTip(el, pinned) {
  const tip = $('tip');
  const block = infoBlock(el.dataset.ref, el._prices);
  if (!block) return;
  tip.replaceChildren(block);
  tip.hidden = false;
  tip.classList.toggle('pinned', !!pinned);
  const r = el.getBoundingClientRect();
  const w = tip.offsetWidth, ht = tip.offsetHeight;
  let left = r.right + 8;
  if (left + w > window.innerWidth - 8) left = Math.max(8, r.left - w - 8);
  let top = Math.min(Math.max(8, r.top), window.innerHeight - ht - 8);
  tip.style.left = `${left}px`;
  tip.style.top = `${Math.max(8, top)}px`;
}
function hideTip() { const tip = $('tip'); if (tip) tip.hidden = true; }
document.addEventListener('mouseover', (e) => {
  const el = e.target.closest?.('[data-ref]');
  if (el && !$('popover').contains(el)) showTip(el, false);
});
document.addEventListener('mouseout', (e) => {
  const el = e.target.closest?.('[data-ref]');
  if (el && !el.contains(e.relatedTarget) && !$('tip').classList.contains('pinned')) hideTip();
});
// Tap (touch) or click on something without its own action shows its details until the next click.
document.addEventListener('click', (e) => {
  const el = e.target.closest?.('[data-ref]');
  if (el && !el.closest('button, .acc, .pulse, .pick, #popover')) { showTip(el, true); e.stopPropagation(); return; }
  if (!e.target.closest?.('#tip')) hideTip();
}, true);

// The icon key (Pantheon's icons too in a Pantheon game).
function legendPanel(pantheon) {
  if (!catalog) return null;
  let open = true;
  try { open = localStorage.getItem('7wd-legend') !== 'closed'; } catch { /* no storage */ }
  const icons = pantheon && catalog.pantheon ? [...catalog.legend, ...catalog.pantheon.legend] : catalog.legend;
  const d = h('details', { class: 'legend-box', open },
    h('summary', {}, 'Icon key'),
    h('div', { class: 'legend-grid' }, icons.map(l => h('span', {}, h('b', {}, l.icon), ' ', l.meaning))),
    h('div', { class: 'small muted' }, 'Hover (or tap) any card, Wonder or token for its full text and what it costs right now.'));
  d.addEventListener('toggle', () => localStorage.setItem('7wd-legend', d.open ? 'open' : 'closed'));
  return d;
}

// ------------------------------------------------------------------ Tabletop Simulator (spec A9)
// While following, the server reads the TTS table (about once a second, every 10 seconds, or on "Read the table now")
// and enters what it shows; the page polls the small /api/tts box and fetches the whole state again when the server's
// change counter moves.

let ttsPoll = null;
const TTS_POLL_MS = 1000;

function watchTts() {
  clearTimeout(ttsPoll);
  if (!view?.tts?.following) return;
  ttsPoll = setTimeout(async () => {
    try {
      const res = await fetch('/api/tts');
      const data = await res.json();
      if (data.changes !== view.changes) { await call('GET', '/api/state'); return; }
      view.tts = data.tts;
      const box = $('tts');
      if (box) box.replaceWith(ttsPanel(view.tts));
    } catch { /* the server is restarting: try again */ }
    watchTts();
  }, TTS_POLL_MS);
}

const ttsCall = (body) => call('POST', '/api/tts', body);

function ago(iso) {
  const s = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
  return s < 60 ? `${s} s ago` : `${Math.floor(s / 60)} min ago`;
}

function ttsPanel(t) {
  const panel = h('div', { class: `panel tts tts-is-${t?.status ?? 'off'}`, id: 'tts' }, h('h2', {}, 'Tabletop Simulator'));
  if (!t) return panel;
  panel.append(h('div', { class: 'tts-controls small' },
    h('label', {}, h('input', { type: 'checkbox', checked: t.following, onchange: (e) => ttsCall({ follow: e.target.checked }) }),
      ' Follow the TTS table'),
    h('label', { class: 'muted', title: 'Auto: the colour you sit at in TTS (you host the game). Choose White or Red to override it.' }, 'You sit ',
      h('select', { onchange: (e) => ttsCall({ color: e.target.value }) },
        [['Auto', t.color === 'Auto' && t.side ? `Auto: ${t.side}` : 'Auto (your TTS seat)'], ['White', 'White'], ['Red', 'Red']]
          .map(([c, label]) => h('option', { value: c, selected: c === t.color }, label)))),
    h('label', { class: 'muted', title: 'Each reading makes TTS pause for a moment. Read less often if moving pieces feels sluggish.' }, 'Read the table ',
      h('select', { onchange: (e) => ttsCall({ pace: e.target.value }) },
        [['live', 'every second'], ['10s', 'every 10 seconds'], ['click', 'only when I click']]
          .map(([p, label]) => h('option', { value: p, selected: p === t.pace }, label))))));
  if (!t.following) {
    panel.append(h('div', { class: 'small muted' }, 'Off: enter everything here by hand. Turn it on to have Moves read from the table.'));
    return panel;
  }
  if (t.pace !== 'live') panel.append(h('div', { class: 'tts-choices' },
    h('button', { class: t.pace === 'click' ? 'btn' : 'btn ghost', title: 'Read the table now and enter what changed (then advice for your turn)',
      onclick: () => call('POST', '/api/tts/read') }, 'Read the table now'),
    h('span', { class: 'small muted' }, t.lastRead ? ` last read ${ago(t.lastRead)}` : ' not read yet')));
  panel.append(h('div', { class: 'small muted' }, t.connected
    ? `Connected · ${t.roundTripMs ?? '?'} ms per reading${t.settled ? '' : ' · something is held or moving'}`
      + `${t.side ? ` · you sit ${t.side}${t.sideDetected ? ' (your TTS seat)' : ''}` : ''}${t.catchingUp ? ' · catching up' : ''}`
    : t.problem ?? 'Connecting…'));
  if (t.connected && t.problem) panel.append(h('div', { class: 'small', style: 'color:var(--err)' }, t.problem));
  if (t.message) panel.append(h('div', { class: 'tts-message' }, t.message));
  if (t.status === 'new-game') panel.append(h('div', { class: 'tts-choices' },
    h('button', { class: 'btn', onclick: async () => { if (await call('POST', '/api/tts/newgame')) { showNewGame = false; render(); } } },
      'Start the new game'),
    h('div', { class: 'small muted' }, t.newGame, ' · ',
      h('a', { href: '#', onclick: (e) => { e.preventDefault(); showNewGame = true; render(); } }, 'other settings…'))));
  if (t.promos?.length) panel.append(h('div', { class: 'tts-choices' }, t.promos.map(p =>
    h('button', { class: 'btn', onclick: () => call('POST', '/api/tts/promo', { promo: p.id }) }, `Enable ${p.name} for this game`))));
  if (t.choices.length) panel.append(h('div', { class: 'tts-choices' }, t.choices.map(c =>
    h('button', { class: 'btn ghost tts-choice', title: 'Enter these lines', onclick: () => call('POST', '/api/tts/choose', { index: c.index }) },
      c.lines.join(' → ')))));
  const list = (title, items) => items.length ? h('div', { class: 'small tts-list' }, h('div', { class: 'muted' }, title),
    h('ul', {}, items.slice(0, 12).map(i => h('li', {}, i)),
      items.length > 12 ? h('li', { class: 'muted' }, `… and ${items.length - 12} more`) : null)) : null;
  panel.append(...[
    list('The table shows, but the log doesn\'t:', t.notInLog),
    list('The log expects, but the table doesn\'t show:', t.notOnTable),
    list('Soft checks (only notes):', t.notes)].filter(Boolean));
  if (t.lastAppended) panel.append(h('div', { class: 'small muted' }, 'Last from the table: ', t.lastAppended));
  return panel;
}

// ------------------------------------------------------------------ render

function render() {
  hoverRow = null; // the hovered row is redrawn, so it no longer has the mouse
  hidePopover();
  hideTip();
  const err = $('error');
  err.hidden = !view?.error;
  err.textContent = view?.error ?? '';
  const note = $('notice');
  note.hidden = !view?.notice;
  note.textContent = view?.notice ?? '';

  const game = view?.game;
  const main = $('main');
  main.replaceChildren();
  $('undo').disabled = !game?.canUndo || showNewGame;
  $('undo').title = view?.play ? "Take back your last Move and the AI's reply (Ctrl+Z)" : 'Remove the last Move and the Reveals after it (Ctrl+Z)';
  $('newgame').hidden = !game || showNewGame;

  if (!game || showNewGame) {
    $('prompt').textContent = 'New game';
    main.className = 'single';
    main.append(newGameScreen(), ttsPanel(view?.tts));
    watchTts();
    return;
  }
  main.className = '';
  $('prompt').textContent = game.prompt;
  main.append(
    h('section', {}, boardPanel(game)),
    h('aside', {}, sidePanel(game)));
  drawMarkers(shownRow(view.advice));
  flashAiMoves(game);
  watchAdvice();
  watchTts();
  watchAi();
}

// ------------------------------------------------------------------ playing against the AI (ticket 36)
// The server deals the cards and runs the AI's search in the background; while the AI thinks, the page asks for the
// state every 300 ms and redraws when the AI has moved.

let aiPoll = null;
const AI_POLL_MS = 300;
const aiTurn = (g) => !!view?.play && !g.result && g.decision && g.decision.player !== g.seat;

function watchAi() {
  clearTimeout(aiPoll);
  if (!view?.play?.thinking) return;
  aiPoll = setTimeout(async () => {
    try {
      const res = await fetch('/api/state');
      const data = await res.json();
      const moved = data.game?.log.length !== view.game?.log.length || data.play?.thinking !== view.play?.thinking
        || data.play?.error !== view.play?.error || !data.game;
      if (moved) { view = data; render(); return; }
    } catch { /* the server is restarting: try again */ }
    watchAi();
  }, AI_POLL_MS);
}

function playPanel(p) {
  const panel = h('div', { class: 'panel play', id: 'play' }, h('h2', {}, 'Playing against the AI'));
  panel.append(h('div', {}, h('b', {}, p.opponent), h('span', { class: 'muted small' },
    ` · P${p.aiSeat + 1}${p.simulations ? ` · ${p.simulations.toLocaleString('en')} simulations per Move` : ''}`)));
  if (p.thinking) panel.append(h('div', { class: 'thinking ai-status' }, h('span', { class: 'spinner' }), p.message));
  else if (p.aiMoves?.length) panel.append(h('div', { class: 'ai-last' }, p.aiMoves.map(m => h('div', {}, m.text))));
  if (p.error) panel.append(h('div', { class: 'small', style: 'color:var(--err)' }, p.error, ' ',
    h('button', { class: 'btn ghost', onclick: () => call('POST', '/api/play/retry') }, 'Ask the AI again')));
  panel.append(h('div', { class: 'tally' }, p.tally.text));
  if (p.takebacks) panel.append(h('div', { class: 'small muted' }, `Takebacks this game: ${p.takebacks} (kept with the result)`));
  panel.append(h('label', { class: 'small muted auto' },
    h('input', { type: 'checkbox', checked: p.advice, onchange: (e) => call('POST', '/api/play/advice', { on: e.target.checked }) }),
    ' Show advice (spoils the test)'));
  panel.append(h('div', { class: 'small muted' }, 'Results are kept in ', p.resultsFile));
  return panel;
}

// A % badge: the Move's (or card's) share of the search; the search's choice in gold.
const badge = (share, top) => h('span', { class: `badge${top ? ' top' : ''}`, title: 'Share of the search' }, sharePct(share));

function boardPanel(g) {
  const panel = h('div', { class: 'panel' });
  if (view.play) panel.append(aiBanner(g));
  const tray = trayFor(g);
  if (tray) panel.append(tray);
  if (g.pyramid) panel.append(h('h2', {}, `Age ${['', 'I', 'II', 'III'][g.age]}`), pyramid(g));
  else if (g.age === 0) panel.append(h('p', { class: 'muted' }, 'Setup: the first player, the Progress tokens and the Wonder draft.'));
  if (g.age > 0) panel.append(track(g.conflict));
  if (g.pantheon) panel.append(pantheonPanel(g));
  if (g.boardTokens.length) panel.append(h('div', { class: 'board-tokens' }, 'Progress tokens on the board: ',
    g.boardTokens.map(t => chip(`token:${t.id}`, t.name))));
  panel.append(h('div', { class: 'cities' }, g.players.map(p => city(p, g))));
  const key = legendPanel(g.pantheonGame);
  if (key) panel.append(key);
  return panel;
}

// The big "AI played" banner (ticket 37): the AI's Moves since your last Move, in plain words, until you move again.
function aiBanner(g) {
  const moves = view.play.aiMoves ?? [];
  const thinking = aiTurn(g);
  const box = h('div', { class: `ai-banner${thinking && !moves.length ? ' thinking' : ''}`, id: 'ai-banner' });
  if (moves.length) box.append(h('div', { class: 'ai-moves' }, moves.map(m => h('div', { class: 'ai-move' }, m.text))));
  if (thinking) box.append(h('div', { class: 'ai-next' }, h('span', { class: 'spinner' }), view.play.message ?? 'The AI is to move'));
  else if (!g.result) box.append(h('div', { class: 'ai-next you' }, 'Your move.'));
  return box;
}

// Highlights what the AI's last Moves touched: the emptied pyramid slot stays outlined while the banner shows, and the
// card, Wonder or token it went to flashes once (not again on every redraw).
let flashedKey = null;
function flashAiMoves(g) {
  const moves = view?.play?.aiMoves ?? [];
  const key = JSON.stringify(moves);
  const fresh = key !== flashedKey;
  flashedKey = key;
  if (!moves.length) return;
  const ai = g.seat === 0 ? 1 : 0;
  for (const m of moves) {
    if (m.slot != null && g.pyramid && g.pyramid.age === m.age) {
      const ghost = document.querySelector(`.pyramid .card[data-slot="${m.slot}"]`);
      if (ghost) { ghost.classList.add('ai-took'); ghost.title = 'The AI took this card'; if (fresh) ghost.classList.add('flash'); }
    }
    if (!fresh) continue;
    const ref = m.wonder ? `wonder:${m.wonder}` : m.card ? `card:${m.card}` : m.token ? `token:${m.token}` : null;
    if (ref) document.querySelectorAll(`.city[data-player="${ai}"] [data-ref="${ref}"]`).forEach(el => el.classList.add('flash'));
  }
}

function sidePanel(g) {
  const play = view.play;
  const log = h('div', { class: `log${play ? ' norewind' : ''}`, id: 'log' },
    g.log.length === 0 ? h('div', { class: 'muted' }, 'No entries yet.') : null,
    g.log.map(l => h('div', {
      class: `${l.kind}${l.player === g.seat && l.kind === 'move' ? ' you' : ''}`,
      title: play ? '' : 'Rewind to just after this line (later lines are removed)',
      onclick: play ? null : () => rewind(l.index + 1, l.text),
    }, h('span', { class: 'n' }, l.index + 1), l.text)));
  const panels = [
    play ? playPanel(play) : ttsPanel(view.tts),
    advicePanel(view.advice),
    h('div', { class: 'panel' }, h('h2', {}, 'Move Log'),
      h('div', { class: 'small muted', style: 'margin-bottom:6px' }, g.rules,
        play ? ' · the program deals the cards; "?" is what the AI keeps private' : ' · click a line to rewind to it'),
      log,
      h('div', { class: 'small muted', style: 'margin-top:6px' }, 'Saved after every entry to ', g.savedTo)),
  ];
  if (g.discard.length) panels.push(h('div', { class: 'panel' }, h('h2', {}, `Discard pile (${g.discard.length})`),
    g.discard.map(c => chip(`card:${c.id}`, c.name))));
  queueMicrotask(() => { const el = $('log'); if (el) el.scrollTop = el.scrollHeight; });
  return panels;
}

// ------------------------------------------------------------------ the pyramid

function pyramid(g) {
  const p = g.pyramid;
  const css = getComputedStyle(document.documentElement);
  const colStep = parseFloat(css.getPropertyValue('--col-step'));
  const rowStep = parseFloat(css.getPropertyValue('--row-step'));
  const cardW = parseFloat(css.getPropertyValue('--card-w'));
  const cardH = parseFloat(css.getPropertyValue('--card-h'));
  const maxCol = Math.max(...p.slots.map(s => s.col));
  const box = h('div', { class: 'pyramid', style: `width:${maxCol * colStep + cardW}px;height:${(p.rows - 1) * rowStep + cardH}px` });
  const guildMode = g.reveal?.kind === 'guilds';
  for (const s of p.slots) {
    const classes = ['card', s.face];
    if (s.card) classes.push(`c-${s.card.color}`);
    const mine = s.actions.length && !aiTurn(g);
    if (mine) classes.push('acc');
    if (s.pulse) classes.push('pulse');
    if (guildMode) classes.push('pick');
    if (guildMode && guildPick.includes(s.name)) classes.push('picked');
    const el = h('div', {
      class: classes.join(' '),
      style: `left:${s.col * colStep}px;top:${s.row * rowStep}px;z-index:${s.row + 1}`,
      title: s.card || s.face === 'empty' ? null : `slot ${s.name}: face down`,
      'data-slot': s.slot,
      'data-ref': s.card ? `card:${s.card.id}` : null,
    }, h('span', { class: 'slotname' }, s.name), s.card ? cardFace(s, p.pricePlayer, s.accessible) : null,
      s.token ? h('span', { class: `slot-token tok-${s.token.startsWith('offering') ? 'offering' : s.token}`, title: `${s.tokenName}: whoever takes this card (or Zeus) takes it` }, s.tokenName) : null,
      s.face === 'temple' ? h('span', { class: 'temple-back' }, '🛕') : null);
    if (s.card) el._prices = s.prices;
    const b = mine ? view.advice?.badges.find(x => x.slot === s.slot) : null;
    if (b) el.append(badge(b.share, b.top));
    if (mine) el.addEventListener('click', (e) => cardMenu(e, s));
    else if (s.pulse) el.addEventListener('click', () => slotPicker(s));
    else if (guildMode) el.addEventListener('click', () => {
      guildPick = guildPick.includes(s.name) ? guildPick.filter(x => x !== s.name)
        : guildPick.length < g.reveal.count ? [...guildPick, s.name] : guildPick;
      render();
    });
    box.append(el);
  }
  // Another Pending Decision (token, draft, destroy, Statue, ...) is answered on the tiles above: the board dims.
  const dim = g.decision?.tiles.length > 0;
  return h('div', { class: `pyramid-wrap${dim ? ' dim' : ''}` }, box);
}

// A face-up card in the pyramid: name, effect icons, cost icons and chain; an accessible card also shows its price now.
function cardFace(s, pricePlayer, accessible) {
  const i = info(`card:${s.card.id}`);
  const face = [h('div', { class: 'cname' }, s.card.name)];
  if (!i) return face;
  face.push(h('div', { class: 'cbody' },
    h('div', { class: 'ceff' }, i.effectIcons),
    h('div', { class: 'ccost' }, h('span', { class: 'cc' }, i.costIcons || 'free'),
      i.chainFrom ? h('span', { class: 'cchain' }, `🔗 ${i.chainFrom}`) : null)));
  const price = accessible ? s.prices?.[pricePlayer] : null;
  if (price) face.push(h('div', { class: `cprice${price.chain ? ' chain' : ''}${price.affordable ? '' : ' cant'}` }, priceText(price)));
  return face;
}

// 'you pay 3🪙', 'AI pays 5🪙', 'free for you', '🔗 free for the AI'.
function priceText(p) {
  if (p.total > 0) return `${p.who === 'you' ? 'you pay' : `${p.who} pays`} ${p.short}`;
  return `${p.chain ? '🔗 ' : ''}free for ${p.who === 'AI' ? 'the AI' : p.who}`;
}

function cardMenu(event, slot) {
  const pop = $('popover');
  hideTip();
  pop.replaceChildren(infoBlock(`card:${slot.card.id}`, slot.prices) ?? h('div', { class: 'ptitle' }, slot.card.name),
    h('div', { class: 'ptitle' }, 'Take it:'));
  for (const a of slot.actions) {
    const share = a.enabled ? shareOf(a.line) : null;
    pop.append(h('button', { disabled: !a.enabled, onclick: () => enter(a.line), class: `act-${a.kind}` }, a.label,
      a.refs?.length ? h('span', { class: 'menu-icons' }, info(a.refs[0])?.effectIcons) : null,
      share === null ? null : badge(share, isBestKey(a.line))));
    if (!a.enabled && a.reason) pop.append(h('div', { class: 'why' }, a.reason));
  }
  pop.hidden = false;
  const r = event.currentTarget.getBoundingClientRect();
  pop.style.left = `${Math.max(8, Math.min(r.left, window.innerWidth - pop.offsetWidth - 8))}px`;
  pop.style.top = `${Math.max(8, Math.min(r.bottom + 6, window.innerHeight - pop.offsetHeight - 8))}px`;
  event.stopPropagation();
}
function hidePopover() { $('popover').hidden = true; }

// A filterable picker for one face-down card: the unseen cards are offered, cards already revealed are refused with where they are.
function slotPicker(slot) {
  const filter = h('input', { type: 'text', placeholder: 'Type to filter, Enter picks the first match' });
  const list = h('div', { class: 'list' });
  const modal = $('modal');
  const close = () => { modal.hidden = true; modal.replaceChildren(); };
  const draw = () => {
    const q = filter.value.trim().toLowerCase();
    const shown = slot.candidates.filter(c => c.name.toLowerCase().includes(q) || c.id.includes(q));
    let first = true;
    list.replaceChildren(...shown.map(c => {
      const b = h('button', { disabled: !c.enabled, class: c.enabled && first ? 'first' : '', onclick: () => { close(); enter(`${slot.revealPrefix} ${c.id}`); } },
        h('span', { class: `tile-dot dot c-${c.color}`, style: 'display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px' }),
        c.name, ' ', iconLine(c.ref), c.reason ? h('span', { class: 'why' }, c.reason) : null);
      if (c.enabled) first = false;
      return b;
    }));
    if (shown.length === 0) list.append(h('div', { class: 'muted', style: 'padding:6px 8px' }, 'No card matches.'));
  };
  filter.addEventListener('input', draw);
  filter.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') close();
    if (e.key === 'Enter') { const b = list.querySelector('button:not(:disabled)'); if (b) b.click(); }
  });
  modal.replaceChildren(h('div', { class: 'dialog', onclick: (e) => e.stopPropagation() },
    h('h3', {}, `Which card is in slot ${slot.name}?`), filter, list,
    h('div', { class: 'row' }, h('button', { class: 'btn ghost', onclick: close }, 'Cancel'))));
  modal.onclick = close;
  modal.hidden = false;
  draw();
  filter.focus();
}

// ------------------------------------------------------------------ the tray: Reveals, other decisions, the result

function trayFor(g) {
  if (g.result) return resultTray(g);
  if (g.reveal && g.reveal.kind !== 'slots') return revealTray(g.reveal);
  if (g.reveal) return h('div', { class: 'tray' }, h('div', { class: 'title' }, g.reveal.title),
    h('div', { class: 'small muted' }, 'Nothing else can be entered until the pulsing cards are named.'));
  if (g.decision && aiTurn(g)) return h('div', { class: 'tray' }, h('div', { class: 'title' }, g.decision.title),
    h('div', { class: 'small muted' }, 'The AI is deciding; its choice appears here and in the Move Log.'));
  if (g.decision && g.decision.tiles.length) return h('div', { class: 'tray' },
    h('div', { class: 'title' }, g.decision.title),
    h('div', { class: 'tiles' }, g.decision.tiles.map(t => {
      const key = t.then ? `${t.line}\n${t.then}` : t.line;  // a Statue pair: both lines
      const share = shareOf(key);
      const cost = !['mausoleum', 'messe', 'destroy', 'libertykeep', 'libertygive'].includes(t.kind);
      return h('button', { class: `tile${t.refs?.length ? ' rich' : ''}`, onclick: () => enter(t.line, t.then),
        'data-ref': t.refs?.length === 1 ? t.refs[0] : null },
        h('span', { class: 'tile-label' }, t.label, share === null ? null : badge(share, isBestKey(key))),
        t.refs?.length === 1 ? h('span', { class: 'tile-icons' }, iconLine(t.refs[0], { cost })) : null,
        t.refs?.length === 2 ? h('span', { class: 'tile-icons pair' }, t.refs.map(r => h('span', { 'data-ref': r }, info(r)?.effectIcons))) : null);
    })));
  return null;
}

function revealTray(r) {
  const tray = h('div', { class: 'tray' }, h('div', { class: 'title' }, r.title));
  if (r.kind === 'fields') {
    // A Pantheon Reveal form: one choice per field, written in field order after the prefix ("1.0 greek 1.2 roman ...").
    const fields = r.fields ?? [];
    fields.forEach((f, i) => {
      if (fieldPick[i] && !f.options.some(o => o.id === fieldPick[i] && o.enabled)) delete fieldPick[i];
      const only = f.options.filter(o => o.enabled);
      if (!fieldPick[i] && only.length === 1) fieldPick[i] = only[0].id;
    });
    tray.append(h('div', { class: 'fields' }, fields.map((f, i) => h('div', { class: 'field' },
      h('div', { class: 'field-label small' }, f.label),
      h('div', { class: 'tiles' }, f.options.map(o => h('button', {
        class: `tile${o.ref ? ' rich' : ''}${fieldPick[i] === o.id ? ' sel' : ''}`, disabled: !o.enabled, title: o.reason ?? '', 'data-ref': o.ref,
        onclick: () => { fieldPick[i] = o.id; render(); },
      }, h('span', { class: 'tile-label' }, o.name), o.ref ? h('span', { class: 'tile-icons' }, iconLine(o.ref)) : null)))))));
    const done = fields.every((f, i) => fieldPick[i]);
    const line = `${r.linePrefix} ${fields.map((f, i) => f.key ? `${f.key} ${fieldPick[i]}` : fieldPick[i]).join(' ')}`;
    tray.append(h('div', { class: 'tray-actions' },
      h('span', { class: 'muted small' }, `${fields.filter((f, i) => fieldPick[i]).length} of ${fields.length} chosen`),
      h('button', { class: 'btn', disabled: !done, onclick: () => enter(line) }, 'Confirm')));
    return tray;
  }
  if (r.kind === 'guilds') {
    tray.append(h('div', { class: 'tray-actions' },
      h('span', { class: 'muted small' }, `Chosen: ${guildPick.join(', ') || 'none'} (${guildPick.length} of ${r.count})`),
      h('button', { class: 'btn', disabled: guildPick.length !== r.count, onclick: () => enter(`${r.linePrefix} ${guildPick.join(' ')}`) }, 'Confirm'),
      guildPick.length ? h('button', { class: 'btn ghost', onclick: () => { guildPick = []; render(); } }, 'Clear') : null));
    return tray;
  }
  if (r.count === 1) {
    tray.append(h('div', { class: 'tiles' }, r.options.map(o =>
      h('button', { class: `tile${o.ref ? ' rich' : ''}`, disabled: !o.enabled, title: o.reason ?? '', 'data-ref': o.ref, onclick: () => enter(`${r.linePrefix} ${o.id}`) },
        h('span', { class: 'tile-label' }, o.name), o.ref ? h('span', { class: 'tile-icons' }, iconLine(o.ref)) : null))));
    return tray;
  }
  multiPick = multiPick.filter(id => r.options.some(o => o.id === id && o.enabled));
  tray.append(h('div', { class: 'tiles' }, r.options.map(o => h('button', {
    class: `tile${o.ref ? ' rich' : ''}${multiPick.includes(o.id) ? ' sel' : ''}`, disabled: !o.enabled, title: o.reason ?? '',
    onclick: () => {
      multiPick = multiPick.includes(o.id) ? multiPick.filter(x => x !== o.id)
        : multiPick.length < r.count ? [...multiPick, o.id] : multiPick;
      render();
    },
    'data-ref': o.ref,
  }, h('span', { class: 'tile-label' }, o.color ? h('span', { class: `dot c-${o.color}` }) : null, o.name),
    o.ref ? h('span', { class: 'tile-icons' }, iconLine(o.ref)) : null))));
  tray.append(h('div', { class: 'tray-actions' },
    h('span', { class: 'muted small' }, `${multiPick.length} of ${r.count} chosen`),
    h('button', { class: 'btn', disabled: multiPick.length !== r.count, onclick: () => enter(`${r.linePrefix} ${multiPick.join(' ')}`) }, 'Confirm')));
  return tray;
}

function resultTray(g) {
  const r = g.result;
  const name = (p) => g.players[p].label;
  const play = view.play;
  const headline = !play ? (r.winner < 0 ? 'A draw' : `${name(r.winner)} wins by ${r.victory}`)
    : r.winner < 0 ? 'A draw' : r.winner === g.seat ? `You win by ${r.victory}!` : `The AI wins by ${r.victory}`;
  return h('div', { class: 'tray' },
    h('div', { class: 'title' }, headline),
    play ? h('div', { class: 'tally' }, play.tally.text,
      h('span', { class: 'muted small' }, ' · New game to play again')) : null,
    h('table', { class: 'score' },
      h('tr', {}, h('th', {}, 'Score sheet'), h('th', {}, name(0)), h('th', {}, name(1))),
      r.scores.map(s => h('tr', {}, h('td', {}, s.category), h('td', {}, s.p1), h('td', {}, s.p2))),
      h('tr', {}, h('th', {}, 'Total'), h('th', {}, r.p1Total), h('th', {}, r.p2Total))));
}

// ------------------------------------------------------------------ track and cities

function track(c) {
  const cells = [];
  for (let x = -9; x <= 9; x++) {
    const tok = c.tokens.find(t => t.left && (t.victim === 0 ? -t.zone : t.zone) === x);
    cells.push(h('span', {
      class: `${Math.abs(x) === 9 ? 'cap' : ''}${x === c.pawn ? ' pawn' : ''}${tok ? ' tok' : ''}${x === c.minerva ? ' minerva' : ''}`,
      'data-tok': tok ? `-${tok.coins}` : null,
      title: x === c.pawn ? 'Conflict Pawn' : x === c.minerva ? 'Minerva: the Conflict pawn stops before her' : '',
    }, x === c.pawn ? '◆' : x === c.minerva ? '🦉' : ''));
  }
  return h('div', {}, h('div', { class: 'track', title: 'Conflict track: P1 capital on the left, P2 on the right' }, cells));
}

function city(p, g) {
  const wonders = p.wonders.map(w => {
    const i = info(`wonder:${w.id}`);
    const price = w.prices?.[0];
    const el = h('div', { class: `wonder${w.built ? ' built' : ''}${w.removed ? ' removed' : ''}`, 'data-ref': `wonder:${w.id}` },
      h('span', { class: 'wname' }, w.built ? '🏛️ ' : '', w.name),
      h('span', { class: 'weff' }, i?.effectIcons ?? w.summary),
      h('span', { class: 'wcost' }, w.built ? 'built' : w.removed ? 'removed'
        : [h('span', {}, i?.costIcons ?? ''), price ? h('span', { class: `wprice${price.affordable ? '' : ' cant'}` }, priceText(price)) : null]));
    if (price) el._prices = w.prices;
    return el;
  });
  return h('div', { class: `city${p.toDecide ? ' todecide' : ''}`, 'data-player': p.index },
    h('div', { class: 'head' }, h('b', {}, p.label, p.first ? ' · first' : ''), h('span', {}, `${p.coins} coins · ${p.vp} VP`)),
    p.production ? h('div', { class: 'muted' }, 'Production: ', p.production) : null,
    p.science.length ? h('div', { class: 'muted' }, 'Science: ', p.science.join(', ')) : null,
    h('div', { style: 'margin:4px 0' }, p.cards.length ? p.cards.map(c => chip(`card:${c.id}`, c.name)) : h('span', { class: 'muted' }, 'No cards yet')),
    p.tokens.length ? h('div', {}, p.tokens.map(t => chip(`token:${t.id}`, t.name))) : null,
    g.pantheon ? holdings(g.pantheon.players[p.index]) : null,
    wonders);
}

// ------------------------------------------------------------------ Pantheon (spec-pantheon P-A9)

// A player's Pantheon holdings: Mythology and Offering tokens, Astarte's coins, the Divinities they activated.
function holdings(hd) {
  const parts = [];
  if (hd.mythologyTokens.length) parts.push(h('span', {}, 'Mythology tokens: ', hd.mythologyTokens.map(m => h('span', { class: `chip light myth tok-${m.toLowerCase()}` }, m))));
  if (hd.offerings.length) parts.push(h('span', {}, ' Offerings: ', hd.offerings.map(o => h('span', { class: 'chip light' }, o)), ` (worth ${hd.offeringValue}🪙)`));
  if (hd.astarte) parts.push(h('span', { title: "Astarte's coins: spent after the treasury, never lost, 1 VP each at the end" }, ` Astarte: ${hd.astarte}🪙`));
  if (hd.activated.length) parts.push(h('div', { class: 'muted small' }, 'Activated: ', hd.activated.join(', ')));
  return parts.length ? h('div', { class: 'pan-hold small' }, parts) : null;
}

// The Pantheon board: 6 spaces (space 1 at P1's capital end) with both players' costs, face-down Mythologies, the
// Gate, and an Activate button while the player to decide may activate instead of taking a card.
function pantheonPanel(g) {
  const pan = g.pantheon;
  const mine = !aiTurn(g);
  const spaces = pan.spaces.map(sp => {
    const cls = ['pspace', sp.face, sp.mythology ? `myth-${sp.mythology}` : ''].join(' ');
    const body = [];
    if (sp.face === 'empty') body.push(h('div', { class: 'muted small' }, 'empty'));
    else if (sp.face === 'hidden') body.push(h('div', { class: 'pname' }, `${sp.mythologyName}`), h('div', { class: 'muted small' }, `face down (P${sp.placer + 1}'s, unseen)`));
    else if (sp.face === 'down') body.push(h('div', { class: 'pname', 'data-ref': `divinity:${sp.id}` }, sp.name), h('div', { class: 'muted small' }, `face down (P${sp.placer + 1}'s)`));
    else body.push(h('div', { class: 'pname', 'data-ref': `divinity:${sp.id}` }, sp.name, ' ', h('span', { class: 'icons' }, info(`divinity:${sp.id}`)?.effectIcons ?? '')),
      h('div', { class: 'muted small' }, sp.face === 'gate' ? 'the Gate (double cost)' : sp.mythologyName));
    if (sp.costsNow) body.push(h('div', { class: 'small' }, `now: P1 ${sp.costsNow[0]}🪙 · P2 ${sp.costsNow[1]}🪙`));
    if (sp.activate && mine) {
      const share = sp.activate.enabled ? shareOf(sp.activate.line) : null;
      body.push(h('button', { class: 'btn act-activate', disabled: !sp.activate.enabled, title: sp.activate.reason ?? '', onclick: () => enter(sp.activate.line) },
        sp.activate.label, share === null ? null : badge(share, isBestKey(sp.activate.line))));
    }
    return h('div', { class: cls },
      h('div', { class: 'pcost small' }, h('span', { title: "P1's cost" }, `P1 ${sp.costs[0]}`), h('b', {}, sp.number), h('span', { title: "P2's cost" }, `P2 ${sp.costs[1]}`)),
      body);
  });
  const extra = [];
  extra.push(h('div', { class: 'small muted' }, 'Decks (top first): ', pan.decks.map(d => `${d.name} [${d.cards.join(', ') || 'empty'}]`).join(' · ')));
  if (pan.drawn.length) extra.push(h('div', { class: 'small' }, 'Drawn: ', pan.drawn.join(', ')));
  if (pan.enki.length) extra.push(h('div', { class: 'small' }, "On Enki: ", pan.enki.join(', ')));
  if (pan.snake) extra.push(h('div', { class: 'small' }, `🐍 Nisaba's Snake is on ${pan.snake}`));
  return h('div', { class: 'pantheon' }, h('h2', {}, 'Pantheon ', h('span', { class: 'small muted' }, "space 1 is at P1's capital end")),
    h('div', { class: 'pspaces' }, spaces), extra);
}

// ------------------------------------------------------------------ new game, undo, rewind

// The opponents offered for "Play against the AI" and the remembered choices (GET /api/play/opponents), loaded with
// the new-game screen.
let opponents = null;
async function loadOpponents() {
  try {
    opponents = await (await fetch('/api/play/opponents')).json();
  } catch {
    opponents = { runsFolder: null, opponents: [], settings: null };
  }
  if (!view?.game || showNewGame) render();
}

const CUSTOM = '__custom__';

function newGameScreen() {
  const remembered = opponents?.settings;
  const s = view?.settings ?? remembered ?? { messe: view?.game?.messe ?? false, liberty: view?.game?.liberty ?? false, rule2015: view?.game?.rule2015 ?? false, seat: view?.game?.seat ?? 0 };
  const messe = h('input', { type: 'checkbox', checked: s.messe });
  const liberty = h('input', { type: 'checkbox', checked: s.liberty });
  const pantheon = h('input', { type: 'checkbox', id: 'pantheon', checked: !!s.pantheon });
  const ruleNew = h('input', { type: 'radio', name: 'rule', checked: !s.rule2015 });
  const rule2015 = h('input', { type: 'radio', name: 'rule', checked: s.rule2015 });
  const seat1 = h('input', { type: 'radio', name: 'seat', checked: s.seat !== 1 && !(s.vsAi && s.randomSeat) });
  const seat2 = h('input', { type: 'radio', name: 'seat', checked: s.seat === 1 && !(s.vsAi && s.randomSeat) });
  const seatRandom = h('input', { type: 'radio', name: 'seat', checked: !!(s.vsAi && s.randomSeat) });
  const modeHand = h('input', { type: 'radio', name: 'mode', id: 'mode-hand', checked: !s.vsAi });
  const modeAi = h('input', { type: 'radio', name: 'mode', id: 'mode-ai', checked: !!s.vsAi });

  // The opponent: a Run's best network, a Generation, the MCTS Anchor, the Heuristic, Random, or any Agent spec. With
  // Pantheon ticked, only networks trained for Pantheon are listed.
  const select = h('select', { id: 'opponent' });
  const fillOpponents = (initial) => {
    const list = (pantheon.checked ? opponents?.pantheonOpponents : opponents?.opponents) ?? [];
    const want = initial ? s.opponent : select.value;
    const groups = new Map();
    for (const o of list) {
      if (!groups.has(o.group)) groups.set(o.group, h('optgroup', { label: o.group }));
      groups.get(o.group).append(h('option', { value: o.spec, 'data-label': o.label }, o.text));
    }
    select.replaceChildren(...groups.values(), h('option', { value: CUSTOM }, 'Other: type an Agent spec…'));
    if (want && list.some(o => o.spec === want)) select.value = want;
    else if (initial && want) select.value = CUSTOM;
    else select.value = list[0]?.spec ?? CUSTOM;
  };
  fillOpponents(true);
  const custom = h('input', { type: 'text', id: 'opponent-spec', placeholder: 'e.g. neural:C:\\7wd-runs\\pilot-1-small\\net\\gen-0003.onnx,mode=classic',
    value: s.opponent && select.value === CUSTOM ? s.opponent : '', style: 'width:100%' });
  const sims = h('input', { type: 'number', id: 'ai-sims', min: 1, step: 100, value: s.aiSimulations ?? 800, style: 'width:7em' });
  const aiBox = h('fieldset', { class: 'ai-settings' }, h('legend', {}, 'The AI opponent'),
    h('label', {}, 'Opponent ', select),
    h('div', { class: 'custom-spec' }, custom),
    h('label', {}, 'Think time: ', sims, ' simulations per Move ',
      h('span', { class: 'small muted' }, '(networks and MCTS; 800 is the Matches\' setting, more is stronger and slower)')),
    h('div', { class: 'small muted' }, opponents?.runsFolder ? `Networks are listed from ${opponents.runsFolder}.` : ''),
    h('div', { class: 'small muted' }, 'The program deals every card; you see only what your seat may see. Advice is off unless you turn it on.'));
  const syncAi = () => {
    const ai = modeAi.checked;
    aiBox.hidden = !ai;
    seatRandom.disabled = !ai;
    if (!ai && seatRandom.checked) seat1.checked = true;
    custom.parentElement.hidden = select.value !== CUSTOM;
  };
  modeHand.addEventListener('change', syncAi);
  modeAi.addEventListener('change', syncAi);
  select.addEventListener('change', syncAi);
  pantheon.addEventListener('change', () => { fillOpponents(false); syncAi(); });

  const start = async () => {
    const body = { messe: messe.checked, liberty: liberty.checked, pantheon: pantheon.checked, rule2015: rule2015.checked, seat: seat2.checked ? 1 : 0 };
    if (modeAi.checked) {
      const isCustom = select.value === CUSTOM;
      Object.assign(body, {
        vsAi: true,
        opponent: isCustom ? custom.value.trim() : select.value,
        opponentLabel: isCustom ? custom.value.trim() : select.selectedOptions[0]?.dataset.label,
        aiSimulations: Math.max(1, parseInt(sims.value, 10) || 800),
        randomSeat: seatRandom.checked,
      });
    }
    const button = $('start');
    button.disabled = true;
    button.textContent = 'Starting…';
    const ok = await call('POST', '/api/new', body);
    if (ok) { showNewGame = false; render(); }
  };
  const screen = h('div', { class: 'panel newgame' },
    h('h2', {}, 'New game'),
    h('fieldset', {}, h('legend', {}, 'What kind of game'),
      h('label', {}, modeHand, ' Enter a game by hand (a real game, or TTS), with advice'),
      h('label', {}, modeAi, ' Play against the AI (here, in this window)')),
    aiBox,
    h('fieldset', {}, h('legend', {}, 'Promo Wonders in the pool'),
      h('label', {}, messe, ' The Messe'), h('label', {}, liberty, ' Statue of Liberty')),
    h('fieldset', {}, h('legend', {}, 'Expansion'),
      h('label', {}, pantheon, ' Pantheon (Divinities, Mythology and Offering tokens, Grand Temples)')),
    h('fieldset', {}, h('legend', {}, 'Who starts the next Age when military is tied'),
      h('label', {}, ruleNew, ' The player who took the last card chooses (current rule)'),
      h('label', {}, rule2015, ' The player who took the last card starts (2015 rule)')),
    h('fieldset', {}, h('legend', {}, 'Your seat'),
      h('label', {}, seat1, ' P1'), h('label', {}, seat2, ' P2'), h('label', {}, seatRandom, ' Random (against the AI)'),
      h('div', { class: 'small muted' }, 'P1 and P2 are only labels; who plays first is drawn (against the AI) or entered next.')),
    h('div', { class: 'row', style: 'display:flex;gap:8px;justify-content:flex-end' },
      view?.game ? h('button', { class: 'btn ghost', onclick: () => { showNewGame = false; render(); } }, 'Back to the game') : null,
      h('button', { class: 'btn', id: 'start', onclick: start }, view?.game ? 'Start (this game is saved and closed)' : 'Start')));
  syncAi();
  return screen;
}

function rewind(count, text) {
  const later = view.game.log.length - count;
  if (later === 0) return;
  if (confirm(`Rewind to just after line ${count} (${text})?\nThe ${later} later line${later > 1 ? 's are' : ' is'} removed.`))
    call('POST', '/api/rewind', { count });
}

$('undo').addEventListener('click', () => call('POST', '/api/undo'));
$('newgame').addEventListener('click', () => { showNewGame = true; render(); loadOpponents(); });
document.addEventListener('click', hidePopover);
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') hidePopover();
  if ((e.ctrlKey || e.metaKey) && e.key === 'z' && $('modal').hidden && !showNewGame && view?.game?.canUndo) {
    e.preventDefault();
    call('POST', '/api/undo');
  }
});

loadCatalog().then(() => call('GET', '/api/state'));
loadOpponents();
