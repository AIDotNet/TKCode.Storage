// "calendar" provider for CalDAV servers (docs/design/05-builtin-plugins.md §6.2): WeCom (caldav.wecom.work, the
// only way to read a user's own WeCom schedule), iCloud, Nextcloud, Feishu and any other CalDAV server. Discovery
// follows RFC 6764 / 4791: current-user-principal → calendar-home-set → calendars, then a calendar-query REPORT
// with a time range per calendar. Network access is limited to the 服务器地址 the user typed (network:@serverUrl).
import { island, http, settings } from '@island/api';
import { eventsBetween } from './ical.js';

const DISCOVERY_TTL = 30 * 60 * 1000;

// ---- small helpers: base64 (no btoa here), URLs, XML -------------------------------------------------------------

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
const utf8 = (s) => {
  const out = [];
  for (const ch of s) {
    let c = ch.codePointAt(0);
    if (c < 0x80) out.push(c);
    else if (c < 0x800) out.push(0xc0 | (c >> 6), 0x80 | (c & 63));
    else if (c < 0x10000) out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
    else out.push(0xf0 | (c >> 18), 0x80 | ((c >> 12) & 63), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
  }
  return out;
};
export const base64 = (s) => {
  const b = utf8(s);
  let out = '';
  for (let i = 0; i < b.length; i += 3) {
    const n = (b[i] << 16) | ((b[i + 1] ?? 0) << 8) | (b[i + 2] ?? 0);
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + (i + 1 < b.length ? B64[(n >> 6) & 63] : '=') + (i + 2 < b.length ? B64[n & 63] : '=');
  }
  return out;
};

const origin = (url) => /^(https?:\/\/[^/]+)/i.exec(url)?.[1] ?? '';
const resolve = (base, href) => {
  if (/^https?:\/\//i.test(href)) return href;
  if (href.startsWith('/')) return origin(base) + href;
  return base.replace(/[^/]*$/, '') + href;
};

const decodeXml = (s) => s
  .replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, '$1')
  .replace(/&#(\d+);/g, (_, n) => String.fromCodePoint(+n))
  .replace(/&#x([0-9a-f]+);/gi, (_, n) => String.fromCodePoint(parseInt(n, 16)))
  .replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');

// Elements by local name, whatever the namespace prefix (d:, D:, cal:, none…).
const elements = (xml, name) => {
  const re = new RegExp(`<(?:[\\w.-]+:)?${name}\\b[^>]*?(?:/>|>([\\s\\S]*?)</(?:[\\w.-]+:)?${name}\\s*>)`, 'gi');
  return [...xml.matchAll(re)].map((m) => m[1] ?? '');
};
const element = (xml, name) => elements(xml, name)[0];
const hasElement = (xml, name) => new RegExp(`<(?:[\\w.-]+:)?${name}\\b`, 'i').test(xml);

// Only the properties a server reports with 200 (a 404 propstat lists what it does not have).
const okProps = (response) => elements(response, 'propstat').filter((ps) => /\s200\s/.test(element(ps, 'status') ?? ' 200 ')).join('\n');

// ---- the server -------------------------------------------------------------------------------------------------

const config = () => ({
  url: String(settings.get('serverUrl') ?? '').trim(),
  user: String(settings.get('username') ?? '').trim(),
  name: String(settings.get('name') ?? '').trim(),
});

const request = async (method, url, depth, body) => {
  const { user } = config();
  const password = await settings.getSecret('password');
  if (!password) throw new Error('还没有填写密码：请在控制台的 CalDAV 插件设置里填写');
  let res;
  try {
    res = await http.fetch(url, {
      method,
      headers: {
        Authorization: `Basic ${base64(`${user}:${password}`)}`,
        Depth: String(depth),
        'Content-Type': 'application/xml; charset=utf-8',
        Accept: 'application/xml, text/xml',
      },
      body,
      timeoutMs: 30000,
    });
  } catch (e) {
    if (String(e.message).includes('没有访问')) {
      throw new Error(`服务器把请求转到了别的地址（${/'([^']+)'/.exec(e.message)?.[1] ?? '另一个站点'}）：请把那个地址填到"服务器地址"`);
    }
    throw e;
  }
  if (res.status === 401 || res.status === 403) throw new Error('用户名或密码不对（企业微信、iCloud 需要专用的同步密码）');
  if (res.status !== 207 && !res.ok) throw new Error(`服务器返回 ${res.status}`);
  return res.text();
};

const propfind = (url, depth, props) => request('PROPFIND', url, depth,
  `<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/"><d:prop>${props}</d:prop></d:propfind>`);

let discovered = null; // { key, at, calendars: [{ id, url, name, color }] }

const discover = async () => {
  const { url, user } = config();
  if (!url) throw new Error('还没有填写服务器地址：请在控制台的 CalDAV 插件设置里填写');
  if (!/^https?:\/\//i.test(url)) throw new Error('服务器地址要以 https:// 开头');
  const key = `${url}|${user}`;
  if (discovered?.key === key && Date.now() - discovered.at < DISCOVERY_TTL) return discovered.calendars;

  // 1. Who am I? Servers answer at the root or at /.well-known/caldav; some take the URL as the principal itself.
  let principal = null;
  for (const candidate of [url, `${origin(url)}/.well-known/caldav`]) {
    try {
      const xml = await propfind(candidate, 0, '<d:current-user-principal/>');
      const href = element(element(xml, 'current-user-principal') ?? '', 'href');
      if (href) { principal = resolve(candidate, decodeXml(href).trim()); break; }
    } catch (e) {
      if (candidate === url && /密码|填写/.test(e.message)) throw e;
    }
  }
  // 2. Where are my calendars?
  let home = url;
  if (principal) {
    const xml = await propfind(principal, 0, '<c:calendar-home-set/>');
    const href = element(element(xml, 'calendar-home-set') ?? '', 'href');
    if (href) home = resolve(principal, decodeXml(href).trim());
  }
  // 3. Which of the collections there are calendars with events?
  const xml = await propfind(home, 1, '<d:resourcetype/><d:displayname/><a:calendar-color/><c:supported-calendar-component-set/>');
  const calendars = [];
  for (const response of elements(xml, 'response')) {
    const props = okProps(response);
    const type = element(props, 'resourcetype') ?? '';
    if (!hasElement(type, 'calendar')) continue;
    const comps = element(props, 'supported-calendar-component-set');
    if (comps && hasElement(comps, 'comp') && !/name="VEVENT"/i.test(comps)) continue; // tasks-only lists
    const href = decodeXml(element(response, 'href') ?? '').trim();
    if (!href) continue;
    const full = resolve(home, href);
    calendars.push({
      id: full,
      url: full,
      name: decodeXml(element(props, 'displayname') ?? '').trim() || decodeURIComponent(href.replace(/\/$/, '').split('/').pop() ?? '日历'),
      color: (element(props, 'calendar-color') ?? '').trim().slice(0, 7) || undefined,
    });
  }
  if (calendars.length === 0) throw new Error('这个账户下没有找到日历');
  discovered = { key, at: Date.now(), calendars };
  return calendars;
};

const pad = (n) => String(n).padStart(2, '0');
const utcStamp = (ms) => {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}${pad(d.getUTCMonth() + 1)}${pad(d.getUTCDate())}T${pad(d.getUTCHours())}${pad(d.getUTCMinutes())}${pad(d.getUTCSeconds())}Z`;
};

const eventsOf = async (calendar, fromMs, toMs) => {
  const xml = await request('REPORT', calendar.url, 1,
    `<?xml version="1.0" encoding="utf-8"?><c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">` +
    `<d:prop><d:getetag/><c:calendar-data/></d:prop>` +
    `<c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT">` +
    `<c:time-range start="${utcStamp(fromMs)}" end="${utcStamp(toMs)}"/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>`);
  const events = [];
  for (const data of elements(xml, 'calendar-data')) {
    events.push(...eventsBetween(decodeXml(data), fromMs, toMs, calendar.id));
  }
  return events;
};

export function activate() {
  island.provide('calendar', 'caldav', {
    /** One account: the configured server and user. */
    accounts() {
      const { url, user, name } = config();
      if (!url || !user) return [];
      return [{ handle: 'main', label: name || `${user} · ${url.replace(/^https?:\/\//i, '').split('/')[0]}` }];
    },
    async listCalendars() {
      return (await discover()).map(({ id, name, color }) => ({ id, name, color }));
    },
    /** range: { from, to } (ISO). Events of every calendar of the account, overlapping the range. */
    async listEvents(account, range) {
      const fromMs = new Date(range.from).getTime();
      const toMs = new Date(range.to).getTime();
      if (!(toMs > fromMs)) throw new Error('时间范围无效');
      const out = [];
      for (const calendar of await discover()) out.push(...await eventsOf(calendar, fromMs, toMs));
      return out;
    },
  });
  settings.onChange(() => { discovered = null; });
}
