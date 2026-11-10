// First-party to-do list (docs/design/05-builtin-plugins.md §4). The island has no text input (typing there would
// take the keyboard from the app the user works in), so tasks come in two ways:
//   1. the "添加待办" box in the console (this plugin's settings): every NEW line becomes a task — lines already
//      seen are not added again, so the box can be kept or cleared, as the user likes;
//   2. the "从剪贴板添加" button on the page: each line of the copied text becomes a task.
// Both understand simple dates and times ("明天 下午3点 交周报", "!周五 发版"). On the island tasks are ticked off,
// starred, deleted, or handed to the focus timer. Other plugins see the list through the "todo" provider.
import { island, ui, settings, storage, clipboard, notify } from '@island/api';
import { parseTask } from './parse.js';

// A system notification as well, when the user wants one (setting "systemNotify"); never fails the caller.
const systemNotify = (title, body) => {
  if (notify && settings.get('systemNotify') !== false) notify.show({ title, body }).catch(() => {});
};


const PER_PAGE = 5;
const MINUTE = 60 * 1000;
const DAY = 24 * 60 * MINUTE;
const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
const pad = (n) => String(n).padStart(2, '0');
const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

// "今天 15:30", "明天", "11月12日 周三", "已过期 2 小时"
const dueLabel = (t, now) => {
  if (!t.due) return '';
  const d = new Date(t.due);
  const time = t.allDay ? '' : ` ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  if (!t.done && d < now && !(t.allDay && sameDay(d, now))) {
    const m = Math.round((now - d) / MINUTE);
    return m < 60 ? `已过期 ${m} 分钟` : m < 1440 ? `已过期 ${Math.round(m / 60)} 小时` : `已过期 ${Math.round(m / 1440)} 天`;
  }
  if (sameDay(d, now)) return `今天${time}`;
  if (sameDay(d, new Date(now.getTime() + DAY))) return `明天${time}`;
  return `${d.getMonth() + 1}月${d.getDate()}日 ${WEEK[d.getDay()]}${time}`;
};

export function activate() {
  let tasks = storage.get('tasks') ?? [];   // { id, title, due?, allDay, high, done, doneAt?, created, pomodoros }
  let nextId = tasks.reduce((m, t) => Math.max(m, t.id), 0) + 1;
  let view = 'open';
  let page = 0;
  let notice = null;
  let focusAvailable = false;
  let dueAct = null;
  const alerted = new Set(storage.get('alerted') ?? []);

  let addCount = 0;
  const save = () => { storage.set('tasks', tasks); storage.set('alerted', [...alerted]); };

  const add = (line) => {
    const p = parseTask(line);
    if (!p.title) return null;
    const task = { id: nextId++, title: p.title, due: p.due?.toISOString() ?? null, allDay: p.allDay, high: p.high,
                   done: false, created: new Date().toISOString(), pomodoros: 0 };
    tasks.push(task);
    return task;
  };

  // Lines of the console box that were not there last time become tasks (a multiset difference, so a line typed
  // twice adds two tasks, and removing a line never removes a task).
  const importFromSettings = () => {
    const lines = (settings.get('add') ?? '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    const seen = storage.get('seenLines') ?? [];
    const left = [...seen];
    let added = 0;
    for (const line of lines) {
      const i = left.indexOf(line);
      if (i >= 0) left.splice(i, 1);
      else if (add(line)) added++;
    }
    storage.set('seenLines', lines);
    return added;
  };

  const fromClipboard = async () => {
    notice = null;
    try {
      const entry = await clipboard.read();
      const lines = (entry?.text ?? '').split(/\r?\n/).map((l) => l.trim()).filter(Boolean).slice(0, 20);
      if (lines.length === 0) {
        notice = '剪贴板里没有文字。复制一行或几行文字后再点。';
      } else {
        lines.forEach(add);
        save();
        island.alert({ leading: ui.icon('ListPlus'), center: ui.text(`已添加 ${lines.length} 项`, { size: 'sm', weight: 'semibold' }), seconds: 2 });
      }
    } catch (e) {
      notice = e.message;
    }
    render();
  };

  const purge = () => {
    const cutoff = Date.now() - settings.get('keepDoneDays') * DAY;
    tasks = tasks.filter((t) => !t.done || new Date(t.doneAt).getTime() >= cutoff);
  };

  const toggle = (t) => {
    t.done = !t.done;
    t.doneAt = t.done ? new Date().toISOString() : undefined;
    save();
    render();
  };
  const star = (t) => { t.high = !t.high; save(); render(); };
  const remove = (t) => { tasks = tasks.filter((x) => x !== t); save(); render(); };
  const focusOn = (t) => {
    island.providers.call('focus', 'focus', 'start', { id: t.id, title: t.title })
      .catch((e) => { notice = `无法开始专注：${e.message}`; render(); });
  };

  // Open tasks: overdue and timed ones first by time, then starred, then the rest as entered.
  const order = (a, b) => {
    const da = a.due ? new Date(a.due).getTime() : Infinity;
    const db = b.due ? new Date(b.due).getTime() : Infinity;
    return da - db || (b.high ? 1 : 0) - (a.high ? 1 : 0) || a.id - b.id;
  };

  const row = (t, now) => {
    const overdue = !t.done && t.due && new Date(t.due) < now && !(t.allDay && sameDay(new Date(t.due), now));
    return ui.item({ key: t.id, gap: 'sm', align: 'center', onClick: () => toggle(t) }, [
      ui.icon(t.done ? 'CircleCheck' : 'Circle', { tone: t.done ? 'secondary' : t.high ? 'accent' : 'secondary', size: 16 }),
      ui.text(t.title, { grow: true, tone: t.done ? 'secondary' : undefined, weight: t.high && !t.done ? 'semibold' : undefined }),
      t.pomodoros ? ui.text(`🍅${t.pomodoros}`, { tone: 'secondary', size: 'xs' }) : null,
      ui.text(dueLabel(t, now), { tone: overdue ? 'accent' : 'secondary', size: 'xs' }),
      !t.done && focusAvailable ? ui.iconButton('Timer', { label: '专注此任务', onClick: () => focusOn(t) }) : null,
      !t.done ? ui.iconButton(t.high ? 'StarOff' : 'Star', { label: t.high ? '取消重要' : '标为重要', onClick: () => star(t) }) : null,
      ui.iconButton('X', { label: '删除', onClick: () => remove(t) }),
    ]);
  };

  function render() {
    const now = new Date();
    const open = tasks.filter((t) => !t.done).sort(order);
    const done = tasks.filter((t) => t.done).sort((a, b) => new Date(b.doneAt) - new Date(a.doneAt));
    const shown = view === 'open' ? open : done;
    const pages = Math.max(1, Math.ceil(shown.length / PER_PAGE));
    page = Math.min(page, pages - 1);
    const dueToday = open.filter((t) => t.due && new Date(t.due) <= new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1)).length;

    island.home.page('list').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      // Quick add: type and press Enter ("明天 15:30 交周报", "!" in front marks it important).
      ui.input({
        key: `add-${addCount}`, placeholder: '添加待办，回车保存（如"明天 15:30 交周报"）',
        onSubmit: (text) => {
          if (add(text.trim())) {
            addCount++; // a fresh, empty box for the next one
            view = 'open';
            save();
            render();
          }
        },
      }),
      ui.row({ gap: 'xs', align: 'center' }, [
        ui.tabs({ value: view, onChange: (v) => { view = v; page = 0; render(); } },
          [ui.option('open', `待办 ${open.length || ''}`.trim()), ui.option('done', '已完成')]),
        ui.spacer(),
        ui.iconButton('ClipboardPaste', { label: '从剪贴板添加', onClick: fromClipboard }),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
      ]),
      shown.length === 0
        ? ui.stack({ gap: 'xs' }, view === 'open'
          ? [ui.text('没有待办', { weight: 'semibold' }),
             ui.text('在控制台 › 插件 › 待办 的"添加待办"里每行写一项，或复制几行文字后点右上角的粘贴按钮。', { tone: 'secondary', size: 'sm', wrap: true })]
          : [ui.text('还没有完成的事项。', { tone: 'secondary' })])
        : ui.stack({ gap: 'xs', align: 'stretch' }, shown.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map((t) => row(t, now))),
      pages > 1 ? ui.dots({ count: pages, index: page }) : null,
      notice ? ui.text(notice, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: ui.text(open.length === 0 ? '' : dueToday > 0 ? `${open.length} 项 · ${dueToday} 项今天到期` : `${open.length} 项`, { tone: 'secondary', size: 'sm' }) });

    if (open.length === 0) island.ambient('count').remove();
    else island.ambient('count').update(ui.text(`${open.length} 项待办`, { size: 'sm', weight: 'semibold' }));
  }

  // Timed tasks close to their time: shown on the collapsed island ahead of time (passive), owning it for the first
  // ten minutes after the time (active, opening the island shows the list), plus one alert when the time comes.
  const checkDue = () => {
    const now = Date.now();
    const before = settings.get('remindBefore') * MINUTE;
    const near = tasks
      .filter((t) => !t.done && t.due && !t.allDay)
      .map((t) => ({ t, at: new Date(t.due).getTime() }))
      .filter(({ at }) => at - now <= before && now - at <= 60 * MINUTE)
      .sort((a, b) => a.at - b.at);
    for (const { t, at } of near) {
      if (at <= now && !alerted.has(t.id)) {
        alerted.add(t.id);
        save();
        island.alert({ leading: ui.icon('AlarmClock', { tone: 'accent' }), center: ui.text(t.title, { size: 'sm', weight: 'semibold' }), seconds: 6, priority: 'high' });
        systemNotify('待办到期', t.title);
      }
    }
    if (near.length === 0) {
      dueAct?.end();
      dueAct = null;
      return;
    }
    const { t, at } = near[0];
    const mins = Math.round((at - now) / MINUTE);
    const when = mins > 0 ? `${mins} 分钟后` : '到时间了';
    const spec = {
      // Ahead of time it waits its turn; for the first ten minutes after the time it comes first.
      priority: at <= now && now - at <= 10 * MINUTE ? 'high' : 'passive',
      compact: {
        leading: ui.icon('AlarmClock', { tone: at <= now ? 'accent' : 'secondary', size: 16 }),
        trailing: ui.text(when, { size: 'sm', weight: 'semibold' }),
      },
      minimal: ui.icon('AlarmClock', { tone: at <= now ? 'accent' : 'secondary', size: 14 }),
      expanded: {
        leading: ui.icon('AlarmClock', { tone: 'accent', size: 24 }),
        center: ui.stack({ gap: 'xs' }, [
          ui.text(t.title, { weight: 'semibold', lines: 1 }),
          ui.text(when, { tone: 'secondary', size: 'sm' }),
        ]),
        trailing: ui.button('完成', { variant: 'ghost', onClick: () => toggle(t) }),
      },
    };
    if (dueAct) dueAct.update(spec); else dueAct = island.activity('due', spec);
  };

  island.provide('todo', 'todo', {
    /** Open tasks, in list order: [{ id, title, due, high }] */
    list: () => tasks.filter((t) => !t.done).sort(order).map(({ id, title, due, high }) => ({ id, title, due, high })),
    complete: (id) => { const t = tasks.find((x) => x.id === id); if (t && !t.done) toggle(t); return !!t; },
    /** The focus timer finished a pomodoro on this task. */
    addPomodoro: (id) => { const t = tasks.find((x) => x.id === id); if (t) { t.pomodoros = (t.pomodoros ?? 0) + 1; save(); render(); } return !!t; },
  });

  const refreshProviders = () => island.providers.list('focus')
    .then((list) => { focusAvailable = list.some((p) => p.running); render(); })
    .catch(() => {});

  settings.onChange((key) => {
    if (key === 'add' && importFromSettings() > 0) save();
    purge();
    render();
    checkDue();
  });
  island.providers.onChange(refreshProviders);

  if (importFromSettings() > 0) save();
  purge();
  render();
  checkDue();
  refreshProviders();
  setInterval(() => { render(); checkDue(); }, 30 * 1000);
}
