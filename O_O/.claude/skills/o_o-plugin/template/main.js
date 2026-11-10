// A minimal O_O plugin: a page with a button, and a chip on the collapsed island counting the clicks.
import { island, ui, settings, storage } from '@island/api';

export function activate() {
  let count = storage.get('count') ?? 0;

  function render() {
    island.page('main').update(ui.stack({ gap: 'md' }, [
      ui.text(settings.get('greeting'), { size: 'xl', weight: 'semibold' }),
      ui.row({ gap: 'sm', align: 'center' }, [
        ui.text(`已点击 ${count} 次`, { tone: 'secondary', tabular: true }),
        ui.spacer(),
        ui.button('点一下', { variant: 'ghost', onClick: () => { count++; storage.set('count', count); render(); } }),
      ]),
    ]));
    if (count === 0) island.chip('count').remove();
    else island.chip('count').update(ui.row({ gap: 'xs', align: 'center' }, [
      ui.icon('Hand', { tone: 'secondary', size: 14 }),
      ui.text(String(count), { size: 'sm', weight: 'semibold', tabular: true }),
    ]));
  }

  settings.onChange(render);
  render();
}
