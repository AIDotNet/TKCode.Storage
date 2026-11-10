// A small iCalendar (RFC 5545) reader for calendar providers: VEVENTs with their times (UTC, floating, TZID via the
// file's VTIMEZONE or a table of common zones), recurrence (RRULE DAILY / WEEKLY / MONTHLY / YEARLY with INTERVAL,
// COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH), EXDATE and RECURRENCE-ID overrides, expanded into the calendar
// protocol's events (docs/design/05-builtin-plugins.md §6.1). The same file ships in each provider plugin.

const DAY_MS = 24 * 60 * 60 * 1000;
const WEEKDAYS = ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'];
const MAX_STEPS = 5000;

// Zones without daylight saving time that calendars often name without sending a VTIMEZONE (minutes east of UTC).
const FIXED_ZONES = {
  'Asia/Shanghai': 480, 'Asia/Chongqing': 480, 'Asia/Harbin': 480, 'Asia/Hong_Kong': 480, 'Asia/Macau': 480,
  'Asia/Taipei': 480, 'Asia/Singapore': 480, 'Asia/Kuala_Lumpur': 480, 'Asia/Manila': 480, 'PRC': 480,
  'China Standard Time': 480, 'Asia/Tokyo': 540, 'Tokyo Standard Time': 540, 'Asia/Seoul': 540, 'Asia/Kolkata': 330,
  'India Standard Time': 330, 'Asia/Dubai': 240, 'UTC': 0, 'Etc/UTC': 0, 'GMT': 0, 'Etc/GMT': 0, 'Z': 0,
};

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

// ---- lines and properties ---------------------------------------------------------------------------------------

const unescapeText = (v) => v.replace(/\\([nN,;\\])/g, (_, c) => (c === 'n' || c === 'N' ? '\n' : c));

/** "NAME;P=1;Q="a:b":VALUE" → { name, params, value } */
const parseLine = (line) => {
  let i = 0;
  let inQuotes = false;
  for (; i < line.length; i++) {
    const c = line[i];
    if (c === '"') inQuotes = !inQuotes;
    else if (c === ':' && !inQuotes) break;
  }
  const head = line.slice(0, i);
  const value = line.slice(i + 1);
  const parts = head.match(/(?:[^;"]|"[^"]*")+/g) ?? [head];
  const params = {};
  for (const p of parts.slice(1)) {
    const eq = p.indexOf('=');
    if (eq > 0) params[p.slice(0, eq).toUpperCase()] = p.slice(eq + 1).replace(/^"|"$/g, '');
  }
  return { name: parts[0].toUpperCase(), params, value };
};

/** The file's components as a tree: { type, props: { NAME: [{ params, value }] }, children: [] }. */
export const parseICS = (text) => {
  const lines = String(text).replace(/\r\n|\r/g, '\n').replace(/\n[ \t]/g, '').split('\n');
  const root = { type: 'ROOT', props: {}, children: [] };
  const stack = [root];
  for (const raw of lines) {
    if (!raw.trim()) continue;
    const { name, params, value } = parseLine(raw);
    const top = stack[stack.length - 1];
    if (name === 'BEGIN') {
      const c = { type: value.trim().toUpperCase(), props: {}, children: [] };
      top.children.push(c);
      stack.push(c);
    } else if (name === 'END') {
      if (stack.length > 1) stack.pop();
    } else {
      (top.props[name] ??= []).push({ params, value });
    }
  }
  return root;
};

const all = (node, type) => {
  const out = [];
  const walk = (n) => { for (const c of n.children) { if (c.type === type) out.push(c); walk(c); } };
  walk(node);
  return out;
};

const first = (comp, name) => comp.props[name]?.[0];
const text = (comp, name) => (first(comp, name) ? unescapeText(first(comp, name).value).trim() : undefined);

// ---- times ------------------------------------------------------------------------------------------------------
// A "wall" time is the calendar fields as written in the file; it becomes an instant with the event's zone.

const parseWall = (value) => {
  const m = /^(\d{4})(\d{2})(\d{2})(?:T(\d{2})(\d{2})(\d{2})?(Z)?)?$/.exec(value.trim());
  if (!m) return null;
  return {
    y: +m[1], mo: +m[2] - 1, d: +m[3],
    h: m[4] ? +m[4] : 0, mi: m[5] ? +m[5] : 0, s: m[6] ? +m[6] : 0,
    date: !m[4], utc: !!m[7],
  };
};

const wallMs = (w) => Date.UTC(w.y, w.mo, w.d, w.h, w.mi, w.s); // the fields as if they were UTC: for calendar maths
const fromWallMs = (ms, date) => {
  const t = new Date(ms);
  return { y: t.getUTCFullYear(), mo: t.getUTCMonth(), d: t.getUTCDate(), h: t.getUTCHours(), mi: t.getUTCMinutes(), s: t.getUTCSeconds(), date };
};

// The n-th (1-based; negative from the end) weekday `wd` of a month, as a day of the month (or null).
const nthWeekday = (y, mo, wd, n) => {
  const days = new Date(Date.UTC(y, mo + 1, 0)).getUTCDate();
  if (n > 0) {
    const firstWd = new Date(Date.UTC(y, mo, 1)).getUTCDay();
    const d = 1 + ((wd - firstWd + 7) % 7) + (n - 1) * 7;
    return d <= days ? d : null;
  }
  const lastWd = new Date(Date.UTC(y, mo, days)).getUTCDay();
  const d = days - ((lastWd - wd + 7) % 7) + (n + 1) * 7;
  return d >= 1 ? d : null;
};

const parseByDay = (v) => (v ? v.split(',').map((x) => {
  const m = /^([+-]?\d{1,2})?(SU|MO|TU|WE|TH|FR|SA)$/.exec(x.trim().toUpperCase());
  return m ? { n: m[1] ? +m[1] : 0, wd: WEEKDAYS.indexOf(m[2]) } : null;
}).filter(Boolean) : []);

/** Builds a resolver: (wall, tzid) → epoch ms, from the file's VTIMEZONEs, the fixed table, or local time. */
const zoneResolver = (root) => {
  const zones = {};
  for (const tz of all(root, 'VTIMEZONE')) {
    const id = text(tz, 'TZID');
    if (!id) continue;
    zones[id] = tz.children.filter((c) => c.type === 'STANDARD' || c.type === 'DAYLIGHT').map((c) => {
      const off = /^([+-])(\d{2})(\d{2})/.exec(first(c, 'TZOFFSETTO')?.value ?? '+0000');
      const rule = Object.fromEntries((first(c, 'RRULE')?.value ?? '').split(';').filter(Boolean).map((p) => p.split('=')));
      return {
        start: parseWall(first(c, 'DTSTART')?.value ?? '19700101T000000'),
        offset: (off[1] === '-' ? -1 : 1) * (+off[2] * 60 + +off[3]),
        month: rule.BYMONTH ? +rule.BYMONTH - 1 : null,
        byDay: parseByDay(rule.BYDAY)[0] ?? null,
      };
    });
  }
  // When an observance last began on or before `ms` (wall ms), or -Infinity.
  const onset = (o, ms) => {
    if (o.month === null || !o.byDay) return wallMs(o.start) <= ms ? wallMs(o.start) : -Infinity;
    const y = new Date(ms).getUTCFullYear();
    for (const year of [y, y - 1]) {
      const d = nthWeekday(year, o.month, o.byDay.wd, o.byDay.n || 1);
      if (d === null) continue;
      const t = Date.UTC(year, o.month, d, o.start.h, o.start.mi, o.start.s);
      if (t <= ms && t >= wallMs(o.start)) return t;
    }
    return -Infinity;
  };
  return (w, tzid) => {
    if (w.utc) return wallMs(w);
    if (tzid && zones[tzid]?.length) {
      const ms = wallMs(w);
      let best = null;
      let bestAt = -Infinity;
      for (const o of zones[tzid]) {
        const at = onset(o, ms);
        if (at > bestAt) { bestAt = at; best = o; }
      }
      const offset = best ? best.offset : zones[tzid][0].offset;
      return ms - offset * 60000;
    }
    const fixed = tzid !== undefined ? FIXED_ZONES[tzid] ?? FIXED_ZONES[tzid.replace(/^\/[^/]*\//, '')] : undefined;
    if (fixed !== undefined) return wallMs(w) - fixed * 60000;
    return new Date(w.y, w.mo, w.d, w.h, w.mi, w.s).getTime(); // floating, or a zone we do not know: local time
  };
};

const parseDuration = (v) => {
  const m = /^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$/.exec((v ?? '').trim());
  if (!m) return null;
  const ms = ((+(m[2] ?? 0) * 7 + +(m[3] ?? 0)) * 24 * 3600 + +(m[4] ?? 0) * 3600 + +(m[5] ?? 0) * 60 + +(m[6] ?? 0)) * 1000;
  return m[1] === '-' ? -ms : ms;
};

// ---- recurrence -------------------------------------------------------------------------------------------------

/** Start walls of the occurrences of `rule` from `start`, in order, until `untilMs` (wall ms) or the rule ends. */
function* occurrences(start, rrule, endWallMs, toInstant, tzid) {
  const r = Object.fromEntries(rrule.split(';').filter(Boolean).map((p) => {
    const eq = p.indexOf('=');
    return [p.slice(0, eq).toUpperCase(), p.slice(eq + 1)];
  }));
  const freq = (r.FREQ ?? '').toUpperCase();
  const interval = Math.max(1, +(r.INTERVAL ?? 1) || 1);
  const count = r.COUNT ? +r.COUNT : Infinity;
  const until = r.UNTIL ? parseWall(r.UNTIL) : null;
  const untilMs = until ? (until.date ? wallMs(until) + DAY_MS - 1 : toInstant(until, until.utc ? undefined : tzid)) : Infinity;
  const byDay = parseByDay(r.BYDAY);
  const byMonthDay = r.BYMONTHDAY ? r.BYMONTHDAY.split(',').map(Number) : [];
  const byMonth = r.BYMONTH ? r.BYMONTH.split(',').map((x) => +x - 1) : [];
  const t0 = wallMs(start);
  const timeOfDay = t0 - Date.UTC(start.y, start.mo, start.d);

  let emitted = 0;
  const emit = function* (ms) {
    if (ms < t0) return false;
    // Occurrences keep the series' UTC-ness: a DTSTART written in UTC (…Z) repeats in UTC, not in local time.
    const w = { ...fromWallMs(ms, start.date), utc: start.utc };
    if (toInstant(w, start.date ? undefined : tzid) > untilMs) return true;
    emitted++;
    yield w;
    return emitted >= count;
  };

  for (let step = 0; step < MAX_STEPS; step++) {
    let candidates = [];
    if (freq === 'DAILY') {
      candidates = [t0 + step * interval * DAY_MS];
    } else if (freq === 'WEEKLY') {
      const weekStart = t0 - ((new Date(t0).getUTCDay() + 6) % 7) * DAY_MS; // Monday of DTSTART's week
      const base = weekStart + step * interval * 7 * DAY_MS;
      const days = byDay.length ? byDay.map((b) => (b.wd + 6) % 7) : [(new Date(t0).getUTCDay() + 6) % 7];
      candidates = [...new Set(days)].sort((a, b) => a - b).map((i) => base + i * DAY_MS);
    } else if (freq === 'MONTHLY') {
      const m = start.mo + step * interval;
      const y = start.y + Math.floor(m / 12);
      const mo = ((m % 12) + 12) % 12;
      const daysIn = new Date(Date.UTC(y, mo + 1, 0)).getUTCDate();
      let days = [];
      if (byDay.length) {
        for (const b of byDay) {
          if (b.n) { const d = nthWeekday(y, mo, b.wd, b.n); if (d) days.push(d); }
          else for (let d = 1; d <= daysIn; d++) if (new Date(Date.UTC(y, mo, d)).getUTCDay() === b.wd) days.push(d);
        }
      } else if (byMonthDay.length) {
        days = byMonthDay.map((d) => (d < 0 ? daysIn + d + 1 : d)).filter((d) => d >= 1 && d <= daysIn);
      } else if (start.d <= daysIn) {
        days = [start.d];
      }
      candidates = [...new Set(days)].sort((a, b) => a - b).map((d) => Date.UTC(y, mo, d) + timeOfDay);
    } else if (freq === 'YEARLY') {
      const y = start.y + step * interval;
      const months = byMonth.length ? byMonth : [start.mo];
      for (const mo of months) {
        const daysIn = new Date(Date.UTC(y, mo + 1, 0)).getUTCDate();
        if (byDay.length) {
          for (const b of byDay) { const d = nthWeekday(y, mo, b.wd, b.n || 1); if (d) candidates.push(Date.UTC(y, mo, d) + timeOfDay); }
        } else if (start.d <= daysIn) {
          candidates.push(Date.UTC(y, mo, start.d) + timeOfDay);
        }
      }
      candidates.sort((a, b) => a - b);
    } else {
      yield start; // unknown frequency: just the first occurrence
      return;
    }
    if (byMonth.length && freq !== 'YEARLY') {
      candidates = candidates.filter((ms) => byMonth.includes(new Date(ms).getUTCMonth()));
    }
    for (const ms of candidates) {
      if (ms > endWallMs) return;
      const done = yield* emit(ms);
      if (done) return;
    }
  }
}

// ---- events -----------------------------------------------------------------------------------------------------

const iso = (ms) => new Date(ms).toISOString();
const isoDate = (w) => `${w.y}-${String(w.mo + 1).padStart(2, '0')}-${String(w.d).padStart(2, '0')}`;

const meetingLink = (...texts) => {
  const joined = texts.filter(Boolean).join('\n');
  for (const re of MEETING) {
    const m = re.exec(joined);
    if (m) return m[0].replace(/[).,;]+$/, '');
  }
  return undefined;
};

/**
 * The events of an iCalendar text that overlap [fromMs, toMs), recurring ones expanded, as protocol events:
 * { id, calendarId, title, start, end, allDay, location?, meetingUrl?, url? } — timed events in ISO UTC, all-day
 * ones as YYYY-MM-DD (end exclusive).
 */
export const eventsBetween = (icsText, fromMs, toMs, calendarId = '') => {
  const root = typeof icsText === 'string' ? parseICS(icsText) : icsText;
  const toInstant = zoneResolver(root);
  const vevents = all(root, 'VEVENT');
  const overrides = new Map(); // "uid|instant" → override VEVENT
  for (const ev of vevents) {
    const rid = first(ev, 'RECURRENCE-ID');
    const uid = text(ev, 'UID');
    if (rid && uid) {
      const w = parseWall(rid.value);
      if (w) overrides.set(`${uid}|${toInstant(w, rid.params.TZID)}`, ev);
    }
  }

  const out = [];
  const add = (ev, startW, tzid, durationMs, key) => {
    const startMs = startW.date ? toInstant({ ...startW, utc: false }, undefined) : toInstant(startW, tzid);
    const endMs = startMs + durationMs;
    if (endMs <= fromMs || startMs >= toMs) return;
    const title = text(ev, 'SUMMARY') ?? '（无标题）';
    const location = text(ev, 'LOCATION');
    const description = text(ev, 'DESCRIPTION');
    const url = text(ev, 'URL');
    const e = {
      id: key,
      calendarId,
      title,
      allDay: startW.date,
      start: startW.date ? isoDate(startW) : iso(startMs),
      end: startW.date ? isoDate(fromWallMs(wallMs(startW) + durationMs, true)) : iso(endMs),
    };
    if (location) e.location = location;
    const meeting = meetingLink(text(ev, 'X-GOOGLE-CONFERENCE'), url, location, description);
    if (meeting) e.meetingUrl = meeting;
    if (url && url !== meeting) e.url = url;
    out.push(e);
  };

  for (const ev of vevents) {
    if ((text(ev, 'STATUS') ?? '').toUpperCase() === 'CANCELLED') continue;
    const ds = first(ev, 'DTSTART');
    const start = ds && parseWall(ds.value);
    if (!start) continue;
    if (ds.params.VALUE === 'DATE') start.date = true;
    const tzid = start.utc ? undefined : ds.params.TZID;
    const de = first(ev, 'DTEND');
    const end = de && parseWall(de.value);
    let durationMs;
    if (end) {
      durationMs = start.date ? wallMs(end) - wallMs(start) : toInstant(end, end.utc ? undefined : de.params.TZID ?? tzid) - toInstant(start, tzid);
    } else {
      durationMs = parseDuration(text(ev, 'DURATION')) ?? (start.date ? DAY_MS : 0);
    }
    durationMs = Math.max(0, durationMs);
    const uid = text(ev, 'UID') ?? `${text(ev, 'SUMMARY')}|${ds.value}`;
    const rrule = first(ev, 'RRULE')?.value;

    if (first(ev, 'RECURRENCE-ID')) {
      add(ev, start, tzid, durationMs, `${uid}|${ds.value}`); // an overridden instance, as its own event
      continue;
    }
    if (!rrule) {
      add(ev, start, tzid, durationMs, uid);
      continue;
    }
    const exdates = new Set();
    for (const ex of ev.props.EXDATE ?? []) {
      for (const v of ex.value.split(',')) {
        const w = parseWall(v);
        if (w) exdates.add(w.date || start.date ? isoDate(w) : toInstant(w, w.utc ? undefined : ex.params.TZID ?? tzid));
      }
    }
    // Occurrences can start before `fromMs` and still overlap it; walk the wall clock up to the range end (+1 day
    // of slack for zone offsets).
    const endWall = toMs + 2 * DAY_MS + Math.abs(new Date().getTimezoneOffset()) * 60000;
    for (const w of occurrences(start, rrule, endWall, toInstant, tzid)) {
      const instant = w.date ? isoDate(w) : toInstant(w, tzid);
      if (exdates.has(instant)) continue;
      const startMs = w.date ? toInstant({ ...w, utc: false }, undefined) : instant;
      if (overrides.has(`${uid}|${startMs}`)) continue; // that instance was moved or changed: the override shows it
      add(ev, w, tzid, durationMs, `${uid}|${w.date ? isoDate(w) : iso(startMs)}`);
    }
  }
  out.sort((a, b) => new Date(a.start).getTime() - new Date(b.start).getTime());
  return out;
};

/** Calendar names in an iCalendar file (X-WR-CALNAME), for subscriptions. */
export const calendarName = (icsText) => {
  const root = typeof icsText === 'string' ? parseICS(icsText) : icsText;
  const cal = all(root, 'VCALENDAR')[0];
  return cal ? text(cal, 'X-WR-CALNAME') : undefined;
};
