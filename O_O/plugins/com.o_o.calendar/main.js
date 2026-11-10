// First-party calendar aggregator (docs/design/05-builtin-plugins.md §6): asks every "calendar" provider plugin
// (CalDAV, ICS subscriptions, later Feishu / DingTalk / Google) for today's and tomorrow's events, merges them (the
// same meeting in several calendars shows once), and keeps the island in step: a chip with the next event, one
// alert and an activity shortly before an event starts, and the 日程 page. It holds no credentials and no network
// permission: providers fetch in their own plugins and return data only.
//
// Provider protocol (interface "calendar"):
//   accounts() → [{ handle, label }]
//   listCalendars(account) → [{ id, name, color?, primary? }]
//   listEvents(account, { from, to }) → [{ id, calendarId, title, start, end, allDay, location?, meetingUrl?, url? }]
//     timed events: ISO 8601 instants; all-day events: YYYY-MM-DD (end exclusive)
//   Providers that sign in (Google, Feishu, DingTalk) also offer
//     status() → { needsSignIn, configured, signingIn }   signIn() → { started }   (opens the system browser)
//   and the 日程 page shows a "登录 …" row for each that has no account yet.
import { island, ui, settings, storage, clipboard, shell } from '@island/api';

const IFACE = 'calendar';
const MINUTE = 60 * 1000;
const DAY = 24 * 60 * MINUTE;
const REFRESH = 5 * MINUTE;
const ACTIVE_AFTER_START = 5 * MINUTE; // how long after the start opening the island still goes to the page
const PER_PAGE = 5;
const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const pad = (n) => String(n).padStart(2, '0');
const hm = (ms) => { const d = new Date(ms); return `${pad(d.getHours())}:${pad(d.getMinutes())}`; };

const startOfDay = (ms) => { const d = new Date(ms); return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime(); };
const dayOf = (s) => { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s); return m ? new Date(+m[1], +m[2] - 1, +m[3]).getTime() : null; };
const instant = (s) => dayOf(s) ?? new Date(s).getTime();

const cut = (s, n) => ([...s].length > n ? `${[...s].slice(0, n).join('')}…` : s);

export async function activate() {
  const cached = storage.get('cache') ?? { events: [], errors: [], at: null };
  let events = cached.events;           // merged: { key, title, startMs, endMs, allDay, location?, meetingUrl?, sources: [] }
  let errors = cached.errors;           // [{ source, message }]
  let fetchedAt = cached.at;
  let providers = [];
  let tab = 'today';
  let page = 0;
  let notice = null;
  let alerted = new Set(storage.get('alerted') ?? []); // "key|startMs" of events already announced
  let act = null;
  let timer = null;
  let refreshTimer = null;
  let signIns = [];                      // providers waiting for the user to sign in: { id, title, configured, signingIn }
  let watchUntil = 0;                    // after "登录" was clicked: look again every few seconds until then
  let watchTimer = null;

  // ---- fetching ---------------------------------------------------------------------------------------------------

  const merge = (lists) => {
    const byKey = new Map();
    for (const { source, list } of lists) {
      for (const e of list) {
        const startMs = instant(e.start);
        const endMs = Math.max(startMs, instant(e.end ?? e.start));
        if (!Number.isFinite(startMs)) continue;
        const key = `${String(e.title ?? '').trim().toLowerCase()}|${startMs}|${e.meetingUrl ?? ''}`;
        const known = byKey.get(key);
        if (known) {
          if (!known.sources.includes(source)) known.sources.push(source);
          known.location ??= e.location;
          continue;
        }
        byKey.set(key, {
          key, title: String(e.title ?? '（无标题）'), startMs, endMs, allDay: !!e.allDay,
          location: e.location, meetingUrl: e.meetingUrl, url: e.url, sources: [source],
        });
      }
    }
    return [...byKey.values()].sort((a, b) => a.startMs - b.startMs || a.title.localeCompare(b.title));
  };

  let refreshing = false;
  let again = false;
  const refresh = async () => {
    if (refreshing) { again = true; return; }
    refreshing = true;
    try {
      providers = await island.providers.list(IFACE);
      const from = startOfDay(Date.now());
      const range = { from: new Date(from).toISOString(), to: new Date(from + 2 * DAY).toISOString() };
      const lists = [];
      const failures = [];
      const waiting = [];
      for (const p of providers) {
        if (!p.running) continue;
        try {
          const accounts = await island.providers.call(IFACE, p.id, 'accounts');
          if (accounts.length === 0) {
            // A provider that signs in tells whether it needs to; others (CalDAV, ICS) just have no account yet.
            const s = await island.providers.call(IFACE, p.id, 'status').catch(() => null);
            if (s?.needsSignIn) waiting.push({ id: p.id, title: p.title, configured: !!s.configured, signingIn: !!s.signingIn });
          }
          for (const a of accounts) {
            const source = a.label || p.title;
            try {
              lists.push({ source, list: await island.providers.call(IFACE, p.id, 'listEvents', a, range) });
            } catch (e) {
              failures.push({ source, message: e.message });
            }
          }
        } catch (e) {
          failures.push({ source: p.title, message: e.message });
        }
      }
      events = merge(lists);
      errors = failures;
      signIns = waiting;
      if (waiting.length === 0) watchUntil = 0;
      fetchedAt = new Date().toISOString();
      storage.set('cache', { events, errors, at: fetchedAt });
    } finally {
      refreshing = false;
      if (again) { again = false; refresh(); }
    }
    evaluate();
  };

  // ---- the island: chip, alert, activity, page ---------------------------------------------------------------------

  const timed = () => events.filter((e) => !e.allDay);

  // The event that is about to start (or has just started), if any: [start - remind, start + a few minutes).
  const soon = (now, remind) => timed().find((e) => now >= e.startMs - remind && now < Math.min(e.endMs, e.startMs + ACTIVE_AFTER_START) + (e.endMs === e.startMs ? ACTIVE_AFTER_START : 0));

  const evaluate = () => {
    clearTimeout(timer);
    const now = Date.now();
    const remind = settings.get('remindBefore') * MINUTE;
    const lookahead = settings.get('lookaheadHours') * 60 * MINUTE;

    // Starting soon: one alert, and an activity so opening the island shows the 日程 page.
    const s = soon(now, remind);
    if (s) {
      const id = `${s.key}|${s.startMs}`;
      if (!alerted.has(id)) {
        alerted.add(id);
        alerted = new Set([...alerted].filter((k) => +k.split('|').pop() > now - DAY));
        storage.set('alerted', [...alerted]);
        const minutes = Math.round((s.startMs - now) / MINUTE);
        island.alert({
          leading: ui.icon(s.meetingUrl ? 'Video' : 'CalendarClock'),
          trailing: ui.text(`${minutes > 0 ? `${minutes} 分钟后` : '现在'} · ${cut(s.title, 10)}`, { size: 'sm', weight: 'semibold' }),
        }, { seconds: 6 });
      }
      act ??= island.activity.create('soon', { title: '日程即将开始', page: 'today' });
      act.setPriority(now >= s.startMs ? 'active' : 'passive');
      act.update({});
      tab = startOfDay(s.startMs) === startOfDay(now) ? 'today' : 'tomorrow';
    } else if (act) {
      act.end();
      act = null;
    }

    // Collapsed: the next timed event within the lookahead, or the one under way.
    const next = timed().find((e) => e.endMs > now && e.startMs - now <= lookahead);
    if (next) {
      island.chip('next').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.icon(next.meetingUrl ? 'Video' : 'Calendar', { tone: 'secondary', size: 14 }),
        ui.text(next.startMs <= now ? cut(next.title, 4) : hm(next.startMs), { size: 'sm', weight: 'semibold', tabular: true }),
      ]));
    } else {
      island.chip('next').remove();
    }

    render(now);

    // Wake up exactly at the next moment something changes (an alert, a start, an end, a chip appearing,
    // midnight), but at least once a minute so labels such as "15 分钟后" stay right.
    const boundaries = [startOfDay(now) + DAY];
    for (const e of timed()) {
      boundaries.push(e.startMs - remind, e.startMs, e.startMs + ACTIVE_AFTER_START, e.endMs, e.startMs - lookahead);
    }
    const wait = Math.min(MINUTE, ...boundaries.filter((b) => b > now).map((b) => b - now));
    timer = setTimeout(evaluate, Math.max(1000, wait + 50));
  };

  const open = async (e) => {
    notice = null;
    if (e.meetingUrl) {
      // Join: open the link (the meeting app or the browser). If it cannot be opened, the link goes to the clipboard.
      try {
        await shell.openUrl(e.meetingUrl);
        render(Date.now());
        return;
      } catch {
        // fall through to copying
      }
      try {
        await clipboard.write({ text: e.meetingUrl });
        island.alert({ leading: ui.icon('Link'), trailing: ui.text('已复制会议链接', { size: 'sm', weight: 'semibold' }) }, { seconds: 2 });
        notice = e.meetingUrl;
      } catch (err) {
        notice = err.message;
      }
    } else if (e.location) {
      notice = `地点：${e.location}`;
    }
    render(Date.now());
  };

  // "登录 …": the provider opens the system browser; until the account appears (or 5 minutes pass) look every few
  // seconds, so the events show up soon after the user finishes in the browser.
  const signIn = async (p) => {
    if (!p.configured) {
      notice = `先在控制台 › 插件 › ${p.title} 中填写应用的 ID 和密钥，再回来登录。`;
      render(Date.now());
      return;
    }
    try {
      await island.providers.call(IFACE, p.id, 'signIn');
      notice = `已在浏览器中打开 ${p.title} 的登录页，完成后这里会自动更新。`;
      watchUntil = Date.now() + 5 * MINUTE;
      clearInterval(watchTimer);
      watchTimer = setInterval(() => {
        if (Date.now() > watchUntil) { clearInterval(watchTimer); watchTimer = null; return; }
        refresh();
      }, 3000);
    } catch (err) {
      notice = err.message;
    }
    render(Date.now());
  };

  const signInRow = (p) => ui.item({ key: `signin:${p.id}`, gap: 'sm', align: 'center', onClick: () => signIn(p) }, [
    ui.icon('LogIn', { tone: 'secondary', size: 16 }),
    ui.text(p.signingIn ? `正在登录 ${p.title}…` : `登录 ${p.title}`, { grow: true, weight: 'medium' }),
    ui.text(p.configured ? '在浏览器中登录' : '需先填写应用 ID', { tone: 'secondary', size: 'xs' }),
  ]);

  const when = (e, now) => {
    if (e.allDay) return '全天';
    const range = `${hm(e.startMs)}–${hm(e.endMs)}`;
    if (now >= e.startMs && now < e.endMs) return `进行中 · 至 ${hm(e.endMs)}`;
    return range;
  };

  const row = (e, now) => {
    const past = e.endMs <= now && !e.allDay;
    return ui.item({ key: e.key, gap: 'sm', align: 'center', onClick: () => open(e) }, [
      ui.text(when(e, now), { size: 'sm', tabular: true, tone: 'secondary', width: 120 }),
      ui.text(e.title, { grow: true, tone: past ? 'secondary' : undefined, weight: past ? undefined : 'medium' }),
      e.meetingUrl ? ui.icon('Video', { tone: 'secondary', size: 16 })
        : e.location ? ui.icon('MapPin', { tone: 'secondary', size: 16 }) : null,
    ]);
  };

  const status = (now) => {
    const upcoming = timed().find((e) => e.startMs > now);
    if (!upcoming) return '';
    const minutes = Math.round((upcoming.startMs - now) / MINUTE);
    return minutes < 60 ? `${minutes} 分钟后开始` : `下一个 ${hm(upcoming.startMs)}`;
  };

  function render(now) {
    const today = startOfDay(now);
    const dayStart = tab === 'today' ? today : today + DAY;
    const shown = events.filter((e) => e.startMs < dayStart + DAY && (e.endMs > dayStart || (e.endMs === e.startMs && e.startMs >= dayStart)));
    const pages = Math.max(1, Math.ceil(shown.length / PER_PAGE));
    page = Math.min(page, pages - 1);
    const date = new Date(dayStart);
    const anyProvider = providers.length > 0 || events.length > 0;

    let body;
    if (!anyProvider) {
      body = ui.stack({ gap: 'xs' }, [
        ui.text('还没有日历来源', { weight: 'semibold' }),
        ui.text('在控制台的"插件"页添加 Google、飞书、钉钉日历，或"CalDAV 日历"（企业微信、iCloud 等）、"日历订阅（ICS）"。', { tone: 'secondary', size: 'sm', wrap: true }),
      ]);
    } else if (shown.length === 0) {
      body = ui.text(fetchedAt ? `${tab === 'today' ? '今天' : '明天'}没有日程。` : '正在读取…', { tone: 'secondary' });
    } else {
      body = ui.stack({ gap: 'xs', align: 'stretch' }, shown.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map((e) => row(e, now)));
    }

    island.page('today').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.row({ gap: 'xs', align: 'center' }, [
        ui.tabs({ value: tab, onChange: (v) => { tab = v; page = 0; render(Date.now()); } },
          [ui.option('today', '今天'), ui.option('tomorrow', '明天')]),
        ui.text(`${date.getMonth() + 1}月${date.getDate()}日 ${WEEK[date.getDay()]}`, { tone: 'secondary', size: 'sm' }),
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(Date.now()); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(Date.now()); } }) : null,
      ]),
      body,
      ...signIns.map(signInRow),
      pages > 1 ? ui.dots({ count: pages, index: page }) : null,
      notice ? ui.text(notice, { tone: 'secondary', size: 'xs', wrap: true }) : null,
      ...errors.map((e) => ui.text(`${e.source}：${e.message}`, { tone: 'secondary', size: 'xs', wrap: true })),
    ]), { status: ui.text(status(now), { tone: 'secondary', size: 'sm' }) });
  }

  // ---- lifecycle -------------------------------------------------------------------------------------------------

  const schedule = () => {
    clearInterval(refreshTimer);
    refreshTimer = setInterval(refresh, REFRESH);
  };
  settings.onChange(() => evaluate());
  island.providers.onChange(() => refresh());
  evaluate();          // the cached events at once
  schedule();
  await refresh();
}
