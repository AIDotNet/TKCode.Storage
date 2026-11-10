// The 2.0 island API (docs/design/09-island-v2.md §3): the plugin fills fixed regions; the host decides where they show.
import { island, ui } from '@island/api';

export function activate() {
  island.ambient('temp').update(ui.row({ gap: 'xs', align: 'center' }, [ui.icon('Sun', { size: 14 }), ui.text('17°', { size: 'sm' })]));
  island.home.page('demo').update(ui.text('这是主页里的一页。'));

  const started = Date.now();
  const left = () => {
    const s = Math.max(0, 25 * 60 - Math.floor((Date.now() - started) / 1000));
    return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
  };
  const timerView = () => ({
    compact: { leading: ui.icon('Timer', { size: 16 }), trailing: ui.text(left(), { size: 'sm', weight: 'semibold', tabular: true }) },
    minimal: ui.icon('Timer', { size: 16 }),
    expanded: {
      leading: ui.icon('Timer', { size: 28 }),
      center: ui.stack({ gap: 'xs' }, [ui.text('专注', { weight: 'semibold' }), ui.text('写设计文档', { size: 'sm', tone: 'secondary' })]),
      trailing: ui.text(left(), { size: 'lg', weight: 'semibold', tabular: true }),
      bottom: ui.row({ gap: 'sm' }, [ui.button('暂停'), ui.button('结束', { onClick: () => timer.end() })]),
    },
  });
  const timer = island.activity('timer', timerView());
  setInterval(() => timer.update(timerView()), 1000);

  island.activity('playing', {
    open: { app: 'com.netease.163music' },
    compact: { leading: ui.icon('Music', { size: 16 }), trailing: ui.text('晴天', { size: 'sm' }) },
    minimal: ui.icon('Music', { size: 16 }),
    expanded: { leading: ui.icon('Music', { size: 28 }), center: ui.text('晴天 · 周杰伦', { weight: 'semibold' }) },
  });
}
