// First-party focus timer (docs/design/05-builtin-plugins.md §5): pomodoros of work, short and long breaks. While a
// phase runs the collapsed island shows the time left (a chip) and opening the island goes to this page (an active
// activity). A pomodoro can be tied to a task of the to-do plugin ("todo" provider); finishing one counts for the
// day and for that task. The state survives restarts: a phase that ended while O_O was closed finishes on start.
import { island, ui, settings, storage, notify } from '@island/api';

// A system notification as well, when the user wants one (setting "systemNotify"); never fails the caller.
const systemNotify = (title, body) => {
  if (notify && settings.get('systemNotify') !== false) notify.show({ title, body }).catch(() => {});
};


const MINUTE = 60 * 1000;
const PHASES = { work: '专注', short: '短休息', long: '长休息' };
const pad = (n) => String(n).padStart(2, '0');
const clock = (ms) => { const s = Math.max(0, Math.ceil(ms / 1000)); return `${pad(Math.floor(s / 60))}:${pad(s % 60)}`; };
const dayKey = (d = new Date()) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;

export function activate() {
  // { phase, running, endsAt (ms, while running), remaining (ms, while paused), cycle (pomodoros since the last long break), task }
  let state = storage.get('state') ?? { phase: 'work', running: false, remaining: null, cycle: 0, task: null };
  const days = storage.get('days') ?? {};   // 'YYYY-MM-DD' → finished pomodoros
  let tasks = [];                           // open to-dos, for binding
  let todoAvailable = false;
  let session = null;
  let ticker = null;

  const length = (phase) => settings.get(phase) * MINUTE;
  const left = () => state.running ? state.endsAt - Date.now() : state.remaining ?? length(state.phase);
  const save = () => { storage.set('state', state); storage.set('days', days); };
  const today = () => days[dayKey()] ?? 0;

  const start = () => {
    if (state.running) return;
    state.endsAt = Date.now() + left(); // left() before running is set: it reads the paused or full length
    state.running = true;
    state.remaining = null;
    save();
    update();
  };
  const pause = () => {
    if (!state.running) return;
    state.remaining = state.endsAt - Date.now();
    state.running = false;
    save();
    update();
  };
  const reset = () => {
    state = { ...state, running: false, remaining: null, endsAt: undefined };
    save();
    update();
  };

  // Ends the phase. `finished`: it ran out (a work phase then counts); otherwise it was skipped.
  const next = (finished) => {
    const was = state.phase;
    if (was === 'work' && finished) {
      days[dayKey()] = today() + 1;
      // Keep a month of history.
      for (const k of Object.keys(days).sort().slice(0, -31)) delete days[k];
      if (state.task) {
        // Credited to the task when the to-do plugin answers; kept until then (it may not be running yet).
        storage.set('credit', [...(storage.get('credit') ?? []), state.task.id]);
        flushCredit();
      }
    }
    if (was === 'work') state.cycle += 1;
    const phase = was !== 'work' ? 'work' : state.cycle % settings.get('longEvery') === 0 ? 'long' : 'short';
    const auto = finished && settings.get('autoNext');
    state = { ...state, phase, running: auto, endsAt: auto ? Date.now() + length(phase) : undefined, remaining: null };
    if (phase === 'work' && was === 'long') state.cycle = 0;
    save();
    if (finished) {
      island.alert({
        leading: ui.icon(was === 'work' ? 'Coffee' : 'Timer', { tone: 'accent' }),
        trailing: ui.text(was === 'work' ? `完成一个番茄，${PHASES[phase]}一下` : '休息结束，开始专注吧', { size: 'sm', weight: 'semibold' }),
      }, { seconds: 6 });
      systemNotify(was === 'work' ? '完成一个番茄' : '休息结束', was === 'work' ? `${PHASES[phase]}一下吧。` : '开始下一个番茄吧。');
    }
    update();
  };

  const bind = (value) => {
    const t = tasks.find((x) => String(x.id) === value);
    state.task = t ? { id: t.id, title: t.title } : null;
    save();
    render();
  };

  const flushCredit = async () => {
    for (const id of storage.get('credit') ?? []) {
      try {
        await island.providers.call('todo', 'todo', 'addPomodoro', id);
      } catch {
        return; // not running: try again when it is
      }
      const rest = [...(storage.get('credit') ?? [])];
      rest.splice(rest.indexOf(id), 1);
      storage.set('credit', rest);
    }
  };

  const loadTasks = async () => {
    try {
      const list = await island.providers.list('todo');
      todoAvailable = list.some((p) => p.running);
      if (todoAvailable) await flushCredit();
      tasks = todoAvailable ? await island.providers.call('todo', 'todo', 'list') : [];
    } catch {
      todoAvailable = false;
      tasks = [];
    }
    render();
  };

  function render() {
    const total = length(state.phase);
    const remaining = left();
    const fresh = !state.running && state.remaining == null;
    const n = today();

    const taskLine = todoAvailable
      ? ui.row({ gap: 'xs', align: 'center' }, [
          ui.icon('ListTodo', { tone: 'secondary', size: 16 }),
          ui.picker({ value: state.task ? String(state.task.id) : '', label: '不关联待办', onChange: bind }, [
            ui.option('', '不关联待办'),
            ...(state.task && !tasks.some((t) => t.id === state.task.id) ? [ui.option(String(state.task.id), state.task.title)] : []),
            ...tasks.slice(0, 20).map((t) => ui.option(String(t.id), t.title)),
          ]),
        ])
      : state.task ? ui.text(state.task.title, { tone: 'secondary', size: 'sm' }) : null;

    island.page('timer').update(ui.stack({ gap: 'md', align: 'stretch' }, [
      ui.row({ gap: 'md', align: 'end' }, [
        ui.stack({ gap: 'xs' }, [
          ui.text(PHASES[state.phase], { tone: state.phase === 'work' ? 'accent' : 'secondary', weight: 'semibold', size: 'sm' }),
          ui.text(clock(remaining), { size: '4xl', tabular: true }),
        ]),
        ui.spacer(),
        ui.button(state.running ? '暂停' : fresh ? '开始' : '继续', { variant: 'ghost', onClick: state.running ? pause : start }),
        ui.iconButton('SkipForward', { label: '跳过这一段', onClick: () => next(false) }),
        ui.iconButton('RotateCcw', { label: '重新开始这一段', disabled: fresh, onClick: reset }),
      ]),
      ui.progress({ value: Math.round((1 - remaining / total) * 1000) / 10, max: 100 }),
      ui.row({ gap: 'sm', align: 'center' }, [
        taskLine,
        ui.spacer(),
        ui.text(n > 0 ? `今天完成 ${n} 个番茄` : '今天还没有完成番茄', { tone: 'secondary', size: 'xs' }),
      ]),
    ]), { status: ui.text(n > 0 ? `今日 ${n} 🍅` : '', { tone: 'secondary', size: 'sm' }) });

    // Collapsed: the time left, while a phase is under way (running or paused part-way).
    if (fresh) island.chip('remaining').remove();
    else island.chip('remaining').update(ui.row({ gap: 'xs', align: 'center' }, [
      ui.icon(state.phase === 'work' ? (state.running ? 'Timer' : 'Pause') : 'Coffee', { tone: state.phase === 'work' ? 'accent' : 'secondary', size: 14 }),
      ui.text(clock(remaining), { size: 'sm', weight: 'semibold', tabular: true }),
    ]));
  }

  // Runs while a phase runs: the time, the end of the phase, and the activity that brings the user here.
  function update() {
    if (state.running && state.endsAt <= Date.now()) {
      next(true);
      return;
    }
    if (state.running) {
      session ??= island.activity.create('session', { title: '专注中', priority: 'active', page: 'timer' });
      session.update({});
      ticker ??= setInterval(update, 1000);
    } else {
      session?.end();
      session = null;
      if (ticker) { clearInterval(ticker); ticker = null; }
    }
    render();
  }

  island.provide('focus', 'focus', {
    /** Starts a work phase, tied to a to-do task ({ id, title }) or to none. */
    start: (task) => {
      state = { ...state, phase: 'work', running: false, remaining: null, task: task ? { id: task.id, title: String(task.title) } : null };
      start();
      loadTasks();
      return true;
    },
    state: () => ({ phase: state.phase, running: state.running, remainingMs: left(), today: today(), task: state.task }),
  });

  settings.onChange(() => update());
  island.providers.onChange(loadTasks);
  update();
  loadTasks();
  setInterval(loadTasks, 60 * 1000); // to-dos change elsewhere; keep the picker current
}
