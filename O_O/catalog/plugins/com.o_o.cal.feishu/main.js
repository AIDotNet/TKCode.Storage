// "calendar" provider for Feishu (Lark) calendars (docs/design/05-builtin-plugins.md §6.2). The user creates a
// 企业自建应用 and enters its App ID / App Secret; sign-in goes through the host's oauth capability (system browser,
// PKCE; the host keeps and refreshes user_access_token and adds it only for open.feishu.cn). Calendar v4:
//   GET /open-apis/calendar/v4/calendars                         the user's calendars (primary + subscribed)
//   GET /open-apis/calendar/v4/calendars/{id}/events?start_time&end_time   events (unix seconds)
// Recurring events come back as their series (RRULE in `recurrence`); they are expanded here with the same iCalendar
// rules the CalDAV provider uses (ical.js). Answers are { code, msg, data }: code 0 is success.
import { island, http, oauth, settings } from '@island/api';
import { eventsBetween } from './ical.js';

const API = 'https://open.feishu.cn/open-apis';
const SCOPES = ['calendar:calendar:readonly', 'offline_access'];
// Feishu's own codes for a user token that is invalid or expired.
const TOKEN_CODES = new Set([99991661, 99991663, 99991668, 99991677, 20005]);

const json = async (url, account) => {
  const res = await http.fetch(url, { auth: account, headers: { Accept: 'application/json' } });
  let body = null;
  try { body = res.json(); } catch { /* not JSON */ }
  if (res.status === 401 || TOKEN_CODES.has(body?.code)) throw new Error('飞书登录已失效，请重新登录');
  if (body && body.code !== 0) throw new Error(`飞书返回错误 ${body.code}${body.msg ? `：${body.msg}` : ''}`);
  if (!res.ok || !body) throw new Error(`飞书返回 ${res.status}`);
  return body.data ?? {};
};

const pad = (n) => String(n).padStart(2, '0');
const utcStamp = (ms) => {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}${pad(d.getUTCMonth() + 1)}${pad(d.getUTCDate())}T${pad(d.getUTCHours())}${pad(d.getUTCMinutes())}${pad(d.getUTCSeconds())}Z`;
};

// A Feishu time: { timestamp: "<unix seconds>", timezone } for timed events, { date: "YYYY-MM-DD" } for all-day.
const timeOf = (t) => (t?.date ? { date: t.date } : t?.timestamp ? { ms: Number(t.timestamp) * 1000 } : null);

// Feishu's calendar colour is a signed 32-bit ARGB integer; the marker wants #RRGGBB.
const hexOf = (c) => (Number.isInteger(c) ? `#${((c >>> 0) & 0xffffff).toString(16).padStart(6, '0')}` : undefined);

const meetingOf = (e) => e.vchat?.meeting_url || undefined;

const toEvent = (e, calendarId) => {
  const s = timeOf(e.start_time);
  const t = timeOf(e.end_time) ?? s;
  const allDay = !!s.date;
  return {
    id: e.event_id,
    calendarId,
    title: e.summary || '（无标题）',
    start: allDay ? s.date : new Date(s.ms).toISOString(),
    end: allDay ? (t.date ?? s.date) : new Date(t.ms ?? s.ms).toISOString(),
    allDay,
    location: e.location?.name || undefined,
    meetingUrl: meetingOf(e),
    url: e.app_link || undefined,
  };
};

// A recurring series, expanded within [fromMs, toMs) by writing it as a VEVENT and letting ical.js apply the RRULE.
const expand = (e, calendarId, fromMs, toMs) => {
  const s = timeOf(e.start_time);
  const t = timeOf(e.end_time) ?? s;
  const escape = (v) => String(v ?? '').replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/[,;]/g, (c) => `\\${c}`);
  const lines = [
    'BEGIN:VCALENDAR', 'BEGIN:VEVENT',
    `UID:${e.event_id}`,
    s.date ? `DTSTART;VALUE=DATE:${s.date.replace(/-/g, '')}` : `DTSTART:${utcStamp(s.ms)}`,
    t.date ? `DTEND;VALUE=DATE:${t.date.replace(/-/g, '')}` : `DTEND:${utcStamp(t.ms ?? s.ms)}`,
    `RRULE:${String(e.recurrence).replace(/^RRULE:/i, '')}`,
    `SUMMARY:${escape(e.summary || '（无标题）')}`,
  ];
  if (e.location?.name) lines.push(`LOCATION:${escape(e.location.name)}`);
  lines.push('END:VEVENT', 'END:VCALENDAR');
  return eventsBetween(lines.join('\r\n'), fromMs, toMs, calendarId).map((x) => ({
    ...x,
    title: e.summary || x.title,
    meetingUrl: meetingOf(e) ?? x.meetingUrl,
  }));
};

const overlaps = (ev, fromMs, toMs) => {
  const start = ev.allDay ? new Date(`${ev.start}T00:00:00`).getTime() : new Date(ev.start).getTime();
  const end = ev.allDay ? new Date(`${ev.end}T00:00:00`).getTime() : new Date(ev.end).getTime();
  return start < toMs && (end > fromMs || (end === start && start >= fromMs));
};

export function activate() {
  let signingIn = false;
  let calendarsCache = null; // { handle, at, list }

  const mine = async () => (await oauth.accounts()).filter((a) => a.provider === 'feishu');

  const calendars = async (account) => {
    const handle = account?.handle ?? account;
    if (calendarsCache?.handle === handle && Date.now() - calendarsCache.at < 30 * 60 * 1000) return calendarsCache.list;
    const list = [];
    let pageToken = '';
    for (let i = 0; i < 5; i++) {
      const data = await json(`${API}/calendar/v4/calendars?page_size=50${pageToken ? `&page_token=${encodeURIComponent(pageToken)}` : ''}`, account);
      for (const c of data.calendar_list ?? []) {
        // The user's own and subscribed calendars; a free/busy-only view has no event details.
        if (!['primary', 'shared'].includes(c.type) || c.role === 'free_busy_reader' || c.is_deleted) continue;
        list.push({ id: c.calendar_id, color: hexOf(c.color), name: c.summary || (c.type === 'primary' ? '我的日历' : '日历'), primary: c.type === 'primary' });
      }
      if (!data.has_more || !data.page_token) break;
      pageToken = data.page_token;
    }
    if (list.length === 0) throw new Error('这个飞书账户下没有可读的日历');
    calendarsCache = { handle, at: Date.now(), list };
    return list;
  };

  island.provide('calendar', 'feishu', {
    /** For the aggregator: offer "登录" while there is no account. `configured`: the App ID is filled in. */
    async status() {
      return {
        needsSignIn: (await mine()).length === 0,
        configured: !!String(settings.get('appId') ?? '').trim(),
        signingIn,
      };
    },
    /** Opens the system browser to sign in and returns at once. */
    async signIn() {
      const clientId = String(settings.get('appId') ?? '').trim();
      if (!clientId) throw new Error('先在控制台 › 插件 › 飞书日历 中填写 App ID 和 App Secret');
      if (signingIn) return { started: true };
      signingIn = true;
      const clientSecret = (await settings.getSecret('appSecret')) ?? undefined;
      oauth.signIn('feishu', { clientId, clientSecret, scopes: SCOPES, label: '飞书' })
        .then(async (account) => {
          try {
            const me = await json(`${API}/authen/v1/user_info`, account);
            const label = me.email || me.enterprise_email || me.name;
            if (label) await oauth.setLabel(account, label);
          } catch { /* the label is a nicety */ }
        })
        .catch((e) => console.warn(`飞书登录未完成：${e.message}`))
        .finally(() => { signingIn = false; });
      return { started: true };
    },
    async signOut(account) {
      calendarsCache = null;
      return oauth.signOut(account);
    },
    async accounts() {
      return (await mine()).map((a) => ({
        handle: a.handle,
        label: a.label && a.label !== '飞书' ? `飞书 · ${a.label}` : '飞书日历',
      }));
    },
    async listCalendars(account) {
      return calendars(account);
    },
    /** range: { from, to } (ISO). Events of the user's calendars overlapping the range, recurring ones expanded. */
    async listEvents(account, range) {
      const fromMs = new Date(range.from).getTime();
      const toMs = new Date(range.to).getTime();
      if (!(toMs > fromMs)) throw new Error('时间范围无效');
      // The calendars are read side by side: the 月历 tab asks for about seven weeks at once.
      const one = async (cal) => {
        const out = [];
        const tint = (list) => list.map((x) => ({ ...x, color: cal.color }));
        let pageToken = '';
        for (let i = 0; i < 10; i++) {
          const q = `start_time=${Math.floor(fromMs / 1000)}&end_time=${Math.ceil(toMs / 1000)}&page_size=500` +
            (pageToken ? `&page_token=${encodeURIComponent(pageToken)}` : '');
          const data = await json(`${API}/calendar/v4/calendars/${encodeURIComponent(cal.id)}/events?${q}`, account);
          for (const e of data.items ?? []) {
            if (e.status === 'cancelled' || !timeOf(e.start_time)) continue;
            if (e.recurrence) out.push(...expand(e, cal.id, fromMs, toMs));
            else {
              const ev = toEvent(e, cal.id);
              if (overlaps(ev, fromMs, toMs)) out.push(ev);
            }
          }
          if (!data.has_more || !data.page_token) break;
          pageToken = data.page_token;
        }
        return tint(out);
      };
      const out = (await Promise.all((await calendars(account)).map(one))).flat();
      return out;
    },
  });
}
