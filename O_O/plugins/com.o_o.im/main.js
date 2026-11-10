// First-party instant-message notifier (docs/design/08-notifications-and-satellites.md, 09-island-v2.md): reads WeChat's
// and QQ's system notifications (never their processes). A new message is an alert with the source app's own icon; a
// burst from one app is merged into one. While anything is unread, a quiet live activity keeps the count (as the bubble
// beside the island when something else holds it), and its card opens the apps. What it sees of a message (sender and
// text, sender only, or nothing) is the user's choice in the console; it adapts to whatever the host hands it.
import { island, ui, notifications, settings } from '@island/api';

const APPS = ['com.tencent.xinWeChat', 'com.tencent.qq'];
const NAMES = { 'com.tencent.xinWeChat': '微信', 'com.tencent.qq': 'QQ' };

const lastNote = new Map(); // app id → its latest notification, for opening it
const unread = new Map();   // app id → unread count (the platform's badge when it knows, else what we counted)
const burst = new Map();    // app id → notices shown in the current burst (merged into one alert)
const timers = new Map();
let unreadActivity = null;

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
const count = (n) => (n > 99 ? '99+' : String(n));

const renderUnread = () => {
  const sum = total();
  if (sum <= 0) {
    unreadActivity?.end();
    unreadActivity = null;
    return;
  }
  const apps = [...unread.entries()].filter(([, n]) => n > 0).sort((a, b) => b[1] - a[1]);
  const top = apps[0][0];
  const spec = {
    open: { app: top },
    compact: {
      leading: ui.appIcon(top, { size: 16 }),
      trailing: ui.text(count(sum), { size: 'sm', weight: 'semibold', tabular: true }),
    },
    minimal: ui.row({ gap: 'xs', align: 'center' }, [ui.appIcon(top, { size: 14 }), ui.text(count(sum), { size: 'xs', weight: 'semibold', tabular: true })]),
    expanded: {
      leading: ui.appIcon(top, { size: 28 }),
      center: ui.stack({ gap: 'xs' }, [
        ui.text(`${count(sum)} 条未读消息`, { weight: 'semibold' }),
        ui.text(apps.map(([app, n]) => `${NAMES[app] ?? app} ${count(n)}`).join(' · '), { tone: 'secondary', size: 'sm' }),
      ]),
      bottom: ui.row({ gap: 'sm' }, apps.map(([app]) => ui.button(`打开${NAMES[app] ?? ''}`, { variant: 'ghost', onClick: () => openApp(app) }))),
    },
  };
  if (unreadActivity) unreadActivity.update(spec); else unreadActivity = island.activity('unread', spec);
};

const show = (n) => {
  const app = n.app.id;
  lastNote.set(app, n);
  const merged = (burst.get(app) ?? 0) + 1;
  burst.set(app, merged);
  const seconds = Math.max(1, Math.min(10, Number(settings.get('seconds') ?? 4)));
  // Reset the merge window once the alert has gone.
  clearTimeout(timers.get(app));
  timers.set(app, setTimeout(() => burst.delete(app), seconds * 1000 + 1500));

  const who = n.title ?? n.app.name;
  const line = n.body ?? (n.title ? '发来新消息' : '有新消息');
  // One line, like the island it opens from: sender, then the text cut with "…". This plugin's alerts replace each
  // other, so a burst updates one alert in place.
  island.alert({
    leading: ui.appIcon(app, { size: 18 }),
    center: ui.row({ gap: 'sm', align: 'center' }, [
      ui.text(who, { size: 'sm', weight: 'semibold', lines: 1 }),
      ui.text(merged > 1 && !n.body ? `${merged} 条新消息` : line, { size: 'xs', tone: 'secondary', lines: 1, grow: true }),
    ]),
    trailing: merged > 1 ? ui.badge(String(merged)) : undefined,
    seconds,
  });
};

export async function activate() {
  notifications.onReceived((n) => {
    if (muted(n)) return;
    unread.set(n.app.id, (unread.get(n.app.id) ?? 0) + 1);
    renderUnread();
    show(n);
  });
  // The platform's own count (Dock badge) is the truth when it has one: it also drops when the user reads the message.
  notifications.onBadge((app, n) => {
    if (n == null || n <= 0) unread.delete(app); else unread.set(app, n);
    renderUnread();
  });
  for (const app of APPS) {
    const n = await notifications.unread(app).catch(() => null);
    if (n) unread.set(app, n);
  }
  renderUnread();
}
