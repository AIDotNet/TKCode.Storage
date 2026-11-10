// "calendar" provider for Google Calendar (docs/design/05-builtin-plugins.md §6.2). Signs in with the host's oauth
// capability (system browser, PKCE; the host keeps and refreshes the tokens and adds them only for
// www.googleapis.com), then reads the primary calendar's events with
//   GET /calendar/v3/calendars/primary/events?timeMin&timeMax&singleEvents=true&orderBy=startTime
// singleEvents expands recurring events into instances. The scope is the narrow calendar.events.readonly; the
// account's label is the primary calendar's name (its e-mail), which the events response carries in `summary`.
import { island, http, oauth, settings } from '@island/api';

const API = 'https://www.googleapis.com/calendar/v3';
const SCOPES = ['https://www.googleapis.com/auth/calendar.events.readonly'];

const json = async (url, account) => {
  const res = await http.fetch(url, { auth: account, headers: { Accept: 'application/json' } });
  let body = null;
  try { body = res.json(); } catch { /* not JSON */ }
  if (res.status === 401) throw new Error('Google 登录已失效，请重新登录');
  if (res.status === 403) {
    throw new Error(body?.error?.message ? `Google 拒绝访问：${body.error.message}` : 'Google 拒绝访问：请确认已启用 Google Calendar API');
  }
  if (!res.ok) throw new Error(`Google 日历返回 ${res.status}${body?.error?.message ? `：${body.error.message}` : ''}`);
  return body;
};

const meetingOf = (e) => e.hangoutLink
  ?? e.conferenceData?.entryPoints?.find((p) => p.entryPointType === 'video')?.uri;

const toEvent = (e) => {
  const allDay = !!e.start?.date;
  return {
    id: e.id,
    calendarId: 'primary',
    title: e.summary ?? '（无标题）',
    start: allDay ? e.start.date : e.start?.dateTime,
    end: allDay ? (e.end?.date ?? e.start.date) : (e.end?.dateTime ?? e.start?.dateTime),
    allDay,
    location: e.location || undefined,
    meetingUrl: meetingOf(e) || undefined,
    url: e.htmlLink,
    myStatus: e.attendees?.find((a) => a.self)?.responseStatus,
  };
};

export function activate() {
  let signingIn = false;

  const mine = async () => (await oauth.accounts()).filter((a) => a.provider === 'google');

  island.provide('calendar', 'google', {
    /** For the aggregator: offer "登录" while there is no account. `configured`: the client ID is filled in. */
    async status() {
      return {
        needsSignIn: (await mine()).length === 0,
        configured: !!String(settings.get('clientId') ?? '').trim(),
        signingIn,
      };
    },
    /** Opens the system browser to sign in and returns at once (the user may take minutes). */
    async signIn() {
      const clientId = String(settings.get('clientId') ?? '').trim();
      if (!clientId) throw new Error('先在控制台 › 插件 › Google 日历 中填写 OAuth 客户端 ID 和密钥');
      if (signingIn) return { started: true };
      signingIn = true;
      const clientSecret = (await settings.getSecret('clientSecret')) ?? undefined;
      oauth.signIn('google', { clientId, clientSecret, scopes: SCOPES, label: 'Google' })
        .then(async (account) => {
          try {
            const r = await json(`${API}/calendars/primary/events?maxResults=1`, account);
            if (r?.summary) await oauth.setLabel(account, r.summary);
          } catch { /* the label is a nicety */ }
        })
        .catch((e) => console.warn(`Google 登录未完成：${e.message}`))
        .finally(() => { signingIn = false; });
      return { started: true };
    },
    async signOut(account) {
      return oauth.signOut(account);
    },
    async accounts() {
      return (await mine()).map((a) => ({
        handle: a.handle,
        label: a.label && a.label !== 'Google' ? `Google · ${a.label}` : 'Google 日历',
      }));
    },
    async listCalendars(account) {
      const r = await json(`${API}/calendars/primary/events?maxResults=1`, account);
      return [{ id: 'primary', name: r?.summary ?? 'Google 日历', primary: true }];
    },
    /** range: { from, to } (ISO). Instances of the primary calendar's events overlapping the range. */
    async listEvents(account, range) {
      const out = [];
      let pageToken = null;
      for (let i = 0; i < 10; i++) {
        const q = `timeMin=${encodeURIComponent(range.from)}&timeMax=${encodeURIComponent(range.to)}` +
          `&singleEvents=true&orderBy=startTime&maxResults=250${pageToken ? `&pageToken=${encodeURIComponent(pageToken)}` : ''}`;
        const r = await json(`${API}/calendars/primary/events?${q}`, account);
        for (const e of r?.items ?? []) {
          if (e.status !== 'cancelled') out.push(toEvent(e));
        }
        pageToken = r?.nextPageToken;
        if (!pageToken) break;
      }
      return out;
    },
  });
}
