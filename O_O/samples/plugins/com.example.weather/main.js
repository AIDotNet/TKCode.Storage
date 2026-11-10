// Sample third-party plugin: network access (needs the user's consent), settings with live updates, storage cache.
// Uses Open-Meteo (no API key). Copy this folder into <app data>/O_O/plugins/ to install it.
import { island, ui, http, settings, storage } from '@island/api';

export async function activate() {
  const act = island.activity.create('now', { title: '天气', priority: 'passive' });
  let timer = null;

  const render = (w) => {
    const c = settings.all();
    const f = c.units === '华氏度';
    const t = (v) => `${Math.round(f ? v * 9 / 5 + 32 : v)}°`;
    act.update({
      compact: { leading: ui.icon('CloudSun'), trailing: ui.text(t(w.temperature), { weight: 'semibold', tabular: true }) },
      expanded: ui.stack({ gap: 'sm' }, [
        ui.row({ gap: 'md', align: 'end' }, [
          ui.text(t(w.temperature), { size: '4xl', tabular: true }),
          ui.text(c.city, { size: 'lg', tone: 'secondary' }),
        ]),
        c.showWind ? ui.text(`风速 ${w.windspeed} km/h`, { tone: 'secondary' }) : null,
        ui.text(`更新于 ${w.time.slice(11, 16)}`, { size: 'xs', tone: 'secondary' }),
      ]),
    });
  };

  const refresh = async () => {
    const { latitude, longitude } = settings.all();
    try {
      // http is undefined if the user did not grant network access: fall back to the cached reading.
      if (!http) throw new Error('没有网络权限');
      const data = await http.getJson(
        `https://api.open-meteo.com/v1/forecast?latitude=${latitude}&longitude=${longitude}&current_weather=true`);
      storage.set('last', data.current_weather);
      render(data.current_weather);
    } catch (e) {
      console.warn('weather refresh failed:', e.message);
      const last = storage.get('last');
      if (last) render(last);
    }
  };

  const schedule = () => {
    clearInterval(timer);
    timer = setInterval(refresh, settings.get('every') * 60 * 1000);
  };

  settings.onChange((key) => {
    if (key === 'every') schedule();
    refresh();
  });
  schedule();
  await refresh();
}
