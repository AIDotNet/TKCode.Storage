// "calendar" provider for the macOS Calendar app (docs/design/05-builtin-plugins.md §6.2): every account added in
// System Settings › Internet Accounts (iCloud, Exchange, Google, subscribed calendars…) shows up through the host's
// calendar.system capability (EventKit, read only). No credentials and no network here; the first read makes macOS ask
// the user for Calendar access. One read serves listCalendars and listEvents for a minute.
import { island, systemCalendar } from '@island/api';

const FRESH = 60 * 1000;
let cache = null; // { key, at, data }

// Links to video meetings, in the order they are preferred when an event mentions several.
const MEETING = [
  /https?:\/\/[^\s"<>]*meeting\.tencent\.com[^\s"<>]*/i,
  /https?:\/\/[^\s"<>]*vc\.feishu\.cn[^\s"<>]*/i,
  /https?:\/\/[^\s"<>]*meeting\.dingtalk\.com[^\s"<>]*/i,
  /https?:\/\/meet\.google\.com[^\s"<>]*/i,
  /https?:\/\/[^\s"<>]*zoom\.(?:us|com\.cn)\/[^\s"<>]*/i,
  /https?:\/\/teams\.microsoft\.com[^\s"<>]*/i,
  /https?:\/\/[^\s"<>]*webex\.com[^\s"<>]*/i,
];
const meetingLink = (...texts) => {
  const joined = texts.filter(Boolean).join('\n');
  for (const re of MEETING) {
    const m = re.exec(joined);
    if (m) return m[0].replace(/[).,;]+$/, '');
  }
  return undefined;
};

const MESSAGES = {
  denied: '没有“日历”的访问权限：请在 系统设置 › 隐私与安全性 › 日历 中允许 O_O，然后重试',
  restricted: '“日历”的访问被系统限制（如屏幕使用时间或设备管理）',
  unavailable: '这台电脑上读不了系统日历（仅支持 macOS）',
  timeout: '等待“日历”权限超时，请重试并在弹窗中选择允许',
};

const read = async (range) => {
  if (!systemCalendar) throw new Error(MESSAGES.unavailable);
  const key = `${range.from}|${range.to}`;
  if (cache && cache.key === key && Date.now() - cache.at < FRESH) return cache.data;
  const data = await systemCalendar.read({ from: range.from, to: range.to });
  if (data?.error) throw new Error(MESSAGES[data.error] ?? `读取系统日历失败：${data.message ?? data.error}`);
  cache = { key, at: Date.now(), data };
  return data;
};

// A small range just to learn the calendars (needs access as well).
const calendars = async () => {
  const from = new Date();
  return (await read({ from: from.toISOString(), to: new Date(from.getTime() + 24 * 3600 * 1000).toISOString() })).calendars;
};

export function activate() {
  island.provide('calendar', 'system', {
    accounts() {
      return [{ handle: 'system', label: '系统日历' }];
    },
    async listCalendars() {
      return (await calendars()).map((c) => ({
        id: c.id,
        name: c.account && c.account !== c.name ? `${c.name}（${c.account}）` : c.name,
        ...(c.color ? { color: c.color } : {}),
      }));
    },
    async listEvents(_account, range) {
      const { events, calendars } = await read(range);
      const colors = new Map(calendars.map((c) => [c.id, c.color]));
      return events.map(({ notes, ...e }) => {
        if (colors.get(e.calendarId)) e.color = colors.get(e.calendarId);
        const meeting = meetingLink(e.url, e.location, notes);
        if (meeting) e.meetingUrl = meeting;
        if (e.url && e.url === meeting) delete e.url;
        return e;
      });
    },
  });
}
