// First-party clock plugin (docs/design/05-builtin-plugins.md §2). Same API as any third-party plugin.
import { island, ui } from '@island/api';

const WEEKDAYS = ['星期日', '星期一', '星期二', '星期三', '星期四', '星期五', '星期六'];
const pad = (n) => String(n).padStart(2, '0');

export function activate() {

  // A stopwatch, so the expanded view has something to press: proves UI events round-trip to the plugin.
  let startedAt = null;
  let elapsedMs = 0;
  let ticker = null;

  const stopwatchText = () => {
    const ms = elapsedMs + (startedAt === null ? 0 : Date.now() - startedAt);
    const s = Math.floor(ms / 1000);
    return `${pad(Math.floor(s / 60))}:${pad(s % 60)}.${Math.floor((ms % 1000) / 100)}`;
  };

  const toggle = () => {
    if (startedAt === null) {
      startedAt = Date.now();
      ticker = setInterval(render, 100);
    } else {
      elapsedMs += Date.now() - startedAt;
      startedAt = null;
      clearInterval(ticker);
      ticker = null;
    }
    render();
  };

  const reset = () => {
    elapsedMs = 0;
    if (startedAt !== null) startedAt = Date.now();
    render();
  };

  // The stopwatch is a live activity while it runs or holds a time (docs/design/09-island-v2.md); the time of day is
  // never shown on the island. The full clock lives on the optional 今日 home page, off until the user turns it on.
  let activity = null;
  const controls = (running) => ui.row({ gap: 'sm' }, [
    ui.button(running ? '暂停' : '继续', { variant: 'ghost', onClick: toggle }),
    ui.button('结束', { variant: 'ghost', onClick: () => { elapsedMs = 0; startedAt = null; clearInterval(ticker); ticker = null; render(); } }),
  ]);
  function render() {
    const now = new Date();
    const running = startedAt !== null;
    if (running || elapsedMs > 0) {
      const spec = {
        priority: running ? 'normal' : 'passive',
        compact: { leading: ui.icon('Timer', { size: 14 }), trailing: ui.text(stopwatchText(), { size: 'sm', weight: 'semibold', tabular: true }) },
        minimal: ui.icon('Timer', { size: 14 }),
        expanded: {
          leading: ui.icon('Timer', { size: 24 }),
          center: ui.text(stopwatchText(), { size: '2xl', weight: 'semibold', tabular: true }),
          bottom: controls(running),
        },
      };
      if (activity) activity.update(spec); else activity = island.activity('stopwatch', spec);
    } else if (activity) {
      activity.end();
      activity = null;
    }
    island.home.page('today').update(ui.stack({ gap: 'md' }, [
      ui.row({ gap: 'md', align: 'end' }, [
        ui.text(`${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`,
          { size: '4xl', tabular: true }),
        ui.text(`${now.getMonth() + 1}月${now.getDate()}日 ${WEEKDAYS[now.getDay()]}`, { tone: 'secondary' }),
      ]),
      ui.row({ gap: 'sm' }, [
        ui.icon('Timer', { tone: 'secondary' }),
        ui.text(stopwatchText(), { size: 'lg', tabular: true, key: 'sw' }),
        ui.spacer(),
        ui.button('重置', { variant: 'ghost', onClick: reset, disabled: elapsedMs === 0 && !running }),
        ui.button(running ? '暂停' : '开始计时', { variant: 'ghost', onClick: toggle }),
      ]),
    ]));
  }

  render();
  // Re-render on every second boundary (the 今日 page shows seconds).
  setTimeout(() => { render(); setInterval(() => { if (startedAt === null) render(); }, 1000); },
    1000 - (Date.now() % 1000));
}
