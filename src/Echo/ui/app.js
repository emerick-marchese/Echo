// Echo — interface (hosted in WebView2; talks to the C# host through a JSON bridge).

// ---------- Icons ----------
const ICONS = {
  'record-dot': '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3.5" fill="currentColor" stroke="none"/>',
  list: '<path d="M9 6h12M9 12h12M9 18h12"/><circle cx="4.5" cy="6" r="1.2"/><circle cx="4.5" cy="12" r="1.2"/><circle cx="4.5" cy="18" r="1.2"/>',
  settings: '<path d="M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6"/>',
  mic: '<rect x="9" y="2.5" width="6" height="12" rx="3"/><path d="M5 11a7 7 0 0 0 14 0"/><path d="M12 18v3.5"/><path d="M8.5 21.5h7"/>',
  'mic-off': '<path d="M3 3l18 18"/><path d="M9 9v2a3 3 0 0 0 5.2 2"/><path d="M15 9.5V5.5a3 3 0 0 0-5.8-1"/><path d="M5 11a7 7 0 0 0 11.6 5.3"/><path d="M19 11c0 .7-.1 1.4-.3 2"/><path d="M12 18v3.5"/><path d="M8.5 21.5h7"/>',
  users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.9"/><path d="M16 3.1a4 4 0 0 1 0 7.8"/>',
  headset: '<path d="M3 15v-3a9 9 0 0 1 18 0v3"/><path d="M21 15v2a2 2 0 0 1-2 2h-2v-6h4z"/><path d="M3 15v2a2 2 0 0 0 2 2h2v-6H3z"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2.5"/>',
  folder: '<path d="M3 7.5A2.5 2.5 0 0 1 5.5 5H9l2 2h7.5A2.5 2.5 0 0 1 21 9.5v8a2.5 2.5 0 0 1-2.5 2.5h-13A2.5 2.5 0 0 1 3 17.5z"/>',
  external: '<path d="M14 4h6v6"/><path d="M10 14L20 4"/><path d="M20 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h5"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-2.6-6.4L21 8"/><path d="M21 3v5h-5"/>',
  trash: '<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 14H6L5 6"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7"/>',
  back: '<path d="M15 18l-6-6 6-6"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="M21 21l-4.3-4.3"/>',
  sparkles: '<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/><path d="M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z"/>',
  text: '<path d="M4 6h16M4 10.5h16M4 15h11M4 19.5h7"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5.5M12 7.5h.01"/>',
  alert: '<path d="M12 3.5l9 16H3z"/><path d="M12 10v4.5M12 17.5h.01"/>',
  palette: '<circle cx="12" cy="12" r="9"/><circle cx="8" cy="10" r="1.2"/><circle cx="12" cy="7.5" r="1.2"/><circle cx="16" cy="10" r="1.2"/><path d="M12 21a3 3 0 0 1 0-6h1.5"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  chip: '<rect x="6" y="6" width="12" height="12" rx="2"/><path d="M9 2.5V6M15 2.5V6M9 18v3.5M15 18v3.5M2.5 9H6M2.5 15H6M18 9h3.5M18 15h3.5"/>',
  wave: '<path d="M3 12h1.5M7 8v8M11 4.5v15M15 8v8M19 10.5v3M21.5 12h0"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  bolt: '<path d="M13 2.5L4 14h7l-1 7.5L19 10h-7z"/>',
};

const icon = (name, cls = '') => `<svg class="icon ${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] ?? ''}</svg>`;

const APP_ICONS = { teams: 'users', discord: 'headset' };
const VOICE_COLORS = ['#c084fc', '#f59e0b', '#38bdf8', '#f472b6', '#a3e635'];
const ME_COLOR = '#2bd67b';
const voiceColor = v => v > 0 ? VOICE_COLORS[(v - 1) % VOICE_COLORS.length] : '#8b93a7';
const APP_NAMES = { teams: 'Teams', discord: 'Discord' };

// ---------- Helpers ----------
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const $ = sel => document.querySelector(sel);
const pad = n => String(Math.floor(n)).padStart(2, '0');
const timer = s => `${pad(s / 3600)}:${pad((s % 3600) / 60)}:${pad(s % 60)}`;
const stamp = s => timer(Math.max(0, s));

function duration(seconds) {
  const s = Math.round(seconds ?? 0);
  if (s < 60) return `${s} s`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${pad(m % 60)}`;
}

const dateFmt = new Intl.DateTimeFormat('fr-FR', { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
const timeFmt = new Intl.DateTimeFormat('fr-FR', { hour: '2-digit', minute: '2-digit' });
const dateTime = iso => { const d = new Date(iso); return `${dateFmt.format(d)} · ${timeFmt.format(d)}`; };

function talkTime(seconds) {
  const s = Math.round(seconds ?? 0);
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${pad(s % 60)}`;
}

function initials(name) {
  const generic = /^Intervenant (\d+)$/.exec(name);
  if (generic) return generic[1];
  // Letters and digits only: Discord pseudos often carry symbols or emoji.
  const words = name.split(/[\s_.\-]+/).map(w => w.replace(/[^\p{L}\p{N}]/gu, '')).filter(Boolean);
  const named = words.filter(w => /\p{L}/u.test(w));
  return (named.length ? named : words).slice(0, 2).map(w => [...w][0].toUpperCase()).join('') || '?';
}

// A speaker "pill": avatar, name, talk time. Renamable ones turn into a text field on click.
function speakerChip({ key, name, color, speaking = false, talk = null, renamable = false, editing = false, muted = false, gone = false }) {
  const label = editing
    ? `<input class="speaker-input" data-rename="${esc(key)}" value="${esc(name)}" maxlength="40" spellcheck="false">`
    : `<span class="sname">${esc(name)}${muted ? `<span class="smuted" title="Muet dans Discord">${icon('mic-off')}</span>` : ''}</span>`;
  const title = renamable && !editing ? 'Cliquer pour donner un nom' : gone ? 'A quitté l\'appel' : '';
  return `<div class="speaker ${speaking ? 'speaking' : ''} ${renamable ? 'renamable' : ''} ${gone ? 'gone' : ''}" style="--c:${color}" ${renamable && !editing ? `data-edit="${esc(key)}"` : ''} ${title ? `title="${esc(title)}"` : ''}>
    <span class="avatar">${esc(initials(name))}</span>
    <span class="sinfo">${label}${talk !== null ? `<span class="stalk">${talkTime(talk)}</span>` : ''}</span>
  </div>`;
}

function size(bytes) {
  if (!bytes) return '';
  if (bytes < 1e6) return `${Math.max(1, Math.round(bytes / 1e3))} Ko`;
  return `${(bytes / 1e6).toLocaleString('fr-FR', { maximumFractionDigits: 1 })} Mo`;
}

// Level (0–1 linear) to meter width, on a 60 dB scale.
const meterWidth = level => level <= 0 ? 0 : Math.max(0, Math.min(100, (20 * Math.log10(level) + 60) / 60 * 100));

function toggle(id, checked, attrs = '') {
  return `<label class="toggle"><input type="checkbox" id="${id}" ${checked ? 'checked' : ''} ${attrs}><span class="knob"></span></label>`;
}

// ---------- Bridge ----------
function createWebViewBridge(webview) {
  let nextId = 1;
  const pending = new Map();
  const listeners = new Map();
  webview.addEventListener('message', e => {
    const msg = e.data;
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      msg.ok ? resolve(msg.result) : reject(new Error(msg.error || 'Erreur'));
    } else if (msg.event) {
      (listeners.get(msg.event) ?? []).forEach(fn => fn(msg.data));
    }
  });
  return {
    call(type, args = {}) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        webview.postMessage({ id, type, args });
      });
    },
    on(event, fn) {
      if (!listeners.has(event)) listeners.set(event, []);
      listeners.get(event).push(fn);
    },
  };
}

// ---------- State ----------
const ui = {
  page: 'record',
  status: null,
  settings: null,
  options: null,
  recordings: [],
  filter: 'all',
  query: '',
  detail: null,
  tab: 'summary',
  starting: false,
  editing: null,
  namesChanged: false,
  structureKey: '',
  info: {},
};
let bridge;

async function call(type, args) {
  try {
    return await bridge.call(type, args);
  } catch (e) {
    toast('Erreur', e.message, 'error');
    throw e;
  }
}

// ---------- Toasts ----------
function toast(title, text, level = 'info') {
  const el = document.createElement('div');
  el.className = `toast ${level}`;
  el.innerHTML = `${icon(level === 'error' ? 'alert' : level === 'warning' ? 'alert' : 'info')}<div><div class="t-title">${esc(title)}</div>${text ? `<div class="t-text">${esc(text)}</div>` : ''}</div>`;
  $('#toasts').appendChild(el);
  setTimeout(() => el.remove(), 6000);
}

// ---------- Navigation ----------
function navigate(page) {
  ui.page = page;
  if (page !== 'detail') ui.detail = null;
  document.querySelectorAll('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.nav === (page === 'detail' ? 'recordings' : page)));
  ui.structureKey = '';
  if (page === 'recordings') loadRecordings();
  if (page === 'settings') loadSettings();
  render();
  $('#main').scrollTop = 0;
}

function render() {
  const main = $('#main');
  switch (ui.page) {
    case 'record': main.innerHTML = renderRecord(); break;
    case 'recordings': main.innerHTML = renderRecordings(); break;
    case 'detail': main.innerHTML = renderDetail(); bindDetail(); break;
    case 'settings': main.innerHTML = renderSettings(); break;
  }
  updateLive();
  renderSidebar();
}

// ---------- Record page ----------
function recordStructureKey() {
  const s = ui.status;
  if (!s) return 'loading';
  return JSON.stringify([s.selectedApp, s.autoRecord, s.autoStop, s.recording?.app ?? null, s.apps, ui.starting, s.summaryReady, !!s.processing, s.discord?.status, s.discord?.channel]);
}

function renderRecord() {
  const s = ui.status;
  if (!s) return '<div class="page"></div>';
  ui.structureKey = recordStructureKey();
  const rec = s.recording;
  const appId = rec?.app ?? s.selectedApp;
  const appName = APP_NAMES[appId];

  const discord = s.discord ?? {};
  const cards = s.apps.map(a => {
    const where = a.id === 'discord' && discord.status === 'connected' && discord.channel ? ` · ${esc(discord.channel)}` : '';
    const state = a.inCall ? `<span class="dot call"></span>En appel${where}`
      : a.running ? '<span class="dot on"></span>Ouvert'
      : '<span class="dot"></span>Fermé';
    const selected = a.id === appId;
    return `<button class="app-card app-${a.id} ${selected ? 'selected' : ''}" data-app="${a.id}" ${rec ? 'disabled' : ''}>
      <div class="app-tile">${icon(APP_ICONS[a.id])}</div>
      <div><div class="name">${esc(a.name)}</div><div class="state">${state}</div></div>
      <div class="check">${icon('check')}</div>
    </button>`;
  }).join('');

  const recorder = rec ? `
    <div class="card recorder live">
      <button class="rec-button stop" data-action="stop" title="Arrêter l'enregistrement">${icon('stop')}</button>
      <div class="timer" id="timer">${timer(rec.elapsed)}</div>
      <p class="rec-sub">Enregistrement ${esc(appName)} en cours${rec.manual ? '' : ' (démarré automatiquement)'}</p>
      <div class="meters">
        <div class="meter others"><div class="meter-label">${icon('users')}Participants</div><div class="meter-track"><div class="meter-fill" id="meter-others"></div></div></div>
        <div class="meter me"><div class="meter-label">${icon('mic')}Vous</div><div class="meter-track"><div class="meter-fill" id="meter-me"></div></div></div>
      </div>
      <div class="rec-chips" id="rec-chips"></div>
      <div class="speakers" id="speakers"></div>
    </div>` : `
    <div class="card recorder">
      <button class="rec-button" data-action="start" ${ui.starting ? 'disabled' : ''} title="Démarrer l'enregistrement">${icon('mic')}</button>
      <div class="rec-title">Démarrer l'enregistrement ${esc(appName)}</div>
      <p class="rec-sub">Echo enregistre uniquement le son de ${esc(appName)} et votre micro <strong>quand il est actif dans ${esc(appName)}</strong>. Micro coupé : votre voix n'est pas enregistrée.</p>
    </div>`;

  const banners = [];
  if (s.processing) {
    banners.push(`<div class="card banner">${icon('sparkles')}<div class="text"><strong>${esc(s.processing.text ?? 'Traitement en attente…')}</strong>${s.processing.queue > 1 ? ` · ${s.processing.queue} enregistrements en file` : ''}</div><button class="btn" data-nav="recordings">Voir</button></div>`);
  }
  if (appId === 'discord' && discord.status && discord.status !== 'connected') {
    banners.push(`<div class="card banner">${icon('headset')}<div class="text"><strong>Pseudos exacts des participants :</strong> connectez Echo à Discord pour voir le vrai pseudo de chacun, qui parle comme dans Discord, et l'état exact de votre micro (push-to-talk compris).</div><button class="btn" data-nav="settings" data-scroll="discord">Connecter</button></div>`);
  }
  if (!s.summaryReady) {
    banners.push(`<div class="card banner">${icon('info')}<div class="text"><strong>Synthèse non configurée.</strong> Ajoutez une clé API Anthropic ou choisissez Ollama pour obtenir un résumé de chaque appel.</div><button class="btn" data-nav="settings">Configurer</button></div>`);
  }

  return `<div class="page">
    <div class="page-head"><div><h1 class="page-title">Enregistrer un appel</h1><p class="page-sub">Choisissez l'application, puis lancez l'enregistrement.</p></div></div>
    <p class="section-label">Application</p>
    <div class="apps">${cards}</div>
    ${recorder}
    <div class="options">
      <div class="card option">${icon('bolt')}<div class="text"><div class="title">Démarrage automatique</div><div class="desc">Enregistre dès qu'un appel ${esc(APP_NAMES[s.selectedApp])} commence</div></div>${toggle('opt-auto', s.autoRecord)}</div>
      <div class="card option">${icon('stop')}<div class="text"><div class="title">Arrêt à la fin de l'appel</div><div class="desc">L'enregistrement s'arrête quand vous raccrochez</div></div>${toggle('opt-autostop', s.autoStop)}</div>
    </div>
    ${banners.join('')}
  </div>`;
}

let liveTick = { at: 0, elapsed: 0 };

function updateLive() {
  const rec = ui.status?.recording;
  if (!rec || ui.page !== 'record') return;
  const timerEl = $('#timer');
  if (timerEl) timerEl.textContent = timer(liveTick.elapsed + (performance.now() - liveTick.at) / 1000);
  const me = $('#meter-me'), others = $('#meter-others');
  if (me) me.style.width = `${meterWidth(rec.levels.me)}%`;
  if (others) others.style.width = `${meterWidth(rec.levels.others)}%`;
  const chips = $('#rec-chips');
  if (chips) {
    const app = APP_NAMES[rec.app];
    let mic;
    if (rec.muteSource === 'unknown') {
      mic = rec.muted
        ? `<span class="chip warn">${icon('alert')}État du micro inconnu : votre voix n'est pas enregistrée</span>`
        : `<span class="chip warn">${icon('alert')}État du micro inconnu : votre voix est enregistrée</span>`;
    } else {
      mic = rec.muted
        ? `<span class="chip bad">${icon('mic-off')}Micro coupé dans ${esc(app)} — non enregistré</span>`
        : `<span class="chip good">${icon('mic')}Micro actif dans ${esc(app)} — enregistré</span>`;
    }
    if (rec.pushToTalk) {
      mic = rec.muted
        ? `<span class="chip bad">${icon('mic-off')}Micro non transmis dans Discord — non enregistré</span>`
        : `<span class="chip good">${icon('mic')}Push-to-talk enfoncé — enregistré</span>`;
    }
    const source = rec.muteSource === 'api' ? `<span class="chip accent">${icon('link')}API Teams</span>`
      : rec.muteSource === 'discord' ? `<span class="chip accent">${icon('link')}Connecté à Discord</span>`
      : '';
    const html = mic + source;
    if (chips.innerHTML !== html) chips.innerHTML = html;
  }
  renderLiveSpeakers(rec);
}

function renderLiveSpeakers(rec) {
  const el = $('#speakers');
  const sp = rec.speakers;
  if (!el || !sp) { if (el) el.innerHTML = ''; return; }
  if (el.contains(document.activeElement) && document.activeElement.tagName === 'INPUT') return; // renaming
  const current = sp.list.find(s => s.id === sp.current) ?? sp.list.find(s => s.speaking);
  const speaking = sp.meSpeaking ? 'Vous' : current?.name;
  const chips = [speakerChip({ key: 'me', name: 'Vous', color: ME_COLOR, speaking: sp.meSpeaking, talk: sp.meTalk })]
    .concat(sp.list.map(s => speakerChip({ key: `live-${s.id}`, name: s.name, color: voiceColor(s.id), speaking: s.speaking, talk: s.talk, renamable: !sp.exact, editing: ui.editing === `live-${s.id}`, muted: s.muted, gone: s.present === false })));
  const present = sp.list.filter(s => s.present !== false).length;
  const info = sp.exact
    ? `${icon('headset')}${sp.channel ? `${esc(sp.channel)} · ` : ''}${present + 1} participant${present > 0 ? 's' : ''} · pseudos Discord`
    : sp.list.length ? `${sp.list.length} voix reconnue${sp.list.length > 1 ? 's' : ''} · cliquez pour nommer (même nom = même personne)` : 'Les voix apparaissent dès qu\'elles parlent';
  const html = `<div class="speakers-head">
      <span class="now">${speaking ? `<span class="dot live"></span><strong>${esc(speaking)}</strong>&nbsp;parle` : 'Personne ne parle'}</span>
      <span class="sp-info">${info}</span>
    </div>
    <div class="speaker-grid">${chips.join('')}</div>`;
  if (el.innerHTML !== html) el.innerHTML = html;
  const input = el.querySelector('input');
  if (input && document.activeElement !== input) { input.focus(); input.select(); }
}

setInterval(() => {
  if (ui.status?.recording && ui.page === 'record') {
    const timerEl = $('#timer');
    if (timerEl) timerEl.textContent = timer(liveTick.elapsed + (performance.now() - liveTick.at) / 1000);
  }
}, 250);

// ---------- Recordings ----------
async function loadRecordings() {
  ui.recordings = await call('recordings.list');
  if (ui.page === 'recordings') render();
}

function statusChip(r) {
  switch (r.status) {
    case 'done': return r.hasSummary ? `<span class="chip good">${icon('check')}Prêt</span>` : `<span class="chip">${icon('text')}Transcrit</span>`;
    case 'recording': return `<span class="chip bad"><span class="dot live"></span>En cours</span>`;
    case 'failed': return `<span class="chip bad">${icon('alert')}Échec</span>`;
    case 'processing': return `<span class="chip accent">${icon('sparkles')}${esc(r.progress ?? 'Traitement…')}</span>`;
    default: return `<span class="chip">${icon('sparkles')}En attente</span>`;
  }
}

function renderRecordings() {
  const q = ui.query.trim().toLowerCase();
  const items = ui.recordings
    .filter(r => ui.filter === 'all' || r.app.toLowerCase() === ui.filter)
    .filter(r => !q || r.title.toLowerCase().includes(q));

  const list = items.length === 0
    ? `<div class="card empty">${icon('wave')}<div class="title">${ui.recordings.length ? 'Aucun résultat' : 'Aucun enregistrement'}</div><div>${ui.recordings.length ? 'Essayez une autre recherche.' : 'Vos appels enregistrés apparaîtront ici.'}</div></div>`
    : `<div class="rec-list">${items.map(r => {
        const id = r.app.toLowerCase();
        return `<button class="rec-item app-${id}" data-open="${esc(r.id)}">
          <div class="app-tile">${icon(APP_ICONS[id] ?? 'wave')}</div>
          <div style="min-width:0"><div class="title">${esc(r.title)}</div><div class="meta">${esc(r.app)} · ${dateTime(r.startedAt)} · ${duration(r.duration)}</div></div>
          ${statusChip(r)}
          <div class="size">${size(r.audioBytes)}</div>
        </button>`;
      }).join('')}</div>`;

  const seg = [['all', 'Tous'], ['teams', 'Teams'], ['discord', 'Discord']]
    .map(([k, l]) => `<button data-filter="${k}" class="${ui.filter === k ? 'active' : ''}">${l}</button>`).join('');

  return `<div class="page">
    <div class="page-head">
      <div><h1 class="page-title">Enregistrements</h1><p class="page-sub">${ui.recordings.length} appel${ui.recordings.length > 1 ? 's' : ''} enregistré${ui.recordings.length > 1 ? 's' : ''} sur ce PC</p></div>
      <button class="btn" data-action="open-output">${icon('folder')}Ouvrir le dossier</button>
    </div>
    <div class="toolbar">
      <label class="search">${icon('search')}<input id="search" placeholder="Rechercher un appel…" value="${esc(ui.query)}"></label>
      <div class="segmented">${seg}</div>
    </div>
    ${list}
  </div>`;
}

async function openDetail(id) {
  ui.namesChanged = false;
  ui.editing = null;
  ui.detail = await call('recordings.get', { id });
  ui.tab = ui.detail.summaryHtml ? 'summary' : 'transcript';
  navigate('detail');
}

function renderDetail() {
  const d = ui.detail;
  if (!d) return '<div class="page"></div>';
  const r = d.summary;
  const id = r.app.toLowerCase();

  let summary;
  if (d.summaryHtml) summary = `<div class="markdown">${d.summaryHtml}</div>`;
  else if (r.status === 'processing' || r.status === 'pending' || r.status === 'recording') summary = `<div class="notice">${icon('sparkles')}La synthèse sera prête après le traitement de l'appel.</div>`;
  else if (r.error) summary = `<div class="notice error">${icon('alert')}Synthèse indisponible : ${esc(r.error)}</div>`;
  else summary = `<div class="notice">${icon('info')}Pas de synthèse pour cet appel.</div>`;

  const transcript = d.paragraphs.length
    ? `<div class="transcript">${d.paragraphs.map(p => `<div class="para ${p.speaker}" data-start="${p.start}" data-end="${p.end}">
        <button class="t" data-t="${p.start}">${stamp(p.start)}</button>
        <div><span class="who" style="color:${p.speaker === 'me' ? ME_COLOR : voiceColor(p.voice)}">${esc(p.name)}</span>${esc(p.text)}</div></div>`).join('')}</div>`
    : `<div class="notice">${icon('info')}${r.status === 'done' ? 'Aucune parole détectée.' : 'La transcription sera prête après le traitement de l\'appel.'}</div>`;

  return `<div class="page">
    <button class="btn ghost back" data-nav="recordings">${icon('back')}Enregistrements</button>
    <div class="detail-head app-${id}">
      <div class="app-tile">${icon(APP_ICONS[id] ?? 'wave')}</div>
      <div style="flex:1;min-width:0">
        <input class="title-edit" id="title-edit" value="${esc(r.title)}" spellcheck="false" title="Cliquez pour renommer">
        <div class="detail-meta"><span>${esc(r.app)}</span><span>${dateTime(r.startedAt)}</span><span>${duration(r.duration)}</span><span>${size(r.audioBytes)}</span>${statusChip(r)}</div>
      </div>
    </div>
    <div class="card player">
      ${d.audioUrl ? `<audio id="audio" controls preload="metadata" src="${esc(d.audioUrl)}"></audio>` : `<div class="notice">${icon('alert')}Fichier audio introuvable.</div>`}
      <div class="btn-row">
        <button class="btn" data-action="detail-folder">${icon('folder')}Ouvrir le dossier</button>
        <button class="btn" data-action="detail-report" ${d.hasReport ? '' : 'disabled'}>${icon('external')}Compte rendu (navigateur)</button>
        <button class="btn" data-action="detail-reprocess" ${r.status === 'recording' || r.status === 'processing' ? 'disabled' : ''}>${icon('refresh')}Relancer la transcription</button>
        <button class="btn danger" data-action="detail-delete" ${r.status === 'recording' ? 'disabled' : ''}>${icon('trash')}Supprimer</button>
      </div>
    </div>
    ${renderVoices(d)}
    <div class="tabs">
      <button class="tab ${ui.tab === 'summary' ? 'active' : ''}" data-tab="summary">${icon('sparkles')}Synthèse</button>
      <button class="tab ${ui.tab === 'transcript' ? 'active' : ''}" data-tab="transcript">${icon('text')}Transcription</button>
    </div>
    <div class="card">${ui.tab === 'summary' ? summary : transcript}</div>
  </div>`;
}

function renderVoices(d) {
  if (!d.voices?.length && !d.meTalk) return '';
  const chips = [speakerChip({ key: 'me', name: d.myName, color: ME_COLOR, talk: d.meTalk })]
    .concat(d.voices.map(v => speakerChip({ key: `voice-${v.voice}`, name: v.name, color: voiceColor(v.voice), talk: v.talk, renamable: true, editing: ui.editing === `voice-${v.voice}` })));
  const resummarize = ui.namesChanged
    ? `<div class="notice" style="margin-top:12px">${icon('sparkles')}<span style="flex:1">Noms modifiés : la transcription est à jour.</span><button class="btn" data-action="detail-resummarize">${icon('refresh')}Refaire la synthèse avec ces noms</button></div>`
    : '';
  const hint = d.exactNames ? `${icon('headset')}Pseudos Discord · cliquez pour renommer`
    : d.voices.length ? 'Cliquez sur une voix pour la nommer · donnez le même nom à deux voix pour les regrouper' : '';
  return `<div class="card voices"><div class="voices-head"><span class="section-label" style="margin:0">Intervenants</span><span class="hint">${hint}</span></div>
    <div class="speaker-grid left">${chips.join('')}</div>${resummarize}</div>`;
}

function bindDetail() {
  const input = document.querySelector('.voices input');
  if (input) { input.focus(); input.select(); }
  const audio = $('#audio');
  if (!audio) return;
  let current = null;
  audio.addEventListener('timeupdate', () => {
    const t = audio.currentTime;
    const p = [...document.querySelectorAll('.para')].find(x => t >= +x.dataset.start && t < +x.dataset.end + 1);
    if (p === current) return;
    current?.classList.remove('playing');
    current = p ?? null;
    current?.classList.add('playing');
  });
}

// ---------- Settings ----------
async function loadSettings() {
  const [settings, options] = await Promise.all([call('settings.get'), call('settings.options')]);
  ui.settings = settings;
  ui.options = options;
  if (ui.page === 'settings') render();
  if (ui.scrollTo) {
    $(`#${ui.scrollTo}-card`)?.scrollIntoView({ block: 'start' });
    ui.scrollTo = null;
  }
}

function field(label, hint, control) {
  return `<div class="field"><div><div class="label">${label}</div>${hint ? `<div class="hint">${hint}</div>` : ''}</div><div>${control}</div></div>`;
}

function segmented(name, value, items) {
  return `<div class="segmented" data-seg="${name}">${items.map(([v, l]) => `<button type="button" data-value="${v}" class="${String(value) === String(v) ? 'active' : ''}">${l}</button>`).join('')}</div>`;
}

function select(id, value, items) {
  return `<select class="input" id="${id}">${items.map(([v, l]) => `<option value="${esc(v)}" ${String(value) === String(v) ? 'selected' : ''}>${esc(l)}</option>`).join('')}</select>`;
}

function renderSettings() {
  const s = ui.settings, o = ui.options;
  if (!s || !o) return '<div class="page"><div class="page-head"><h1 class="page-title">Paramètres</h1></div></div>';
  const teams = ui.status?.teams ?? {};
  const teamsState = teams.paired ? '<span class="chip good">' + icon('check') + 'Connecté et autorisé</span>'
    : teams.connected ? '<span class="chip warn">' + icon('alert') + 'À autoriser pendant un appel</span>'
    : '<span class="chip">' + icon('info') + 'API Teams non détectée</span>';

  return `<div class="page settings">
    <div class="page-head"><div><h1 class="page-title">Paramètres</h1><p class="page-sub">Tout est enregistré sur ce PC.</p></div></div>

    <div class="card"><h3>${icon('palette')}Apparence</h3>
      ${field('Thème', '', segmented('theme', s.theme, [['dark', 'Sombre'], ['light', 'Clair'], ['system', 'Système']]))}
    </div>

    <div class="card"><h3>${icon('user')}Général</h3>
      ${field('Votre nom', 'Dans la transcription et la synthèse', `<input class="input" id="s-myName" value="${esc(s.myName)}">`)}
      ${field('Nom des autres participants', '', `<input class="input" id="s-othersName" value="${esc(s.othersName)}">`)}
      ${field('Dossier des enregistrements', 'Un sous-dossier par appel', `<div class="input-row"><input class="input" id="s-outputFolder" value="${esc(s.outputFolder)}"><button class="btn" data-action="browse">${icon('folder')}Parcourir</button></div>`)}
      ${field('Lancer Echo avec Windows', 'Démarre discrètement dans la zone de notification', toggle('s-startWithWindows', s.startWithWindows))}
      ${field('Notification au début', 'Prévenir quand un enregistrement démarre', toggle('s-notifyOnStart', s.notifyOnStart))}
    </div>

    <div class="card"><h3>${icon('mic')}Audio</h3>
      ${field('Qualité', 'Opus, excellente pour la voix', segmented('opusBitrate', s.opusBitrate, [[16000, 'Légère · 7 Mo/h'], [24000, 'Recommandée · 11 Mo/h'], [32000, 'Haute · 14 Mo/h']]))}
      ${field('Microphone', 'Le même que dans Teams / Discord', select('s-microphoneId', s.microphoneId, [['', 'Micro par défaut de Windows (communications)'], ...o.microphones.map(m => [m.id, m.name])]))}
      ${field('Annulation d\'écho', 'Retire le son des haut-parleurs du micro', toggle('s-echoCancellation', s.echoCancellation))}
      ${field('Si l\'état du micro est inconnu', 'Enregistrer quand même votre voix', toggle('s-recordMicWhenUnknown', s.recordMicWhenUnknown))}
    </div>

    <div class="card"><h3>${icon('chip')}Transcription</h3>
      ${field('Modèle', 'Transcription locale (Whisper), sur ce PC', select('s-whisperModel', s.whisperModel, [['', 'Automatique — meilleur modèle installé'], ...o.models.map(m => [m.file, `${m.label}${m.installed ? ' — installé' : ` — ${m.sizeMb} Mo à télécharger`}`])]))}
      ${field('Langue des appels', '', select('s-language', s.language, o.languages.map(l => [l.code, l.name])))}
      ${field('Carte graphique', 'Transcription beaucoup plus rapide si disponible', toggle('s-useGpu', s.useGpu))}
      ${field('Reconnaître les voix', 'Montre qui parle pendant l\'appel et sépare les intervenants dans la transcription', toggle('s-identifyVoices', s.identifyVoices))}
      ${field('Vocabulaire', 'Noms, sigles, jargon : aide à les écrire correctement', `<textarea class="input" id="s-vocabulary">${esc(s.vocabulary)}</textarea>`)}
    </div>

    <div class="card"><h3>${icon('sparkles')}Synthèse</h3>
      ${field('Moteur', 'Claude : meilleure qualité · Ollama : 100 % local', segmented('summaryProvider', s.summaryProvider, [['claude', 'Claude'], ['ollama', 'Ollama'], ['none', 'Aucune']]))}
      <div id="claude-fields" ${s.summaryProvider === 'claude' ? '' : 'hidden'}>
        ${field('Clé API Anthropic', `<a href="#" data-link="https://console.anthropic.com/settings/keys">Obtenir une clé</a> · seul le texte est envoyé, jamais l'audio`, `<input class="input" id="s-apiKey" type="password" placeholder="${s.hasApiKey ? 'Clé enregistrée — laisser vide pour la conserver' : 'sk-ant-…'}">`)}
        ${field('Modèle Claude', '', `<input class="input" id="s-claudeModel" value="${esc(s.claudeModel)}">`)}
      </div>
      <div id="ollama-fields" ${s.summaryProvider === 'ollama' ? '' : 'hidden'}>
        ${field('Adresse d\'Ollama', `<a href="#" data-link="https://ollama.com">ollama.com</a> · rien ne quitte le PC`, `<input class="input" id="s-ollamaUrl" value="${esc(s.ollamaUrl)}">`)}
        ${field('Modèle Ollama', 'Par exemple mistral-nemo', `<input class="input" id="s-ollamaModel" value="${esc(s.ollamaModel)}">`)}
      </div>
    </div>

    <div class="card"><h3>${icon('users')}Teams</h3>
      <p class="desc">Pour connaître exactement l'état de votre micro dans Teams : Teams → Paramètres → Confidentialité → <strong>Gérer l'API</strong> → Activer l'API, puis acceptez la demande d'Echo pendant un appel.</p>
      ${field('API locale de Teams', '', `<div class="btn-row" style="align-items:center">${teamsState}<button class="btn" data-action="pair" ${teams.connected && !teams.paired ? '' : 'disabled'}>Demander l'autorisation</button></div>`)}
    </div>

    ${renderDiscordCard(s)}

    <div class="save-bar">
      <button class="btn ghost" data-action="open-logs">Journaux</button>
      <button class="btn primary" data-action="save-settings">${icon('check')}Enregistrer les paramètres</button>
    </div>
  </div>`;
}

function discordStatusHtml(d = {}) {
  switch (d.status) {
    case 'connected': return `<span class="chip good">${icon('check')}Connecté${d.user ? ` · ${esc(d.user)}` : ''}</span>${d.inVoice && d.channel ? `<span class="chip accent">${icon('headset')}${esc(d.channel)}</span>` : ''}`;
    case 'needsAuth': return `<span class="chip warn">${icon('alert')}${d.error ? esc(d.error) : 'À autoriser : cliquez sur Connecter à Discord'}</span>`;
    case 'connecting': return `<span class="chip">${icon('refresh')}Connexion…</span>`;
    case 'closed': return `<span class="chip">${icon('info')}Discord n'est pas ouvert</span>`;
    case 'error': return `<span class="chip bad">${icon('alert')}${esc(d.error ?? 'Erreur')}</span>`;
    default: return `<span class="chip">${icon('info')}Non connecté</span>`;
  }
}

function renderDiscordCard(s) {
  const d = ui.status?.discord ?? {};
  const linked = d.status === 'connected';
  const configured = !!s.discordClientId;
  return `<div class="card" id="discord-card"><h3>${icon('headset')}Discord</h3>
      <p class="desc">Connectez Echo à votre Discord pour avoir le <strong>pseudo exact</strong> de chaque participant (en direct et dans la transcription), voir qui parle comme dans Discord, connaître exactement l'état de votre micro (muet, sourdine, <strong>push-to-talk</strong>) et détecter le début et la fin des appels. Echo ne lit que votre salon vocal : jamais vos messages.</p>
      ${field('Connexion', 'Via l\'application Discord ouverte sur ce PC', `<div class="btn-row" id="discord-status" style="align-items:center">${discordStatusHtml(d)}</div>`)}
      <details class="steps" ${linked ? '' : 'open'}>
        <summary>${linked ? 'Comment ça marche' : 'Configurer (2 minutes, une seule fois)'}</summary>
        <ol>
          <li>Ouvrez le <a href="#" data-link="https://discord.com/developers/applications">portail développeur Discord</a> avec votre compte, cliquez sur <strong>New Application</strong>, nommez-la <strong>Echo</strong> et validez.</li>
          <li>Menu <strong>OAuth2</strong> : copiez le <strong>Client ID</strong>, puis cliquez sur <strong>Reset Secret</strong> et copiez le <strong>Client Secret</strong>. Dans <strong>Redirects</strong>, cliquez sur <strong>Add Redirect</strong>, saisissez exactement <code>http://localhost</code> (sans « / » à la fin) et cliquez sur <strong>Save Changes</strong>.</li>
          <li>Collez-les ci-dessous et cliquez sur <strong>Connecter à Discord</strong> : Discord ouvre une fenêtre d'autorisation, cliquez sur <strong>Autoriser</strong>.</li>
        </ol>
        <p class="hint">Cette application Discord est la vôtre : elle sert uniquement à autoriser Echo sur votre compte. Le secret est chiffré sur ce PC. Sans connexion, Echo reconnaît les participants à leur voix.</p>
      </details>
      ${field('Client ID', 'Portail développeur → OAuth2', `<input class="input" id="d-clientId" inputmode="numeric" value="${esc(s.discordClientId ?? '')}" placeholder="123456789012345678">`)}
      ${field('Client Secret', 'Chiffré sur ce PC', `<input class="input" id="d-clientSecret" type="password" placeholder="${s.hasDiscordSecret ? 'Secret enregistré — laisser vide pour le conserver' : 'Collez le Client Secret'}">`)}
      <div class="btn-row" style="justify-content:flex-end;padding-top:12px">
        ${configured && (linked || d.status === 'needsAuth' || d.status === 'connecting') ? `<button class="btn ghost" data-action="discord-disconnect" ${linked ? '' : 'disabled'}>Déconnecter</button>` : ''}
        <button class="btn primary" data-action="discord-connect" id="discord-connect">${icon('link')}${linked ? 'Reconnecter' : 'Connecter à Discord'}</button>
      </div>
    </div>`;
}

function readSettingsForm() {
  const s = { ...ui.settings };
  const val = id => $(`#s-${id}`)?.value ?? '';
  const chk = id => !!$(`#s-${id}`)?.checked;
  const seg = name => $(`[data-seg="${name}"] .active`)?.dataset.value;
  Object.assign(s, {
    myName: val('myName'), othersName: val('othersName'), outputFolder: val('outputFolder'),
    startWithWindows: chk('startWithWindows'), notifyOnStart: chk('notifyOnStart'),
    opusBitrate: Number(seg('opusBitrate')), microphoneId: val('microphoneId'),
    echoCancellation: chk('echoCancellation'), recordMicWhenUnknown: chk('recordMicWhenUnknown'),
    whisperModel: val('whisperModel'), language: val('language'), useGpu: chk('useGpu'), identifyVoices: chk('identifyVoices'), vocabulary: val('vocabulary'),
    summaryProvider: seg('summaryProvider'), apiKey: val('apiKey'),
    claudeModel: val('claudeModel'), ollamaUrl: val('ollamaUrl'), ollamaModel: val('ollamaModel'),
    theme: seg('theme'),
  });
  return s;
}

// ---------- Sidebar ----------
function renderSidebar() {
  const s = ui.status;
  if (!s) return;
  const rows = s.apps.map(a => `<div class="row"><span class="dot ${s.recording?.app === a.id ? 'live' : a.inCall ? 'call' : a.running ? 'on' : ''}"></span>${esc(a.name)} · ${s.recording?.app === a.id ? 'enregistrement' : a.inCall ? 'en appel' : a.running ? 'ouvert' : 'fermé'}</div>`).join('');
  const el = $('#sidebar-status');
  if (el.innerHTML !== rows) el.innerHTML = rows;
  const badge = $('#nav-badge');
  const n = s.processing?.queue ?? 0;
  badge.hidden = n === 0;
  badge.textContent = n;
}

// ---------- Theme ----------
function applyTheme(theme) {
  const dark = theme === 'dark' || (theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

// ---------- Events ----------
document.addEventListener('click', async e => {
  const edit = e.target.closest('[data-edit]');
  if (edit) {
    ui.editing = edit.dataset.edit;
    if (ui.page === 'detail') render(); else renderLiveSpeakers(ui.status.recording);
    return;
  }
  const t = e.target.closest('[data-nav],[data-app],[data-action],[data-open],[data-filter],[data-tab],[data-t],[data-seg] button,[data-link]');
  if (!t) return;

  if (t.dataset.link) { e.preventDefault(); call('app.openExternal', { url: t.dataset.link }); return; }
  if (t.dataset.nav) {
    navigate(t.dataset.nav);
    if (t.dataset.scroll) ui.scrollTo = t.dataset.scroll;
    return;
  }
  if (t.dataset.open) { openDetail(t.dataset.open); return; }
  if (t.dataset.filter) { ui.filter = t.dataset.filter; render(); return; }
  if (t.dataset.tab) { ui.tab = t.dataset.tab; render(); return; }
  if (t.dataset.t !== undefined) {
    const audio = $('#audio');
    if (audio) { audio.currentTime = parseFloat(t.dataset.t); audio.play(); }
    return;
  }
  if (t.closest('[data-seg]')) {
    const seg = t.closest('[data-seg]');
    seg.querySelectorAll('button').forEach(b => b.classList.toggle('active', b === t));
    if (seg.dataset.seg === 'summaryProvider') {
      $('#claude-fields').hidden = t.dataset.value !== 'claude';
      $('#ollama-fields').hidden = t.dataset.value !== 'ollama';
    }
    if (seg.dataset.seg === 'theme') {
      applyTheme(t.dataset.value);
      ui.settings.theme = t.dataset.value;
      call('settings.setTheme', { theme: t.dataset.value });
    }
    return;
  }
  if (t.dataset.app) {
    if (ui.status.recording) return;
    ui.status.selectedApp = t.dataset.app;
    render();
    call('app.select', { app: t.dataset.app });
    return;
  }

  switch (t.dataset.action) {
    case 'start':
      ui.starting = true; render();
      try {
        const ok = await call('recording.start', { app: ui.status.selectedApp });
        if (!ok) toast('Enregistrement impossible', 'Un enregistrement est déjà en cours ou n\'a pas pu démarrer.', 'warning');
      } finally { ui.starting = false; render(); }
      break;
    case 'stop':
      await call('recording.stop');
      break;
    case 'open-output': call('app.openOutputFolder'); break;
    case 'open-logs': call('app.openLogs'); break;
    case 'browse': {
      const path = await call('settings.browseFolder', { current: $('#s-outputFolder').value });
      if (path) $('#s-outputFolder').value = path;
      break;
    }
    case 'pair': {
      const ok = await call('teams.pair');
      toast(ok ? 'Demande envoyée' : 'Teams injoignable', ok ? 'Acceptez la demande dans la fenêtre de Teams.' : 'Vérifiez que l\'API est activée dans Teams.', ok ? 'info' : 'warning');
      break;
    }
    case 'discord-connect': {
      const clientId = $('#d-clientId').value.trim(), clientSecret = $('#d-clientSecret').value.trim();
      t.disabled = true;
      t.innerHTML = `${icon('refresh')}En attente de Discord…`;
      toast('Autorisez Echo dans Discord', 'Discord ouvre une fenêtre d\'autorisation : cliquez sur Autoriser.');
      try {
        const r = await call('discord.connect', { clientId, clientSecret });
        toast('Connecté à Discord', r?.user ? `Compte ${r.user} : les pseudos seront exacts.` : '');
      } catch { /* toast already shown */ } finally {
        ui.settings = await call('settings.get');
        if (ui.page === 'settings') render();
      }
      break;
    }
    case 'discord-disconnect':
      await call('discord.disconnect');
      toast('Discord déconnecté', 'Echo reconnaîtra de nouveau les participants à leur voix.');
      break;
    case 'save-settings': {
      ui.settings = await call('settings.save', readSettingsForm());
      applyTheme(ui.settings.theme);
      render();
      toast('Paramètres enregistrés');
      break;
    }
    case 'detail-folder': call('recordings.openFolder', { id: ui.detail.summary.id }); break;
    case 'detail-report': call('recordings.openReport', { id: ui.detail.summary.id }); break;
    case 'detail-reprocess':
      await call('recordings.reprocess', { id: ui.detail.summary.id });
      toast('Traitement relancé', 'La transcription et la synthèse vont être refaites.');
      ui.detail = await call('recordings.get', { id: ui.detail.summary.id });
      render();
      break;
    case 'detail-resummarize':
      await call('recordings.resummarize', { id: ui.detail.summary.id });
      ui.namesChanged = false;
      toast('Synthèse relancée', 'Elle sera mise à jour avec les noms des intervenants.');
      ui.detail = await call('recordings.get', { id: ui.detail.summary.id });
      render();
      break;
    case 'detail-delete':
      if (!confirm(`Supprimer « ${ui.detail.summary.title} » ? Le dossier ira dans la corbeille.`)) return;
      await call('recordings.delete', { id: ui.detail.summary.id });
      toast('Enregistrement supprimé', 'Il est récupérable depuis la corbeille.');
      navigate('recordings');
      break;
  }
});

document.addEventListener('change', e => {
  if (e.target.id === 'opt-auto') call('recording.setAuto', { autoRecord: e.target.checked });
  if (e.target.id === 'opt-autostop') call('recording.setAuto', { autoStop: e.target.checked });
});

document.addEventListener('input', e => {
  if (e.target.id === 'search') {
    ui.query = e.target.value;
    const pos = e.target.selectionStart;
    render();
    const input = $('#search');
    input.focus();
    input.setSelectionRange(pos, pos);
  }
});

document.addEventListener('keydown', e => {
  if (e.target.id === 'title-edit' && e.key === 'Enter') e.target.blur();
  if (e.target.dataset?.rename) {
    if (e.key === 'Enter') e.target.blur();
    if (e.key === 'Escape') { e.target.value = ''; e.target.blur(); }
  }
});

document.addEventListener('focusout', async e => {
  const key = e.target.dataset?.rename;
  if (!key) return;
  const name = e.target.value.trim();
  ui.editing = null;
  if (key.startsWith('live-')) {
    if (name) await call('speakers.renameLive', { id: Number(key.slice(5)), name });
    renderLiveSpeakers(ui.status.recording);
  } else if (key.startsWith('voice-') && ui.detail) {
    const voice = Number(key.slice(6));
    const current = ui.detail.voices.find(v => v.voice === voice)?.name;
    if (name && name !== current) {
      const merging = ui.detail.voices.some(v => v.voice !== voice && v.name.toLocaleLowerCase() === name.toLocaleLowerCase());
      await call('recordings.renameVoice', { id: ui.detail.summary.id, voice, name });
      ui.detail = await call('recordings.get', { id: ui.detail.summary.id });
      ui.namesChanged = true;
      if (merging) toast('Voix regroupées', `Les deux voix sont maintenant « ${name} » dans la transcription.`);
    }
    render();
  }
});

document.addEventListener('focusout', async e => {
  if (e.target.id !== 'title-edit' || !ui.detail) return;
  const title = e.target.value.trim();
  if (!title || title === ui.detail.summary.title) { e.target.value = ui.detail.summary.title; return; }
  await call('recordings.rename', { id: ui.detail.summary.id, title });
  ui.detail.summary.title = title;
  toast('Appel renommé', title);
});

matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => applyTheme(ui.settings?.theme ?? ui.status?.theme ?? 'dark'));

// ---------- Live state from the host ----------
function onState(status) {
  const wasRecording = !!ui.status?.recording;
  ui.status = status;
  if (status.recording) liveTick = { at: performance.now(), elapsed: status.recording.elapsed };
  if (!ui.settings) applyTheme(status.theme);

  if (ui.page === 'record' && recordStructureKey() !== ui.structureKey) render();
  else { updateLive(); renderSidebar(); }
  const discordEl = $('#discord-status');
  if (discordEl) {
    const html = discordStatusHtml(status.discord);
    if (discordEl.innerHTML !== html) discordEl.innerHTML = html;
  }

  if (wasRecording && !status.recording && ui.page === 'recordings') loadRecordings();
}

// ---------- Start ----------
async function start() {
  document.querySelectorAll('[data-icon]').forEach(el => { el.outerHTML = icon(el.dataset.icon); });
  const webview = window.chrome?.webview;
  bridge = webview ? createWebViewBridge(webview) : (await import('./mock.js')).createMockBridge();

  bridge.on('state', onState);
  bridge.on('recordingsChanged', () => {
    if (ui.page === 'recordings') loadRecordings();
    if (ui.page === 'detail' && ui.detail) call('recordings.get', { id: ui.detail.summary.id }).then(d => {
      const playing = $('#audio') && !$('#audio').paused;
      ui.detail = d;
      if (!playing) render();
    }).catch(() => {});
  });
  bridge.on('notify', n => toast(n.title, n.text, n.level));
  bridge.on('navigate', n => navigate(n.page));

  ui.info = await call('app.info');
  $('#version').textContent = `Echo ${ui.info.version}`;
  onState(await call('state'));
  navigate(ui.info.startPage ?? 'record');
}

start();
