// The Advisor Window page (spec A8). It holds no game rules: the server sends the whole view (/api/state), and each
// click posts one Move Log line that the server checks, appends and replays. Advice (ticket 30) plugs into the side panel.

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
const enter = (line) => { guildPick = []; multiPick = []; return call('POST', '/api/entry', { line }); };

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

// ------------------------------------------------------------------ render

function render() {
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
    main.append(newGameScreen());
    return;
  }
  main.className = '';
  $('prompt').textContent = game.prompt;
  main.append(
    h('section', {}, boardPanel(game)),
    h('aside', {}, sidePanel(game)));
}

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
    h('div', { class: 'panel' }, h('h2', {}, 'Advice'),
      h('p', { class: 'muted small' }, 'Advice on request comes in the next build step (ticket 30).')),
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
    }, h('span', { class: 'slotname' }, s.name), s.card ? s.card.name : '');
    if (s.actions.length) el.addEventListener('click', (e) => cardMenu(e, s));
    else if (s.pulse) el.addEventListener('click', () => slotPicker(s));
    else if (guildMode) el.addEventListener('click', () => {
      guildPick = guildPick.includes(s.name) ? guildPick.filter(x => x !== s.name)
        : guildPick.length < g.reveal.count ? [...guildPick, s.name] : guildPick;
      render();
    });
    box.append(el);
  }
  return h('div', { class: 'pyramid-wrap' }, box);
}

function cardMenu(event, slot) {
  const pop = $('popover');
  pop.replaceChildren(h('div', { class: 'ptitle' }, slot.card.name));
  for (const a of slot.actions) {
    pop.append(h('button', { disabled: !a.enabled, onclick: () => enter(a.line) }, a.label));
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
    h('div', { class: 'tiles' }, g.decision.tiles.map(t => h('button', { class: 'tile', onclick: () => enter(t.line) }, t.label))));
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
