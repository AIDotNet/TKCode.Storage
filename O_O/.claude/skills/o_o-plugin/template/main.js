// A minimal O_O plugin (island API 2.0): a home page with buttons, an ambient item counting the clicks, and a timer
// that is a live activity while it runs.
import { island, ui, settings, storage } from '@island/api';

export function activate() {
  let count = storage.get('count') ?? 0;
  let startedAt = null;
  let timer = null;   // the live activity while the timer runs
  let ticker = null;

  const elapsed = () => {
    const s = Math.floor((Date.now() - startedAt) / 1000);
    return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
  };

  // A live activity: fixed regions the island lays out; the host decides where it shows.
  const timerView = () => ({
    compact: { leading: ui.icon('Timer', { tone: 'accent', size: 14 }), trailing: ui.text(elapsed(), { size: 'sm', weight: 'semibold', tabular: true }) },
    minimal: ui.icon('Timer', { tone: 'accent', size: 14 }),
    expanded: {
      leading: ui.icon('Timer', { tone: 'accent', size: 24 }),
      center: ui.text('计时中', { weight: 'semibold' }),
      trailing: ui.text(elapsed(), { size: '2xl', weight: 'semibold', tabular: true }),
      bottom: ui.button('停止', { variant: 'ghost', onClick: stop }),
    },
  });

  function start() {
    startedAt = Date.now();
    timer = island.activity('timer', timerView());
    ticker = setInterval(() => timer.update(timerView()), 1000);
    render();
  }

  function stop() {
    clearInterval(ticker);
    timer?.end();
    timer = null;
    render();
  }

  function render() {
    island.home.page('main').update(ui.stack({ gap: 'md' }, [
      ui.text(settings.get('greeting'), { size: 'xl', weight: 'semibold' }),
      ui.row({ gap: 'sm', align: 'center' }, [
        ui.text(`已点击 ${count} 次`, { tone: 'secondary', tabular: true }),
        ui.spacer(),
        ui.button('点一下', { variant: 'ghost', onClick: () => { count++; storage.set('count', count); render(); } }),
        ui.button(timer ? '停止计时' : '开始计时', { variant: 'ghost', onClick: timer ? stop : start }),
      ]),
    ]));
    if (count === 0) island.ambient('count').remove();
    else island.ambient('count').update(ui.row({ gap: 'xs', align: 'center' }, [
      ui.icon('Hand', { tone: 'secondary', size: 14 }),
      ui.text(String(count), { size: 'sm', weight: 'semibold', tabular: true }),
    ]));
  }

  settings.onChange(render);
  render();
}
