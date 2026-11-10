// Reads the macOS Calendar database through EventKit (JavaScript for Automation). Run as:
//   osascript -l JavaScript calendar.js <from-epoch-seconds> <to-epoch-seconds>
// Prints one JSON line: { calendars: [...], events: [...] } or { error }.
ObjC.import('EventKit');
ObjC.import('AppKit');
ObjC.import('Foundation');

function run(argv) {
  try {
    return JSON.stringify(read(Number(argv[0]), Number(argv[1])));
  } catch (e) {
    return JSON.stringify({ error: 'failed', message: String(e) });
  }
}

function read(from, to) {
  const store = $.EKEventStore.alloc.init;
  // 0 notDetermined, 1 restricted, 2 denied, 3 full access, 4 write only.
  let status = Number($.EKEventStore.authorizationStatusForEntityType(0));
  if (status === 0) {
    let done = false;
    const done_ = () => { done = true; };
    if (store.respondsToSelector('requestFullAccessToEventsWithCompletion:')) {
      store.requestFullAccessToEventsWithCompletion(done_);
    } else {
      store.requestAccessToEntityTypeCompletion(0, done_);
    }
    const loop = $.NSRunLoop.currentRunLoop;
    const until = Date.now() + 120000;
    while (!done && Date.now() < until) loop.runUntilDate($.NSDate.dateWithTimeIntervalSinceNow(0.1));
    status = Number($.EKEventStore.authorizationStatusForEntityType(0));
  }
  if (status !== 3) return { error: status === 1 ? 'restricted' : 'denied' };

  const cals = store.calendarsForEntityType(0);
  const calendars = [];
  const list = [];
  for (let i = 0; i < Number(cals.count); i++) {
    const c = cals.objectAtIndex(i);
    list.push(c);
    let color;
    try {
      const rgb = c.color.colorUsingColorSpace($.NSColorSpace.sRGBColorSpace);
      const h = (v) => Math.round(Number(v) * 255).toString(16).padStart(2, '0');
      color = '#' + h(rgb.redComponent) + h(rgb.greenComponent) + h(rgb.blueComponent);
    } catch (e) { /* no colour */ }
    const o = { id: ObjC.unwrap(c.calendarIdentifier), name: ObjC.unwrap(c.title) };
    try { o.account = ObjC.unwrap(c.source.title); } catch (e) { /* none */ }
    if (color) o.color = color;
    calendars.push(o);
  }

  const events = [];
  if (list.length) {
    const start = $.NSDate.dateWithTimeIntervalSince1970(from);
    const end = $.NSDate.dateWithTimeIntervalSince1970(to);
    const pred = store.predicateForEventsWithStartDateEndDateCalendars(start, end, cals);
    const found = store.eventsMatchingPredicate(pred);
    const pad = (n) => String(n).padStart(2, '0');
    const day = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
    for (let i = 0; i < Number(found.count); i++) {
      const e = found.objectAtIndex(i);
      if (Number(e.status) === 3) continue; // cancelled
      const s = new Date(Number(e.startDate.timeIntervalSince1970) * 1000);
      let en = new Date(Number(e.endDate.timeIntervalSince1970) * 1000);
      const allDay = Boolean(e.isAllDay);
      const o = {
        id: ObjC.unwrap(e.eventIdentifier) + '|' + s.getTime(),
        calendarId: ObjC.unwrap(e.calendar.calendarIdentifier),
        title: ObjC.unwrap(e.title) || '（无标题）',
        allDay,
      };
      if (allDay) {
        // EventKit ends an all-day event on its last day (23:59); the plugin protocol wants the day after.
        en = new Date(en.getFullYear(), en.getMonth(), en.getDate() + 1);
        o.start = day(s);
        o.end = day(en);
      } else {
        o.start = s.toISOString();
        o.end = en.toISOString();
      }
      const loc = ObjC.unwrap(e.location);
      if (loc) o.location = loc;
      try { const u = ObjC.unwrap(e.URL.absoluteString); if (u) o.url = u; } catch (x) { /* none */ }
      const notes = ObjC.unwrap(e.notes);
      if (notes) o.notes = notes.slice(0, 2000);
      events.push(o);
    }
  }
  return { calendars, events };
}
