// The Advisor Window page (spec A8). It holds no game rules: the server sends the whole view (/api/state), and each
// click posts one Move Log line that the server checks, appends and replays. Advice is searched on the server in the
// background; the page polls for it and draws the % badges and the compare table.

const $ = (id) => document.getElementById(id);
let view = null;          // the last StateResponse
let showNewGame = false;  // the new-game screen was opened over a game in progress
let guildPick = [];       // slots chosen for the Age III Guild back pattern
let multiPick = [];       // options chosen in a multi-pick Reveal (tokens, draft, library, returned cards)

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
  guildPick = []; multiPick = [];
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


// ------------------------------------------------------------------ Tabletop Simulator (spec A9)
// While following, the server reads the TTS table about once a second and enters what it shows; the page polls the
// small /api/tts box and fetches the whole state again when the server's change counter moves.

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

function ttsPanel(t) {
  const panel = h('div', { class: `panel tts tts-is-${t?.status ?? 'off'}`, id: 'tts' }, h('h2', {}, 'Tabletop Simulator'));
  if (!t) return panel;
  panel.append(h('div', { class: 'tts-controls small' },
    h('label', {}, h('input', { type: 'checkbox', checked: t.following, onchange: (e) => ttsCall({ follow: e.target.checked }) }),
      ' Follow the TTS table'),
    h('label', { class: 'muted', title: 'Auto: the colour you sit at in TTS (you host the game). Choose White or Red to override it.' }, 'You sit ',
      h('select', { onchange: (e) => ttsCall({ color: e.target.value }) },
        [['Auto', t.color === 'Auto' && t.side ? `Auto: ${t.side}` : 'Auto (your TTS seat)'], ['White', 'White'], ['Red', 'Red']]
          .map(([c, label]) => h('option', { value: c, selected: c === t.color }, label))))));
  if (!t.following) {
    panel.append(h('div', { class: 'small muted' }, 'Off: enter everything here by hand. Turn it on to have Moves read from the table.'));
    return panel;
  }
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
  watchAdvice();
  watchTts();
}

// A % badge: the Move's (or card's) share of the search; the search's choice in gold.
const badge = (share, top) => h('span', { class: `badge${top ? ' top' : ''}`, title: 'Share of the search' }, sharePct(share));

function boardPanel(g) {
  const panel = h('div', { class: 'panel' });
  const tray = trayFor(g);
  if (tray) panel.append(tray);
  if (g.pyramid) panel.append(h('h2', {}, `Age ${['', 'I', 'II', 'III'][g.age]}`), pyramid(g));
  else if (g.age === 0) panel.append(h('p', { class: 'muted' }, 'Setup: the first player, the Progress tokens and the Wonder draft.'));
  if (g.age > 0) panel.append(track(g.conflict));
  if (g.boardTokens.length) panel.append(h('div', { class: 'board-tokens' }, 'Progress tokens on the board: ',
    g.boardTokens.map(t => h('span', { class: 'chip light' }, t.name))));
  panel.append(h('div', { class: 'cities' }, g.players.map(p => city(p, g))));
  return panel;
}

function sidePanel(g) {
  const log = h('div', { class: 'log', id: 'log' },
    g.log.length === 0 ? h('div', { class: 'muted' }, 'No entries yet.') : null,
    g.log.map(l => h('div', {
      class: `${l.kind}${l.player === g.seat && l.kind === 'move' ? ' you' : ''}`,
      title: 'Rewind to just after this line (later lines are removed)',
      onclick: () => rewind(l.index + 1, l.text),
    }, h('span', { class: 'n' }, l.index + 1), l.text)));
  const panels = [
    ttsPanel(view.tts),
    advicePanel(view.advice),
    h('div', { class: 'panel' }, h('h2', {}, 'Move Log'),
      h('div', { class: 'small muted', style: 'margin-bottom:6px' }, g.rules, ' · click a line to rewind to it'),
      log,
      h('div', { class: 'small muted', style: 'margin-top:6px' }, 'Saved after every entry to ', g.savedTo)),
  ];
  if (g.discard.length) panels.push(h('div', { class: 'panel' }, h('h2', {}, `Discard pile (${g.discard.length})`),
    g.discard.map(c => h('span', { class: `chip c-${c.color}` }, c.name))));
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
    if (s.actions.length) classes.push('acc');
    if (s.pulse) classes.push('pulse');
    if (guildMode) classes.push('pick');
    if (guildMode && guildPick.includes(s.name)) classes.push('picked');
    const el = h('div', {
      class: classes.join(' '),
      style: `left:${s.col * colStep}px;top:${s.row * rowStep}px;z-index:${s.row + 1}`,
      title: s.card ? s.card.name : `slot ${s.name}`,
      'data-slot': s.slot,
    }, h('span', { class: 'slotname' }, s.name), s.card ? s.card.name : '');
    const b = s.actions.length ? view.advice?.badges.find(x => x.slot === s.slot) : null;
    if (b) el.append(badge(b.share, b.top));
    if (s.actions.length) el.addEventListener('click', (e) => cardMenu(e, s));
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

function cardMenu(event, slot) {
  const pop = $('popover');
  pop.replaceChildren(h('div', { class: 'ptitle' }, slot.card.name));
  for (const a of slot.actions) {
    const share = a.enabled ? shareOf(a.line) : null;
    pop.append(h('button', { disabled: !a.enabled, onclick: () => enter(a.line) }, a.label,
      share === null ? null : badge(share, isBestKey(a.line))));
    if (!a.enabled && a.reason) pop.append(h('div', { class: 'why' }, a.reason));
  }
  pop.hidden = false;
  const r = event.currentTarget.getBoundingClientRect();
  pop.style.left = `${Math.min(r.left, window.innerWidth - 260)}px`;
  pop.style.top = `${Math.min(r.bottom + 6, window.innerHeight - pop.offsetHeight - 8)}px`;
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
        c.name, c.reason ? h('span', { class: 'why' }, c.reason) : null);
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
  if (g.decision && g.decision.tiles.length) return h('div', { class: 'tray' },
    h('div', { class: 'title' }, g.decision.title),
    h('div', { class: 'tiles' }, g.decision.tiles.map(t => {
      const key = t.then ? `${t.line}\n${t.then}` : t.line;  // a Statue pair: both lines
      const share = shareOf(key);
      return h('button', { class: 'tile', onclick: () => enter(t.line, t.then) }, t.label, share === null ? null : badge(share, isBestKey(key)));
    })));
  return null;
}

function revealTray(r) {
  const tray = h('div', { class: 'tray' }, h('div', { class: 'title' }, r.title));
  if (r.kind === 'guilds') {
    tray.append(h('div', { class: 'tray-actions' },
      h('span', { class: 'muted small' }, `Chosen: ${guildPick.join(', ') || 'none'} (${guildPick.length} of ${r.count})`),
      h('button', { class: 'btn', disabled: guildPick.length !== r.count, onclick: () => enter(`${r.linePrefix} ${guildPick.join(' ')}`) }, 'Confirm'),
      guildPick.length ? h('button', { class: 'btn ghost', onclick: () => { guildPick = []; render(); } }, 'Clear') : null));
    return tray;
  }
  if (r.count === 1) {
    tray.append(h('div', { class: 'tiles' }, r.options.map(o =>
      h('button', { class: 'tile', disabled: !o.enabled, title: o.reason ?? '', onclick: () => enter(`${r.linePrefix} ${o.id}`) }, o.name))));
    return tray;
  }
  multiPick = multiPick.filter(id => r.options.some(o => o.id === id && o.enabled));
  tray.append(h('div', { class: 'tiles' }, r.options.map(o => h('button', {
    class: `tile${multiPick.includes(o.id) ? ' sel' : ''}`, disabled: !o.enabled, title: o.reason ?? '',
    onclick: () => {
      multiPick = multiPick.includes(o.id) ? multiPick.filter(x => x !== o.id)
        : multiPick.length < r.count ? [...multiPick, o.id] : multiPick;
      render();
    },
  }, o.color ? h('span', { class: `dot c-${o.color}` }) : null, o.name))));
  tray.append(h('div', { class: 'tray-actions' },
    h('span', { class: 'muted small' }, `${multiPick.length} of ${r.count} chosen`),
    h('button', { class: 'btn', disabled: multiPick.length !== r.count, onclick: () => enter(`${r.linePrefix} ${multiPick.join(' ')}`) }, 'Confirm')));
  return tray;
}

function resultTray(g) {
  const r = g.result;
  const name = (p) => g.players[p].label;
  return h('div', { class: 'tray' },
    h('div', { class: 'title' }, r.winner < 0 ? 'A draw' : `${name(r.winner)} wins by ${r.victory}`),
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
      class: `${Math.abs(x) === 9 ? 'cap' : ''}${x === c.pawn ? ' pawn' : ''}${tok ? ' tok' : ''}`,
      'data-tok': tok ? `-${tok.coins}` : null,
      title: x === c.pawn ? 'Conflict Pawn' : '',
    }, x === c.pawn ? '◆' : ''));
  }
  return h('div', {}, h('div', { class: 'track', title: 'Conflict track: P1 capital on the left, P2 on the right' }, cells));
}

function city(p, g) {
  const wonders = p.wonders.map(w => h('div', { class: `wonder${w.built ? ' built' : ''}${w.removed ? ' removed' : ''}`, title: w.summary },
    h('span', {}, w.name), h('span', { class: 'muted' }, w.built ? `built (Age ${w.card.age})` : w.removed ? 'removed' : w.summary)));
  return h('div', { class: `city${p.toDecide ? ' todecide' : ''}` },
    h('div', { class: 'head' }, h('b', {}, p.label, p.first ? ' · first' : ''), h('span', {}, `${p.coins} coins · ${p.vp} VP`)),
    p.production ? h('div', { class: 'muted' }, 'Production: ', p.production) : null,
    p.science.length ? h('div', { class: 'muted' }, 'Science: ', p.science.join(', ')) : null,
    h('div', { style: 'margin:4px 0' }, p.cards.length ? p.cards.map(c => h('span', { class: `chip c-${c.color}` }, c.name)) : h('span', { class: 'muted' }, 'No cards yet')),
    p.tokens.length ? h('div', {}, p.tokens.map(t => h('span', { class: 'chip light' }, '● ', t.name))) : null,
    wonders);
}

// ------------------------------------------------------------------ new game, undo, rewind

function newGameScreen() {
  const s = view?.settings ?? { messe: view?.game?.messe ?? false, liberty: view?.game?.liberty ?? false, rule2015: view?.game?.rule2015 ?? false, seat: view?.game?.seat ?? 0 };
  const messe = h('input', { type: 'checkbox', checked: s.messe });
  const liberty = h('input', { type: 'checkbox', checked: s.liberty });
  const ruleNew = h('input', { type: 'radio', name: 'rule', checked: !s.rule2015 });
  const rule2015 = h('input', { type: 'radio', name: 'rule', checked: s.rule2015 });
  const seat1 = h('input', { type: 'radio', name: 'seat', checked: s.seat !== 1 });
  const seat2 = h('input', { type: 'radio', name: 'seat', checked: s.seat === 1 });
  const start = async () => {
    const ok = await call('POST', '/api/new', { messe: messe.checked, liberty: liberty.checked, rule2015: rule2015.checked, seat: seat2.checked ? 1 : 0 });
    if (ok) { showNewGame = false; render(); }
  };
  return h('div', { class: 'panel newgame' },
    h('h2', {}, 'New game'),
    h('fieldset', {}, h('legend', {}, 'Promo Wonders in the pool'),
      h('label', {}, messe, ' The Messe'), h('label', {}, liberty, ' Statue of Liberty')),
    h('fieldset', {}, h('legend', {}, 'Who starts the next Age when military is tied'),
      h('label', {}, ruleNew, ' The player who took the last card chooses (current rule)'),
      h('label', {}, rule2015, ' The player who took the last card starts (2015 rule)')),
    h('fieldset', {}, h('legend', {}, 'Your seat'),
      h('label', {}, seat1, ' P1'), h('label', {}, seat2, ' P2'),
      h('div', { class: 'small muted' }, 'P1 and P2 are only labels; who plays first is entered next.')),
    h('div', { class: 'row', style: 'display:flex;gap:8px;justify-content:flex-end' },
      view?.game ? h('button', { class: 'btn ghost', onclick: () => { showNewGame = false; render(); } }, 'Back to the game') : null,
      h('button', { class: 'btn', onclick: start }, view?.game ? 'Start (this game is saved and closed)' : 'Start')));
}

function rewind(count, text) {
  const later = view.game.log.length - count;
  if (later === 0) return;
  if (confirm(`Rewind to just after line ${count} (${text})?\nThe ${later} later line${later > 1 ? 's are' : ' is'} removed.`))
    call('POST', '/api/rewind', { count });
}

$('undo').addEventListener('click', () => call('POST', '/api/undo'));
$('newgame').addEventListener('click', () => { showNewGame = true; render(); });
document.addEventListener('click', hidePopover);
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') hidePopover();
  if ((e.ctrlKey || e.metaKey) && e.key === 'z' && $('modal').hidden && !showNewGame && view?.game?.canUndo) {
    e.preventDefault();
    call('POST', '/api/undo');
  }
});

call('GET', '/api/state');
