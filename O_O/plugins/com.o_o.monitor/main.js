// System monitor: CPU, memory, battery and network speed from the host's system.info capability, sampled every few
// seconds while the island is expanded and slowly otherwise. Optionally one alert when the CPU stays high.
import { island, ui, system, settings } from '@island/api';

const GB = 1024 ** 3;
const rate = (b) => (b >= 1024 ** 2 ? `${(b / 1024 ** 2).toFixed(1)} MB/s` : `${Math.round(b / 1024)} KB/s`);

export async function activate() {
  let s = null;
  let high = 0;       // consecutive samples above the alert level
  let alerted = false;
  let error = null;

  const sample = async () => {
    try {
      s = await system.stats();
      error = null;
      const at = settings.get('alertAt') ?? 0;
      if (at > 0 && s.cpu != null && s.cpu >= at) {
        high++;
        if (high >= 4 && !alerted) {
          alerted = true;
          island.alert({ leading: ui.icon('Cpu', { tone: 'accent' }), trailing: ui.text(`CPU ${Math.round(s.cpu)}%`, { size: 'sm', weight: 'semibold' }), seconds: 5 });
        }
      } else { high = 0; alerted = false; }
    } catch (e) {
      error = e.message;
    }
    render();
  };

  const render = () => {
    if (s?.cpu != null && settings.get('chip') !== false) {
      island.ambient('cpu').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.icon('Cpu', { tone: 'secondary', size: 14 }),
        ui.text(`${Math.round(s.cpu)}%`, { size: 'sm', weight: 'semibold', tabular: true }),
      ]));
    } else {
      island.ambient('cpu').remove();
    }
    if (!s) {
      island.home.page('stats').update(ui.text(error ?? '正在读取…', { tone: 'secondary', wrap: true }));
      return;
    }
    const memPct = s.memTotal ? (s.memUsed / s.memTotal) * 100 : null;
    island.home.page('stats').update(ui.stack({ gap: 'md', align: 'stretch' }, [
      ui.row({ gap: 'lg', align: 'center' }, [
        s.cpu != null ? ui.gauge({ value: s.cpu, max: 100, label: `${Math.round(s.cpu)}%`, detail: `CPU · ${s.cores} 核`, warnAt: 70, dangerAt: 90, size: 96 }) : null,
        memPct != null ? ui.gauge({ value: memPct, max: 100, label: `${Math.round(memPct)}%`, detail: `内存 ${(s.memUsed / GB).toFixed(1)}/${(s.memTotal / GB).toFixed(0)} GB`, warnAt: 80, dangerAt: 92, size: 96 }) : null,
        ui.stack({ gap: 'sm', grow: true }, [
          s.netIn != null ? ui.row({ gap: 'xs', align: 'center' }, [ui.icon('ArrowDown', { size: 14, tone: 'secondary' }), ui.text(rate(s.netIn), { tabular: true })]) : null,
          s.netOut != null ? ui.row({ gap: 'xs', align: 'center' }, [ui.icon('ArrowUp', { size: 14, tone: 'secondary' }), ui.text(rate(s.netOut), { tabular: true })]) : null,
          s.battery != null ? ui.row({ gap: 'xs', align: 'center' }, [ui.icon(s.charging ? 'BatteryCharging' : 'Battery', { size: 14, tone: 'secondary' }), ui.text(`${s.battery}%${s.charging ? ' 充电中' : ''}`, { tabular: true })]) : null,
        ]),
      ]),
      error ? ui.text(error, { size: 'xs', tone: 'secondary', wrap: true }) : null,
    ]));
  };

  // Faster while the user is looking: the page updating is cheap, so a steady few-second rhythm is enough.
  setInterval(sample, 3000);
  await sample();
}
