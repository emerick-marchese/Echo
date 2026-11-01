// Demo data for previewing the interface in a regular browser (outside the Echo app).

export function createMockBridge() {
  const listeners = new Map();
  const emit = (event, data) => (listeners.get(event) ?? []).forEach(fn => fn(data));
  const params = new URLSearchParams(location.search);

  const state = {
    state: 'ready',
    selectedApp: params.get('app') ?? 'discord',
    autoRecord: false,
    autoStop: true,
    theme: params.get('theme') ?? 'dark',
    summaryReady: params.get('summary') !== 'off',
    apps: [
      { id: 'teams', name: 'Teams', running: true, inCall: false },
      { id: 'discord', name: 'Discord', running: true, inCall: true },
    ],
    recording: null,
    processing: params.get('processing') ? { text: 'Transcription 42 %', queue: 1 } : null,
    teams: { connected: true, paired: false, inMeeting: false },
    discord: params.get('discord') === 'off'
      ? { status: 'notConfigured', error: null, user: null, inVoice: false, channel: null, participants: 0 }
      : { status: 'connected', error: null, user: 'Emerick', inVoice: true, channel: 'Raid · Les Copains', participants: 3 },
  };
  const discordOn = () => state.discord.status === 'connected';
  let startedAt = 0;

  const settings = {
    theme: state.theme, myName: 'Moi', othersName: 'Participants',
    outputFolder: 'C:\\Users\\Emerick\\Documents\\Echo', startWithWindows: true, notifyOnStart: true,
    opusBitrate: 24000, microphoneId: '', echoCancellation: true, recordMicWhenUnknown: false,
    whisperModel: '', language: 'fr', useGpu: true, identifyVoices: true, vocabulary: 'Echo, Kubernetes, RGPD',
    summaryProvider: 'claude', hasApiKey: true, claudeModel: 'claude-opus-5-5',
    ollamaUrl: 'http://localhost:11434', ollamaModel: 'mistral-nemo',
    discordClientId: params.get('discord') === 'off' ? '' : '1301234567890123456', hasDiscordSecret: params.get('discord') !== 'off',
  };

  const now = Date.now();
  const recordings = [
    { id: '2026-10-31 18h02 - Raid du jeudi', title: 'Raid du jeudi', app: 'Discord', startedAt: new Date(now - 3600e3).toISOString(), duration: 5420, status: 'processing', progress: 'Transcription 42 %', hasSummary: false, audioBytes: 16.4e6 },
    { id: '2026-10-31 10h30 - Point hebdo produit', title: 'Point hebdo produit', app: 'Teams', startedAt: new Date(now - 9 * 3600e3).toISOString(), duration: 2710, status: 'done', hasSummary: true, audioBytes: 8.2e6 },
    { id: '2026-10-30 16h15 - Entretien candidat backend', title: 'Entretien candidat backend', app: 'Teams', startedAt: new Date(now - 27 * 3600e3).toISOString(), duration: 3305, status: 'done', hasSummary: true, audioBytes: 10.1e6 },
    { id: '2026-10-29 21h40 - Session projet perso', title: 'Session projet perso', app: 'Discord', startedAt: new Date(now - 52 * 3600e3).toISOString(), duration: 1980, status: 'done', hasSummary: true, audioBytes: 6.0e6 },
    { id: '2026-10-28 09h00 - Appel Teams', title: 'Appel Teams', app: 'Teams', startedAt: new Date(now - 80 * 3600e3).toISOString(), duration: 640, status: 'failed', error: 'clé API Anthropic non configurée', hasSummary: false, audioBytes: 1.9e6 },
  ];

  const detail = {
    summary: recordings[1],
    summaryHtml: `<h1>Point hebdo produit : lancement de la version 2</h1>
<h2>Résumé</h2><p>L'équipe a fait le point sur le lancement de la version 2 prévu le 14 novembre. Les tests de charge sont concluants ; il reste à finaliser la documentation et la migration des comptes existants. Le budget marketing est validé.</p>
<h2>Points clés</h2><ul><li>Tests de charge : 3 000 utilisateurs simultanés sans dégradation.</li><li>La migration des comptes prendra environ deux jours.</li><li>Le support sera renforcé la semaine du lancement.</li></ul>
<h2>Décisions</h2><ul><li>Lancement maintenu au 14 novembre.</li><li>Budget marketing de 12 k€ validé.</li></ul>
<h2>Actions à mener</h2><table><thead><tr><th>Action</th><th>Responsable</th><th>Échéance</th></tr></thead><tbody><tr><td>Finaliser la documentation</td><td>Sarah</td><td>8 novembre</td></tr><tr><td>Script de migration des comptes</td><td>Moi</td><td>10 novembre</td></tr></tbody></table>
<h2>Déroulé</h2><ul><li><button class="t" data-t="12">[00:00:12]</button> Tour de table</li><li><button class="t" data-t="245">[00:04:05]</button> Résultats des tests de charge</li><li><button class="t" data-t="1260">[00:21:00]</button> Budget marketing</li></ul>`,
    voices: [
      { voice: 1, name: 'Thomas', talk: 1140 },
      { voice: 2, name: 'Sarah', talk: 760 },
      { voice: 3, name: 'Intervenant 3', talk: 95 },
    ],
    meTalk: 540,
    paragraphs: [
      { start: 12, end: 40, speaker: 'others', voice: 1, name: 'Thomas', text: 'Bonjour à tous, on commence par un rapide tour de table sur le lancement.' },
      { start: 41, end: 75, speaker: 'me', voice: 0, name: 'Moi', text: 'De mon côté, le script de migration est presque prêt, il me reste les comptes avec plusieurs espaces de travail.' },
      { start: 76, end: 120, speaker: 'others', voice: 1, name: 'Thomas', text: 'Parfait. Sarah, tu peux nous dire où en est la documentation ?' },
      { start: 121, end: 170, speaker: 'others', voice: 2, name: 'Sarah', text: 'Elle sera prête pour le 8 novembre, il me manque juste la partie migration.' },
      { start: 245, end: 300, speaker: 'others', voice: 3, name: 'Intervenant 3', text: 'Les tests de charge sont bons : trois mille utilisateurs simultanés, aucune dégradation des temps de réponse.' },
    ],
    audioUrl: '',
    hasReport: true,
    myName: 'Moi',
    othersName: 'Participants',
    exactNames: false,
  };

  const live = discordOn()
    ? { meSpeaking: false, meTalk: 212, current: 2, channel: 'Raid · Les Copains', exact: true, list: [
      { id: 1, name: 'Thomas_92', speaking: false, talk: 301, muted: false, present: true },
      { id: 2, name: 'Sarah ✦', speaking: true, talk: 188, muted: false, present: true },
      { id: 3, name: 'xX_Kev_Xx', speaking: false, talk: 41, muted: true, present: true },
      { id: 4, name: 'Lucas', speaking: false, talk: 12, muted: false, present: false },
    ] }
    : { meSpeaking: false, meTalk: 212, current: 2, list: [
      { id: 1, name: 'Thomas', speaking: false, talk: 301 },
      { id: 2, name: 'Intervenant 2', speaking: true, talk: 188 },
      { id: 3, name: 'Intervenant 3', speaking: false, talk: 41 },
    ] };
  function tick() {
    if (!state.recording) return;
    const t = performance.now() / 1000;
    state.recording.elapsed = (Date.now() - startedAt) / 1000;
    state.recording.levels = {
      others: Math.abs(Math.sin(t * 2.1)) * 0.35 + Math.random() * 0.08,
      me: state.recording.muted ? 0 : Math.abs(Math.sin(t * 1.3 + 1)) * 0.25 * (Math.random() > 0.4 ? 1 : 0.05),
    };
    const who = Math.floor(t / 3) % 3;
    live.list.forEach((s, i) => { s.speaking = i === who && s.present !== false && !s.muted; if (s.speaking) s.talk += 0.25; });
    live.current = live.list.find(s => s.speaking)?.id ?? null;
    state.recording.speakers = structuredClone(live);
    emit('state', structuredClone(state));
  }
  setInterval(tick, 250);

  if (params.get('recording')) {
    startedAt = Date.now() - 754e3;
    const viaDiscord = state.selectedApp === 'discord' && discordOn();
    state.recording = { app: state.selectedApp, elapsed: 754, manual: true, muted: params.get('muted') === '1', muteSource: viaDiscord ? 'discord' : 'window', pushToTalk: viaDiscord && params.get('ptt') === '1', levels: { me: 0.2, others: 0.3 }, speakers: structuredClone(live) };
    state.state = 'recording';
  }

  const handlers = {
    'app.info': () => ({ version: '2.0.0 (aperçu)', startPage: params.get('page') ?? 'record' }),
    state: () => structuredClone(state),
    'app.select': ({ app }) => { state.selectedApp = app; },
    'recording.start': ({ app }) => {
      startedAt = Date.now();
      state.recording = { app, elapsed: 0, manual: true, muted: false, muteSource: 'window', levels: { me: 0, others: 0 } };
      state.state = 'recording';
      emit('state', structuredClone(state));
      return true;
    },
    'recording.stop': () => {
      state.recording = null;
      state.state = 'ready';
      emit('state', structuredClone(state));
      emit('notify', { title: 'Enregistrement terminé', text: 'Transcription et synthèse en cours…', level: 'info' });
    },
    'recording.setAuto': args => {
      if (args.autoRecord !== undefined) state.autoRecord = args.autoRecord;
      if (args.autoStop !== undefined) state.autoStop = args.autoStop;
    },
    'recordings.list': () => structuredClone(recordings),
    'recordings.get': ({ id }) => ({ ...structuredClone(detail), summary: structuredClone(recordings.find(r => r.id === id) ?? recordings[1]) }),
    'recordings.rename': () => true,
    'recordings.renameVoice': ({ voice, name }) => {
      const same = detail.voices.find(x => x.voice !== voice && x.name.toLowerCase() === name.toLowerCase());
      const v = detail.voices.find(x => x.voice === voice);
      if (same && v) {
        same.talk += v.talk; same.name = name;
        detail.voices = detail.voices.filter(x => x !== v);
        detail.paragraphs.forEach(p => { if (p.voice === voice || p.voice === same.voice) { p.voice = same.voice; p.name = name; } });
        return true;
      }
      if (v) v.name = name;
      detail.paragraphs.forEach(p => { if (p.voice === voice) p.name = name; });
      return true;
    },
    'recordings.resummarize': () => true,
    'speakers.renameLive': ({ id, name }) => {
      const s = live.list.find(x => x.id === id);
      const same = live.list.find(x => x.id !== id && x.name.toLowerCase() === name.toLowerCase());
      if (s && same) { same.talk += s.talk; live.list = live.list.filter(x => x !== s); return true; }
      if (s) s.name = name;
      return true;
    },
    'recordings.reprocess': () => true,
    'recordings.delete': () => true,
    'recordings.openFolder': () => {},
    'recordings.openReport': () => {},
    'settings.get': () => structuredClone(settings),
    'settings.options': () => ({
      microphones: [{ id: 'mic-1', name: 'Casque (Jabra Evolve2 65)' }, { id: 'mic-2', name: 'Microphone (Realtek Audio)' }],
      models: [
        { file: 'ggml-large-v3-turbo-q5_0.bin', label: 'Précis — large-v3-turbo', sizeMb: 547, installed: true },
        { file: 'ggml-small-q5_1.bin', label: 'Équilibré — small', sizeMb: 181, installed: false },
        { file: 'ggml-base-q5_1.bin', label: 'Rapide — base', sizeMb: 57, installed: false },
      ],
      languages: [{ code: 'fr', name: 'Français' }, { code: 'en', name: 'English' }, { code: 'auto', name: 'Détection automatique' }],
    }),
    'settings.save': s => { Object.assign(settings, s, { apiKey: undefined }); return structuredClone(settings); },
    'settings.setTheme': ({ theme }) => { settings.theme = theme; state.theme = theme; },
    'settings.browseFolder': () => null,
    'teams.pair': () => true,
    'discord.connect': async ({ clientId }) => {
      if (!/^\d{15,22}$/.test(clientId)) throw new Error('Client ID invalide : c\'est une suite de chiffres (portail développeur Discord → votre application → OAuth2 → Client ID).');
      await new Promise(r => setTimeout(r, 1500));
      settings.discordClientId = clientId; settings.hasDiscordSecret = true;
      state.discord = { status: 'connected', error: null, user: 'Emerick', inVoice: false, channel: null, participants: 0 };
      emit('state', structuredClone(state));
      return { user: 'Emerick' };
    },
    'discord.disconnect': () => { state.discord = { ...state.discord, status: 'needsAuth', inVoice: false }; emit('state', structuredClone(state)); },
    'app.openExternal': () => {},
    'app.openOutputFolder': () => {},
    'app.openLogs': () => {},
  };

  return {
    async call(type, args = {}) {
      const handler = handlers[type];
      if (!handler) throw new Error(`Inconnu : ${type}`);
      return handler(args);
    },
    on(event, fn) {
      if (!listeners.has(event)) listeners.set(event, []);
      listeners.get(event).push(fn);
    },
  };
}
