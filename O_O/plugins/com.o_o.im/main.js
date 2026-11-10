// First-party instant-message notifier (docs/design/08-notifications-and-satellites.md): reads WeChat's and QQ's system
// notifications (never their processes) and shows them as separate capsules beside the main island, each with the source
// app's own icon: a new message (for a few seconds) and, while there is anything unread, a count that stays. What it sees of the message (sender and text, sender only, or
// nothing) is the user's choice in the console; it adapts to whatever the host hands it.
import { island, ui, notifications, settings } from '@island/api';

const APPS = ['com.tencent.xinWeChat', 'com.tencent.qq'];

const satellite = island.satellite('message');
const lastNote = new Map(); // app id → its latest notification, for the unread capsule's click

const unread = new Map();   // app id → unread count (the platform's badge when it knows, else what we counted)
const burst = new Map();    // app id → notices shown in the current burst (merged into one satellite notice)
const timers = new Map();

const muted = (n) => {
  const list = String(settings.get('mute') ?? '').split(/[,，]/).map((s) => s.trim()).filter(Boolean);
  const who = `${n.title ?? ''}`;
  return list.some((name) => who === name || who.includes(name));
};

// The count often comes from the Dock badge with no notification seen this session: open the app itself then.
const openApp = (app) => {
  const n = lastNote.get(app);
  (n ? notifications.activate(n) : Promise.resolve(false))
    .then((ok) => ok || notifications.activateApp(app))
    .catch(() => notifications.activateApp(app).catch(() => {}));
};

const total = () => [...unread.values()].reduce((a, b) => a + b, 0);

const renderUnread = () => {
  const sum = total();
  if (sum <= 0) { satellite.hide('unread'); return; }
  const top = [...unread.entries()].sort((a, b) => b[1] - a[1])[0][0];
  satellite.show(
    ui.row({ gap: 'xs', align: 'center' }, [
      ui.appIcon(top, { size: 18 }),
      ui.text(sum > 99 ? '99+' : String(sum), { size: 'sm', weight: 'semibold', tabular: true }),
    ]),
    { key: 'unread', seconds: 0, onClick: () => { openApp(top); unread.clear(); renderUnread(); } },
  );
};

const show = (n) => {
  const app = n.app.id;
  lastNote.set(app, n);
  const count = (burst.get(app) ?? 0) + 1;
  burst.set(app, count);
  const seconds = Math.max(1, Math.min(10, Number(settings.get('seconds') ?? 4)));
  // Reset the merge window once the notice has gone.
  clearTimeout(timers.get(app));
  timers.set(app, setTimeout(() => burst.delete(app), seconds * 1000 + 1500));

  const who = n.title ?? n.app.name;
  const line = n.body ?? (n.title ? '发来新消息' : '有新消息');
  satellite.show(
    ui.row({ gap: 'sm', align: 'center' }, [
      ui.appIcon(app, { size: 22 }),
      ui.stack({ gap: 'xs', grow: true }, [
        ui.text(who, { size: 'sm', weight: 'semibold', lines: 1 }),
        ui.text(count > 1 && !n.body ? `${count} 条新消息` : line, { size: 'xs', tone: 'secondary', lines: 1 }),
      ]),
    ]),
    { key: 'msg', seconds, badge: count > 1 ? count : undefined, onClick: () => openApp(app) },
  );
};

export async function activate() {
  notifications.onReceived((n) => {
    if (muted(n)) return;
    unread.set(n.app.id, (unread.get(n.app.id) ?? 0) + 1);
    renderUnread();
    show(n);
  });
  // The platform's own count (Dock badge) is the truth when it has one: it also drops when the user reads the message.
  notifications.onBadge((app, count) => {
    if (count == null || count <= 0) unread.delete(app); else unread.set(app, count);
    renderUnread();
  });
  for (const app of APPS) {
    const n = await notifications.unread(app).catch(() => null);
    if (n) unread.set(app, n);
  }
  renderUnread();
}
