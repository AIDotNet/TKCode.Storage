// "calendar" provider for ICS subscription links (docs/design/05-builtin-plugins.md §6.2, "通用 ICS 订阅链接"):
// up to three public calendar addresses. Each link is an account; the file is fetched at most every 15 minutes and
// expanded with the shared iCalendar reader. Network access is limited to the links the user typed
// (network:@url1/@url2/@url3).
import { island, http, settings } from '@island/api';
import { eventsBetween, calendarName } from './ical.js';

const SLOTS = ['1', '2', '3'];
const REFETCH = 15 * 60 * 1000;
const cache = new Map(); // url → { at, text }

const slot = (n) => ({
  url: String(settings.get(`url${n}`) ?? '').trim(),
  name: String(settings.get(`name${n}`) ?? '').trim(),
});

const host = (url) => url.replace(/^[a-z]+:\/\//i, '').split(/[/?#]/)[0];

const fetchText = async (url) => {
  if (/^webcal:\/\//i.test(url)) throw new Error('webcal:// 链接请改成 https:// 后填写');
  if (!/^https?:\/\//i.test(url)) throw new Error('订阅链接要以 https:// 开头');
  const hit = cache.get(url);
  if (hit && Date.now() - hit.at < REFETCH) return hit.text;
  const res = await http.fetch(url, { headers: { Accept: 'text/calendar, */*' }, timeoutMs: 30000 });
  if (res.status === 404) throw new Error('订阅链接已失效（404）');
  if (!res.ok) throw new Error(`订阅地址返回 ${res.status}`);
  const text = res.text();
  if (!/BEGIN:VCALENDAR/i.test(text)) throw new Error('这个链接返回的不是日历文件');
  cache.set(url, { at: Date.now(), text });
  return text;
};

export function activate() {
  island.provide('calendar', 'ics', {
    accounts() {
      return SLOTS.map((n) => ({ n, ...slot(n) })).filter((s) => s.url)
        .map((s) => ({ handle: `url${s.n}`, label: s.name || calendarName(cache.get(s.url)?.text ?? '') || host(s.url) }));
    },
    async listCalendars(account) {
      const { url, name } = slot(String(account?.handle ?? '').replace('url', ''));
      if (!url) throw new Error('这个订阅已被删除');
      return [{ id: url, name: name || calendarName(await fetchText(url)) || host(url) }];
    },
    async listEvents(account, range) {
      const { url } = slot(String(account?.handle ?? '').replace('url', ''));
      if (!url) throw new Error('这个订阅已被删除');
      return eventsBetween(await fetchText(url), new Date(range.from).getTime(), new Date(range.to).getTime(), url);
    },
  });
  settings.onChange(() => cache.clear());
}
