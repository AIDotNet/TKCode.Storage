// NetEase Cloud Music (网易云音乐). Listens to what the media capability reports; while the NetEase client plays, a chip
// on the right of the collapsed island shows bars that dance (ui.spectrum, a made-up motion: reading the real loudness would mean tapping the app's audio, which
// lights macOS's recording indicator). The page shows the song, a scrolling lyric and the
// charts. Songs and lyrics come from NetEase's unofficial public web endpoints (no login); every call may fail, and
// the plugin then simply shows less. Only the song title and artist are ever sent.
import { island, ui, media, http, settings, shell } from '@island/api';

const API = 'https://music.163.com';
const PLAYER = /网易云|netease|cloudmusic|163/i;
const CHARTS = [
  { id: '3778678', title: '热歌榜' },
  { id: '19723756', title: '飙升榜' },
  { id: '3779629', title: '新歌榜' },
];
const PER_PAGE = 5;
const MINUTE = 60 * 1000;

const time = (s) => {
  if (s == null || !Number.isFinite(s)) return '';
  const t = Math.max(0, Math.floor(s));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`;
};
const cut = (s, n) => ([...s].length > n ? `${[...s].slice(0, n).join('')}…` : s);

/** LRC text → [{ at (seconds), text }] sorted by time; metadata lines and empty lines are dropped. */
export const parseLrc = (lrc) => {
  const lines = [];
  for (const raw of String(lrc ?? '').split(/\r?\n/)) {
    const stamps = [...raw.matchAll(/\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]/g)];
    const text = raw.replace(/\[[^\]]*\]/g, '').trim();
    if (stamps.length === 0 || text === '') continue;
    for (const m of stamps) {
      const frac = m[3] ? Number(`0.${m[3]}`) : 0;
      lines.push({ at: Number(m[1]) * 60 + Number(m[2]) + frac, text });
    }
  }
  return lines.sort((a, b) => a.at - b.at);
};

/** The index of the line being sung at `seconds` (-1 before the first). */
export const lineAt = (lines, seconds) => {
  let lo = 0, hi = lines.length - 1, found = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (lines[mid].at <= seconds) { found = mid; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
};

export async function activate() {
  let now = null;                 // the player's state when it is NetEase: { app, title, artist, playing, position?, duration? }
  let anchor = { key: '', startedAt: 0, pausedAt: 0, pausedTotal: 0 }; // our own clock for players that give no position
  let lyrics = { key: '', lines: [], duration: null, state: 'none' };   // state: none | loading | ready | missing
  let view = 'now';
  let chart = 0;
  let tracks = {};                // chart id → [{ id, title, artist }]
  let page = 0;
  let error = null;
  let act = null;
  let ticker = null;

  const isNetease = (n) => !!n && PLAYER.test(n.app ?? '');
  const keyOf = (n) => `${n?.title ?? ''}\u0000${n?.artist ?? ''}`;

  // ---- the position of the song ------------------------------------------------------------------------------------

  /** Seconds into the song: the player's own position when it gives one, otherwise our estimate from when it started. */
  const position = () => {
    if (!now) return null;
    if (now.position != null) return now.position;
    if (!anchor.startedAt) return null;
    const end = now.playing ? Date.now() : anchor.pausedAt;
    return Math.max(0, (end - anchor.startedAt - anchor.pausedTotal) / 1000);
  };

  const track = (next) => {
    const was = now;
    now = isNetease(next) ? next : null;
    if (!now) { anchor = { key: '', startedAt: 0, pausedAt: 0, pausedTotal: 0 }; return; }
    const key = keyOf(now);
    if (key !== anchor.key) {
      anchor = { key, startedAt: Date.now(), pausedAt: 0, pausedTotal: 0 };
      loadLyrics();
    } else if (was && !was.playing && now.playing && anchor.pausedAt) {
      anchor.pausedTotal += Date.now() - anchor.pausedAt;       // resumed: the pause does not count
      anchor.pausedAt = 0;
    } else if (was?.playing && !now.playing) {
      anchor.pausedAt = Date.now();
    }
  };

  // ---- lyrics -------------------------------------------------------------------------------------------------------

  const get = async (path) => {
    const res = await http.fetch(`${API}${path}`, { headers: { Accept: 'application/json', Referer: `${API}/` } });
    if (!res.ok) throw new Error(`网易云返回 ${res.status}`);
    return res.json();
  };

  async function loadLyrics() {
    const mine = keyOf(now);
    if (!now?.title || settings.get('lyrics') === false) { lyrics = { key: mine, lines: [], duration: null, state: 'none' }; return; }
    lyrics = { key: mine, lines: [], duration: null, state: 'loading' };
    try {
      const q = encodeURIComponent([now.title, now.artist].filter(Boolean).join(' '));
      const found = await get(`/api/search/get/web?csrf_token=&hlpretag=&hlposttag=&s=${q}&type=1&offset=0&total=true&limit=3`);
      const song = found?.result?.songs?.[0];
      if (!song) throw new Error('找不到这首歌');
      const lrc = await get(`/api/song/lyric?id=${song.id}&lv=1&kv=1&tv=-1`);
      if (mine !== lyrics.key) return;                               // the song changed meanwhile
      const lines = parseLrc(lrc?.lrc?.lyric);
      lyrics = { key: mine, lines, duration: song.duration ? song.duration / 1000 : null, state: lines.length > 0 ? 'ready' : 'missing' };
    } catch (e) {
      if (mine === lyrics.key) lyrics = { key: mine, lines: [], duration: null, state: 'missing' };
      error = null;
    }
    render();
  }

  async function loadChart(index) {
    const c = CHARTS[index];
    if (tracks[c.id]) return;
    try {
      const data = await get(`/api/playlist/detail?id=${c.id}`);
      tracks[c.id] = (data?.result?.tracks ?? []).slice(0, 50).map((t) => ({
        id: String(t.id), title: t.name ?? '', artist: (t.artists ?? []).map((a) => a.name).join(' / '),
      }));
      error = null;
    } catch (e) {
      error = `榜单读取失败：${e.message}`;
    }
    render();
  }

  const openSong = async (id) => {
    try { await shell.openUrl(`${API}/#/song?id=${id}`); } catch (e) { error = e.message; render(); }
  };

  // ---- views --------------------------------------------------------------------------------------------------------

  const send = async (command) => {
    try { await media.command(command); } catch { /* the player may not take commands */ }
    setTimeout(async () => { track(await media.nowPlaying()); sync(); }, 700);
  };

  const lyricBlock = () => {
    if (settings.get('lyrics') === false || !now?.title) return null;
    if (lyrics.state === 'loading') return ui.text('正在查找歌词…', { tone: 'secondary', size: 'sm' });
    if (lyrics.state !== 'ready') return ui.text('没有找到这首歌的歌词', { tone: 'secondary', size: 'sm' });
    const p = position();
    const i = p == null ? -1 : lineAt(lyrics.lines, p);
    const around = (k) => lyrics.lines[k]?.text ?? '';
    return ui.stack({ gap: 'xs', align: 'stretch' }, [
      ui.text(around(i - 1), { tone: 'secondary', size: 'xs', align: 'center' }),
      ui.text(i >= 0 ? around(i) : '♪', { weight: 'semibold', tone: 'accent', align: 'center', lines: 1 }),
      ui.text(around(i + 1), { tone: 'secondary', size: 'xs', align: 'center' }),
    ]);
  };

  const nowView = () => {
    if (!now) {
      return ui.stack({ gap: 'xs' }, [
        ui.text('网易云音乐没有在播放', { weight: 'semibold' }),
        ui.text('在网易云里播放一首歌，这里就会显示歌名、歌词，收起的岛右侧会出现随音乐跳动的律动条。', { tone: 'secondary', size: 'sm', wrap: true }),
      ]);
    }
    const p = position();
    const total = now.duration ?? lyrics.duration;
    return ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.row({ gap: 'md', align: 'center' }, [
        ui.spectrum({ bars: 9, active: !!now.playing }),
        ui.stack({ gap: 'xs', grow: true }, [
          ui.text(now.title ?? '网易云音乐', { size: 'lg', weight: 'semibold', lines: 1 }),
          now.artist ? ui.text(now.artist, { tone: 'secondary', size: 'sm', lines: 1 }) : null,
        ]),
        ui.iconButton('SkipBack', { label: '上一首', onClick: () => send('previous') }),
        ui.iconButton(now.playing ? 'Pause' : 'Play', { label: now.playing ? '暂停' : '播放', onClick: () => send('toggle') }),
        ui.iconButton('SkipForward', { label: '下一首', onClick: () => send('next') }),
      ]),
      total && p != null ? ui.progress({ value: Math.min(p, total), max: total }) : null,
      total && p != null ? ui.row({}, [ui.text(time(p), { tone: 'secondary', size: 'xs', tabular: true }), ui.spacer(),
                                       ui.text(time(total), { tone: 'secondary', size: 'xs', tabular: true })]) : null,
      lyricBlock(),
    ]);
  };

  const chartView = () => {
    const list = tracks[CHARTS[chart].id];
    const pages = Math.max(1, Math.ceil((list?.length ?? 0) / PER_PAGE));
    page = Math.min(page, pages - 1);
    return ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.row({ gap: 'xs', align: 'center' }, [
        ui.tabs({ value: String(chart), onChange: (v) => { chart = Number(v); page = 0; loadChart(chart); render(); } },
          CHARTS.map((c, i) => ui.option(String(i), c.title))),
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
      ]),
      list ? ui.stack({ gap: 'xs', align: 'stretch' }, list.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map((t, i) =>
        ui.item({ key: t.id, gap: 'sm', align: 'center', onClick: () => openSong(t.id) }, [
          ui.text(String(page * PER_PAGE + i + 1), { tone: 'secondary', size: 'xs', tabular: true, width: 18 }),
          ui.text(t.title, { grow: true, weight: 'medium' }),
          ui.text(cut(t.artist, 14), { tone: 'secondary', size: 'xs' }),
        ])))
        : ui.text(error ?? '正在读取榜单…', { tone: 'secondary' }),
      pages > 1 ? ui.dots({ count: pages, index: page }) : null,
    ]);
  };

  function render() {
    island.page('now').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.tabs({ value: view, onChange: (v) => { view = v; if (v === 'charts') loadChart(chart); render(); } },
        [ui.option('now', '正在播放'), ui.option('charts', '榜单')]),
      view === 'now' ? nowView() : chartView(),
      error && view === 'now' ? ui.text(error, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: now ? ui.text(now.playing ? '播放中' : '已暂停', { tone: 'secondary', size: 'sm' }) : undefined });
  }

  // ---- the island ---------------------------------------------------------------------------------------------------

  function sync() {
    const playing = !!now?.playing;
    if (playing) {
      const p = position();
      const i = p == null || lyrics.state !== 'ready' ? -1 : lineAt(lyrics.lines, p);
      const line = settings.get('chipLyric') && i >= 0 ? lyrics.lines[i].text : null;
      island.chip('wave').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.spectrum({ bars: Math.min(9, Math.max(3, settings.get('bars') ?? 5)), active: true }),
        line ? ui.text(cut(line, 6), { size: 'xs', tone: 'secondary' }) : null,
      ]));
      act ??= island.activity.create('playing', { title: '网易云播放中', priority: 'passive', page: 'now' });
      act.update({});
      ticker ??= setInterval(sync, 1000);   // lyric lines and the progress move
    } else {
      island.chip('wave').remove();
      act?.end();
      act = null;
      if (ticker !== null) { clearInterval(ticker); ticker = null; }
    }
    render();
  }

  media.onChange((next) => { track(next); sync(); });
  settings.onChange(() => { if (now) loadLyrics(); sync(); });
  track(await media.nowPlaying());
  sync();
}
