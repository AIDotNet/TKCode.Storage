// Runs once in every plugin realm before any module. A bare Jaunt engine has no console, timers or event loop; the
// host provides them here on top of a few native functions (__island_*) it registers per realm.
//
// Privacy: the natives are captured into this closure and deleted from globalThis, so plugin code cannot call them
// directly. @island/api, which the host loads before any plugin code, collects them once through __island_handoff.
(() => {
  const natives = {
    log: __island_log,
    setTimer: __island_setTimer,
    clearTimer: __island_clearTimer,
    activity: __island_activity,
    storage: __island_storage,
    settings: __island_settings,
    call: __island_call,
    reply: __island_reply,
  };
  const permissions = Object.freeze(JSON.parse(__island_permissions));
  for (const name of ['__island_log', '__island_setTimer', '__island_clearTimer', '__island_activity',
                      '__island_storage', '__island_settings', '__island_call', '__island_reply',
                      '__island_permissions']) {
    delete globalThis[name];
  }
  const host = natives;

  const format = (args) => args.map((a) => {
    if (typeof a === 'string') return a;
    try { return JSON.stringify(a); } catch { return String(a); }
  }).join(' ');

  globalThis.console = Object.freeze({
    log: (...a) => host.log('info', format(a)),
    info: (...a) => host.log('info', format(a)),
    warn: (...a) => host.log('warn', format(a)),
    error: (...a) => host.log('error', format(a)),
    debug: (...a) => host.log('debug', format(a)),
  });

  // Timers live in JS; the host only schedules wake-ups and calls __island_dispatch('timer', id) when one is due.
  const timers = new Map();
  let nextTimer = 1;
  const addTimer = (fn, ms, repeat, args) => {
    if (typeof fn !== 'function') throw new TypeError('Timer callback must be a function');
    const id = nextTimer++;
    timers.set(id, { fn, args, repeat });
    host.setTimer(id, Math.max(0, Number(ms) || 0), repeat);
    return id;
  };
  const clear = (id) => { if (timers.delete(id)) host.clearTimer(id); };
  globalThis.setTimeout = (fn, ms, ...args) => addTimer(fn, ms, false, args);
  globalThis.setInterval = (fn, ms, ...args) => addTimer(fn, ms, true, args);
  globalThis.clearTimeout = clear;
  globalThis.clearInterval = clear;

  // Event handlers of the views most recently rendered, per activity: id → function. Replaced on every render so
  // handlers of views that are gone do not leak.
  const handlers = new Map();
  let nextHandler = 1;
  const handlerSets = new Map(); // activity id → { ids (in view order), shape (the view without them) }

  // Asynchronous capability calls: the host settles them later through __island_dispatch('settle', id, result).
  const pending = new Map();
  let nextCall = 1;
  const call = (op, payload) => new Promise((resolve, reject) => {
    const id = nextCall++;
    pending.set(id, { resolve, reject });
    try {
      natives.call(id, op, JSON.stringify(payload ?? {}));
    } catch (e) {
      pending.delete(id);
      reject(e);
    }
  });

  const settingsListeners = new Set();
  const provided = new Map(); // "interface/id" → implementation object
  const providerListeners = new Set();
  const clipboardListeners = new Set();
  const dropListeners = new Set();
  const mediaListeners = new Set();
  const notificationListeners = new Set();
  const badgeListeners = new Set();

  const internal = Object.freeze({
    onProvidersChanged(fn) {
      providerListeners.add(fn);
      return () => providerListeners.delete(fn);
    },
    provide(iface, id, impl) {
      provided.set(`${iface}/${id}`, impl);
    },
    onMedia(fn) {
      mediaListeners.add(fn);
      return () => mediaListeners.delete(fn);
    },
    onNotification(fn) {
      notificationListeners.add(fn);
      return () => notificationListeners.delete(fn);
    },
    onBadge(fn) {
      badgeListeners.add(fn);
      return () => badgeListeners.delete(fn);
    },
    onDrop(fn) {
      dropListeners.add(fn);
      return () => dropListeners.delete(fn);
    },
    onClipboardChanged(fn) {
      clipboardListeners.add(fn);
      return () => clipboardListeners.delete(fn);
    },
    onSettingChanged(fn) {
      settingsListeners.add(fn);
      return () => settingsListeners.delete(fn);
    },
    // `shape` is the view's JSON with every handler left out. A re-render that looks exactly like the last one keeps
    // its ids (now running the new functions): the view is unchanged, so an id still means the same visible control,
    // and the JSON comes out identical, which the host then skips. Any other render gets fresh ids, so a click on
    // a view that has since changed does nothing.
    registerHandlers(activityId, fns, shape) {
      const old = handlerSets.get(activityId);
      if (old && shape !== undefined && old.shape === shape && old.ids.length === fns.length) {
        old.ids.forEach((id, i) => handlers.set(id, fns[i]));
        return old.ids;
      }
      if (old) for (const id of old.ids) handlers.delete(id);
      const ids = fns.map((fn) => {
        const id = nextHandler++;
        handlers.set(id, fn);
        return id;
      });
      handlerSets.set(activityId, { ids, shape });
      return ids;
    },
    dropHandlers(activityId) {
      const old = handlerSets.get(activityId);
      if (old) for (const id of old.ids) handlers.delete(id);
      handlerSets.delete(activityId);
    },
  });

  globalThis.__island_handoff = () => {
    delete globalThis.__island_handoff;
    return Object.freeze({ natives, internal, permissions, call });
  };

  globalThis.__island_dispatch = (kind, id, payload) => {
    if (kind === 'timer') {
      const t = timers.get(id);
      if (!t) return;
      if (!t.repeat) timers.delete(id);
      t.fn(...t.args);
    } else if (kind === 'providers') {
      for (const fn of [...providerListeners]) fn();
    } else if (kind === 'provide') {
      // Another plugin calls one of ours: run it (it may be async) and reply with JSON.
      const req = JSON.parse(payload);
      const impl = provided.get(`${req.interface}/${req.id}`);
      Promise.resolve()
        .then(() => {
          if (!impl || typeof impl[req.method] !== 'function') {
            throw new Error(`${req.interface}/${req.id} has no method '${req.method}'`);
          }
          return impl[req.method](...req.args);
        })
        .then((v) => natives.reply(id, true, JSON.stringify(v ?? null)),
              (e) => natives.reply(id, false, String(e?.message ?? e)));
    } else if (kind === 'media') {
      const now = JSON.parse(payload);
      for (const fn of [...mediaListeners]) fn(now);
    } else if (kind === 'notify') {
      const n = JSON.parse(payload);
      for (const fn of [...notificationListeners]) fn(n);
    } else if (kind === 'badge') {
      const b = JSON.parse(payload);
      for (const fn of [...badgeListeners]) fn(b.app, b.count);
    } else if (kind === 'drop') {
      const paths = JSON.parse(payload);
      for (const fn of [...dropListeners]) fn(paths);
    } else if (kind === 'clipboard') {
      const entry = JSON.parse(payload);
      for (const fn of [...clipboardListeners]) fn(entry);
    } else if (kind === 'settings') {
      const key = JSON.parse(payload);
      for (const fn of [...settingsListeners]) fn(key);
    } else if (kind === 'settle') {
      const p = pending.get(id);
      if (!p) return;
      pending.delete(id);
      const result = JSON.parse(payload);
      if (result.ok) p.resolve(result.value);
      else p.reject(new Error(result.error));
    } else if (kind === 'event') {
      const h = handlers.get(id);
      if (h) h(payload === undefined || payload === '' ? undefined : JSON.parse(payload));
    }
  };
})();
