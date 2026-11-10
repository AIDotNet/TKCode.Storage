// "calendar" provider for DingTalk (docs/design/05-builtin-plugins.md §6.2). Sign-in goes through the host's oauth
// capability (system browser; the host exchanges the code at /v1.0/oauth2/userAccessToken, keeps and refreshes the
// token and sends it only to api.dingtalk.com, in the x-acs-dingtalk-access-token header). Then:
//   GET /v1.0/contact/users/me                                         → unionId (and a name for the label)
//   GET /v1.0/calendar/users/{unionId}/calendars/primary/eventsview?timeMin&timeMax&maxResults&nextToken
// The "events view" expands recurring events within the range (the plain event list does not).
import { island, http, oauth, settings } from '@island/api';

const API = 'https://api.dingtalk.com/v1.0';

const json = async (url, account) => {
  const res = await http.fetch(url, { auth: account, headers: { Accept: 'application/json' } });
  let body = null;
  try { body = res.json(); } catch { /* not JSON */ }
  if (res.status === 401) throw new Error('钉钉登录已失效，请重新登录');
  if (res.status === 403) throw new Error(`钉钉拒绝访问${body?.message ? `：${body.message}` : '：请确认应用已获得日程读取权限 Calendar.Event.Read'}`);
  if (!res.ok) throw new Error(`钉钉返回 ${res.status}${body?.message ? `：${body.message}` : ''}`);
  return body ?? {};
};

// DingTalk times: { dateTime: ISO 8601, timeZone } for timed events, { date: "YYYY-MM-DD" } for all-day ones.
const toEvent = (e) => {
  const allDay = !!e.isAllDay || (!!e.start?.date && !e.start?.dateTime);
  const start = allDay ? e.start?.date ?? e.start?.dateTime?.slice(0, 10) : e.start?.dateTime;
  const end = allDay ? e.end?.date ?? e.end?.dateTime?.slice(0, 10) ?? start : e.end?.dateTime ?? start;
  return {
    id: e.id,
    calendarId: 'primary',
    title: e.summary || '（无标题）',
    start: allDay ? start : new Date(start).toISOString(),
    end: allDay ? end : new Date(end).toISOString(),
    allDay,
    location: e.location?.displayName || undefined,
    meetingUrl: e.onlineMeetingInfo?.url || undefined,
  };
};

export function activate() {
  let signingIn = false;
  const unionIds = new Map(); // account handle → unionId

  const mine = async () => (await oauth.accounts()).filter((a) => a.provider === 'dingtalk');

  const me = async (account) => json(`${API}/contact/users/me`, account);

  const unionIdOf = async (account) => {
    const handle = account?.handle ?? account;
    if (!unionIds.has(handle)) {
      const profile = await me(account);
      if (!profile.unionId) throw new Error('无法获取钉钉用户信息：请确认应用已获得通讯录个人信息读取权限');
      unionIds.set(handle, profile.unionId);
    }
    return unionIds.get(handle);
  };

  island.provide('calendar', 'dingtalk', {
    /** For the aggregator: offer "登录" while there is no account. `configured`: the client ID is filled in. */
    async status() {
      return {
        needsSignIn: (await mine()).length === 0,
        configured: !!String(settings.get('clientId') ?? '').trim(),
        signingIn,
      };
    },
    /** Opens the system browser to sign in and returns at once. */
    async signIn() {
      const clientId = String(settings.get('clientId') ?? '').trim();
      if (!clientId) throw new Error('先在控制台 › 插件 › 钉钉日历 中填写 Client ID 和 Client Secret');
      if (signingIn) return { started: true };
      signingIn = true;
      const clientSecret = (await settings.getSecret('clientSecret')) ?? undefined;
      oauth.signIn('dingtalk', { clientId, clientSecret, scopes: ['openid'], label: '钉钉' })
        .then(async (account) => {
          try {
            const profile = await me(account);
            if (profile.unionId) unionIds.set(account.handle, profile.unionId);
            const label = profile.email || profile.nick;
            if (label) await oauth.setLabel(account, label);
          } catch { /* the label is a nicety */ }
        })
        .catch((e) => console.warn(`钉钉登录未完成：${e.message}`))
        .finally(() => { signingIn = false; });
      return { started: true };
    },
    async signOut(account) {
      unionIds.delete(account?.handle ?? account);
      return oauth.signOut(account);
    },
    async accounts() {
      return (await mine()).map((a) => ({
        handle: a.handle,
        label: a.label && a.label !== '钉钉' ? `钉钉 · ${a.label}` : '钉钉日历',
      }));
    },
    async listCalendars() {
      return [{ id: 'primary', name: '钉钉日历', primary: true }];
    },
    /** range: { from, to } (ISO). Instances of the primary calendar's events overlapping the range. */
    async listEvents(account, range) {
      const unionId = await unionIdOf(account);
      const out = [];
      let nextToken = null;
      for (let i = 0; i < 10; i++) {
        const q = `timeMin=${encodeURIComponent(range.from)}&timeMax=${encodeURIComponent(range.to)}&maxResults=100` +
          (nextToken ? `&nextToken=${encodeURIComponent(nextToken)}` : '');
        const r = await json(`${API}/calendar/users/${encodeURIComponent(unionId)}/calendars/primary/eventsview?${q}`, account);
        for (const e of r.events ?? []) {
          if (e.status === 'cancelled' || !(e.start?.dateTime || e.start?.date)) continue;
          out.push(toEvent(e));
        }
        nextToken = r.nextToken;
        if (!nextToken) break;
      }
      return out;
    },
  });
}
