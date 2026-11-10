// Weather from Open-Meteo (https://open-meteo.com): no key. The city name is looked up once (geocoding API), then
// GET /v1/forecast gives the current weather and the next hours. A chip shows the temperature; the page the details.
import { island, ui, http, settings, storage } from '@island/api';

const GEO = 'https://geocoding-api.open-meteo.com/v1/search';
const API = 'https://api.open-meteo.com/v1/forecast';
const HOURS = 6;

// WMO weather codes → [Chinese name, Lucide icon].
const CODES = [
  [[0], '晴', 'Sun'], [[1, 2], '多云', 'CloudSun'], [[3], '阴', 'Cloud'], [[45, 48], '雾', 'CloudFog'],
  [[51, 53, 55, 56, 57], '毛毛雨', 'CloudDrizzle'], [[61, 63, 65, 66, 67, 80, 81, 82], '雨', 'CloudRain'],
  [[71, 73, 75, 77, 85, 86], '雪', 'CloudSnow'], [[95, 96, 99], '雷暴', 'CloudLightning'],
];
const describe = (code) => { const c = CODES.find(([codes]) => codes.includes(code)); return c ? { text: c[1], icon: c[2] } : { text: '未知', icon: 'Cloud' }; };

export async function activate() {
  const cached = storage.get('cache') ?? null;  // { place, data, at }
  let state = cached;
  let error = null;
  let timer = null;

  const fahrenheit = () => settings.get('unit') === '华氏度';
  const deg = (t) => `${Math.round(t)}°`;

  const locate = async (city) => {
    if (state?.place?.query === city) return state.place;
    const res = await http.fetch(`${GEO}?name=${encodeURIComponent(city)}&count=1&language=zh`, { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(`城市查询返回 ${res.status}`);
    const hit = res.json().results?.[0];
    if (!hit) throw new Error(`找不到城市“${city}”`);
    return { query: city, name: hit.name, lat: hit.latitude, lon: hit.longitude };
  };

  const refresh = async () => {
    const city = String(settings.get('city') ?? '').trim();
    try {
      if (!city) throw new Error('先在控制台 › 插件 › 天气 中填写城市');
      const place = await locate(city);
      const q = `latitude=${place.lat}&longitude=${place.lon}&timezone=auto&forecast_hours=${HOURS + 1}` +
        `&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m` +
        `&hourly=temperature_2m,weather_code,precipitation_probability` +
        (fahrenheit() ? '&temperature_unit=fahrenheit' : '');
      const res = await http.fetch(`${API}?${q}`, { headers: { Accept: 'application/json' } });
      if (!res.ok) throw new Error(`天气接口返回 ${res.status}`);
      state = { place, data: res.json(), at: new Date().toISOString() };
      storage.set('cache', state);
      error = null;
    } catch (e) {
      error = e.message;
    }
    render();
  };

  const render = () => {
    const cur = state?.data?.current;
    if (cur) {
      const w = describe(cur.weather_code);
      island.chip('temp').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.icon(w.icon, { tone: 'secondary', size: 14 }),
        ui.text(deg(cur.temperature_2m), { size: 'sm', weight: 'semibold', tabular: true }),
      ]));
    } else {
      island.chip('temp').remove();
    }

    let body;
    if (!cur) {
      body = ui.text(error ?? '正在读取…', { tone: 'secondary', wrap: true });
    } else {
      const w = describe(cur.weather_code);
      const h = state.data.hourly;
      const hours = (h?.time ?? []).slice(1, HOURS + 1).map((t, i) => ui.stack({ gap: 'xs', align: 'center', key: t, grow: true }, [
        ui.text(t.slice(11, 16), { size: 'xs', tone: 'secondary', tabular: true, align: 'center' }),
        ui.icon(describe(h.weather_code[i + 1]).icon, { size: 16 }),
        ui.text(deg(h.temperature_2m[i + 1]), { size: 'sm', tabular: true, align: 'center' }),
        ui.text(`${h.precipitation_probability?.[i + 1] ?? 0}%`, { size: 'xs', tone: 'secondary', tabular: true, align: 'center' }),
      ]));
      body = ui.stack({ gap: 'md', align: 'stretch' }, [
        ui.row({ gap: 'md', align: 'center' }, [
          ui.icon(w.icon, { size: 40 }),
          ui.text(deg(cur.temperature_2m), { size: '4xl', weight: 'semibold', tabular: true }),
          ui.stack({ gap: 'xs' }, [
            ui.text(`${state.place.name} · ${w.text}`, { weight: 'medium' }),
            ui.text(`体感 ${deg(cur.apparent_temperature)} · 湿度 ${cur.relative_humidity_2m}% · 风 ${Math.round(cur.wind_speed_10m)} km/h`, { size: 'sm', tone: 'secondary' }),
          ]),
        ]),
        ui.row({ gap: 'sm', align: 'start' }, hours),
        error ? ui.text(`更新失败：${error}`, { size: 'xs', tone: 'secondary', wrap: true }) : null,
      ]);
    }
    island.page('now').update(body, { status: ui.text(state?.at ? `更新于 ${state.at.slice(11, 16)}` : '', { tone: 'secondary', size: 'sm' }) });
  };

  const schedule = () => {
    clearInterval(timer);
    timer = setInterval(refresh, Math.max(10, Number(settings.get('every') ?? 30)) * 60 * 1000);
  };

  settings.onChange(() => { schedule(); refresh(); });
  render();
  schedule();
  await refresh();
}
