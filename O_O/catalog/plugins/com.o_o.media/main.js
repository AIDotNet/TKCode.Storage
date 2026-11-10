// First-party music plugin (docs/design/05-builtin-plugins.md §1): what is playing, with play/pause and skip. While a
// player plays or is paused, a live activity shows the song on the island and its card has the controls
// (docs/design/09-island-v2.md); the home page has the same.
import { island, ui, media } from '@island/api';

const time = (s) => {
  if (s == null || !Number.isFinite(s)) return '';
  const t = Math.max(0, Math.floor(s));
  return `${Math.floor(t / 60)}:${String(t % 60).padStart(2, '0')}`;
};

const cut = (s, n) => ([...String(s)].length > n ? `${[...String(s)].slice(0, n).join('')}…` : String(s));

export async function activate() {
  let now = null;          // { app, title, artist, album, playing, position, duration }
  let at = Date.now();     // when `now.position` was true
  let act = null;
  let ticker = null;

  const position = () => now?.position == null ? null
    : Math.min(now.position + (now.playing ? (Date.now() - at) / 1000 : 0), now.duration ?? Infinity);

  const send = async (command) => {
    await media.command(command);
    // Players report the new state themselves where they can; ask again shortly for the others.
    setTimeout(async () => set(await media.nowPlaying()), 600);
  };

  const controls = () => ui.row({ gap: 'md', align: 'center' }, [
    ui.spacer(),
    ui.iconButton('SkipBack', { label: '上一首', onClick: () => send('previous') }),
    ui.iconButton(now?.playing ? 'Pause' : 'Play', { label: now?.playing ? '暂停' : '播放', onClick: () => send(now?.playing ? 'pause' : 'toggle') }),
    ui.iconButton('SkipForward', { label: '下一首', onClick: () => send('next') }),
    ui.spacer(),
  ]);

  function render() {
    const p = position();
    const byline = [now?.artist, now?.album].filter(Boolean).join(' · ');
    island.home.page('now').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      now
        ? ui.row({ gap: 'md', align: 'center' }, [
            ui.icon(now.playing ? 'AudioLines' : 'Music', { size: 28, tone: now.playing ? 'accent' : 'secondary' }),
            ui.stack({ gap: 'xs', grow: true }, [
              ui.text(now.title ?? '未知曲目', { size: 'lg', weight: 'semibold' }),
              byline ? ui.text(byline, { tone: 'secondary', size: 'sm' }) : null,
            ]),
          ])
        : ui.stack({ gap: 'xs' }, [
            ui.text('没有正在播放的内容', { weight: 'semibold' }),
            ui.text('在 Mac 上可以显示"音乐"和 Spotify 正在播放的歌曲；下面的按钮可以控制任何播放器。', { tone: 'secondary', size: 'sm', wrap: true }),
          ]),
      now?.duration ? ui.progress({ value: p ?? 0, max: now.duration }) : null,
      now?.duration ? ui.row({}, [ui.text(time(p), { tone: 'secondary', size: 'xs', tabular: true }), ui.spacer(),
                                  ui.text(time(now.duration), { tone: 'secondary', size: 'xs', tabular: true })]) : null,
      controls(),
    ]), { status: now ? ui.text(now.app, { tone: 'secondary', size: 'sm' }) : undefined });
  }

  const activityView = () => {
    const p = position();
    const playing = !!now.playing;
    return {
      priority: playing ? 'normal' : 'passive',
      compact: {
        // No `source`: the bars move by themselves. Following the real loudness means tapping the player's audio (macOS's
        // "System Audio Recording" permission), which this plugin does not do.
        leading: ui.row({ gap: 'xs', align: 'center' }, [
          ui.icon('Music', { size: 14 }),
          // Short enough for the island: a longer title is cut with "…" instead of being clipped mid-character.
          ui.text(cut(now.title ?? now.app, 6), { size: 'sm', weight: 'semibold' }),
        ]),
        trailing: ui.spectrum({ bars: 4, active: playing }),
      },
      minimal: ui.spectrum({ bars: 3, active: playing }),
      expanded: {
        leading: ui.icon(playing ? 'AudioLines' : 'Music', { size: 28, tone: playing ? 'accent' : 'secondary' }),
        center: ui.stack({ gap: 'xs' }, [
          ui.text(now.title ?? '未知曲目', { size: 'lg', weight: 'semibold', lines: 1 }),
          [now.artist, now.album].filter(Boolean).length ? ui.text([now.artist, now.album].filter(Boolean).join(' · '), { tone: 'secondary', size: 'sm', lines: 1 }) : null,
        ]),
        trailing: controls(),
        bottom: now.duration ? ui.stack({ gap: 'xs', align: 'stretch' }, [
          ui.progress({ value: p ?? 0, max: now.duration }),
          ui.row({}, [ui.text(time(p), { tone: 'secondary', size: 'xs', tabular: true }), ui.spacer(),
                      ui.text(time(now.duration), { tone: 'secondary', size: 'xs', tabular: true })]),
        ]) : undefined,
      },
    };
  };

  function sync() {
    if (now) {
      if (act) act.update(activityView()); else act = island.activity('playing', activityView());
    } else {
      act?.end();
      act = null;
    }
    render();
  }

  function set(next) {
    now = next;
    at = Date.now();
    if (now?.playing) {
      ticker ??= setInterval(sync, 1000); // the progress moves
    } else if (ticker !== null) {
      clearInterval(ticker);
      ticker = null;
    }
    sync();
  }

  media.onChange(set);
  render();
  set(await media.nowPlaying());
}
