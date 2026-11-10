// The `@island/api` module (docs/design/03-plugin-system.md §7). The host loads it before any plugin code; it takes
// the natives from the prelude's one-shot handoff, and exports a capability only when the plugin holds its permission
// (otherwise the export is undefined). The host checks permissions again on every call.
const { natives, internal, permissions, call } = globalThis.__island_handoff();
const host = { activity: natives.activity };
const has = (p) => permissions.includes(p);

const PRIORITIES = ['ambient', 'passive', 'active', 'critical'];

// ---- views -----------------------------------------------------------------------------------------------------
// A node is { t, k?, p, c }. Function-valued props become handler references on the way to the host.
const node = (t, props, children) => {
  const p = { ...(props ?? {}) };
  const k = p.key;
  delete p.key;
  const n = { t, p, c: (children ?? []).filter((x) => x != null && x !== false) };
  if (k !== undefined) n.k = String(k);
  return n;
};

export const ui = Object.freeze({
  /** Vertical stack. props: gap ('xs'|'sm'|'md'|'lg'), align ('start'|'center'|'end'|'stretch'), width (px; any node),
   *  grow (any child of a row or stack: takes the remaining space; a text is then cut with "…") */
  stack: (props, children) => node('stack', props, children),
  /** Equal columns, children fill row by row. props: columns (1-6, 2), gap, align; a child may set span (columns it takes) */
  grid: (props, children) => node('grid', props, children),
  /** Horizontal row. props: gap, align */
  row: (props, children) => node('row', props, children),
  /** A row that acts as a button (no box; brightens under the pointer), for list entries. props: gap, align, onClick,
   *  dragFiles ([absolute paths]: dragging the row carries these files to other apps; needs files.shelf) */
  // Props are plain values on the way to the host, so the paths travel as one newline-separated string.
  item: (props, children) => node('item', Array.isArray(props?.dragFiles) ? { ...props, dragFiles: props.dragFiles.map(String).join('\n') } : props, children),
  /** A tile the user can click or drag (a softly filled rounded shape — the one filled shape the island allows; use it
   *  for things picked up, such as files). Its children stack vertically. props: width, gap, align, onClick, dragFiles */
  card: (props, children) => node('card', Array.isArray(props?.dragFiles) ? { ...props, dragFiles: props.dragFiles.map(String).join('\n') } : props, children),
  /** A text box. It takes the keyboard only when the user clicks into it (the user's app keeps it otherwise); Esc
   *  gives it back. props: kind ('search' for a search field), placeholder, value (applied while the user is not
   *  typing in it), maxLength, onChange(text) on every edit, onSubmit(text) on Enter. Give it a key. */
  input: (props) => node('input', props),
  /** Children flow left to right and wrap onto the next line when the island is narrow (cards). props: gap */
  wrap: (props, children) => node('wrap', props, children),
  /** A local picture (png, jpg, gif, webp, bmp), cropped to fill. props: path (absolute), width, height (px). Shown to the
   *  user only; the plugin never gets its pixels. Shows nothing when the file is missing. */
  image: (props) => node('image', props),
  /** Takes the remaining space in a row or stack. */
  spacer: () => node('spacer'),
  /** props: size ('xs'|'sm'|'base'|'lg'|'xl'|'2xl'|'3xl'|'4xl'), weight ('regular'|'medium'|'semibold'|'bold'),
   *  tone ('primary'|'secondary'|'accent'), color ('#RRGGBB', overrides tone), tabular (bool), wrap (bool), lines (wrap to at most n lines, then "…"),
   *  align ('center') */
  text: (text, props) => node('text', { ...props, text: String(text) }),
  /** A Lucide icon by name, e.g. 'Clock'. props: size (px), tone */
  icon: (name, props) => node('icon', { ...props, name }),
  /** props: variant ('primary'|'secondary'|'ghost'|'danger'), size ('small'|'medium'|'large'), onClick */
  button: (label, props) => node('button', { ...props, label: String(label) }),
  /** props: checked, label, onChange(checked) */
  toggle: (props) => node('switch', props),
  /** props: value, max, label, showValue */
  progress: (props) => node('progress', props),
  /** A 270° ring (Arc Gauge). props: value, max (100), label, detail, warnAt, dangerAt, size (px, 64-200) */
  gauge: (props) => node('gauge', props),
  /** A number whose changed digits roll (Arc AnimatedCounter). props: value, prefix, suffix, decimals, size, label */
  counter: (props) => node('counter', props),
  /** Boxed tabs (Arc SegmentedControl) — for surfaces outside the island; on the island use ui.tabs (docs 02 §7).
   *  props: value (selected), onChange(value), align; children: ui.option(value, label) */
  segmented: (props, options) => node('segmented', props, options),
  option: (value, label) => node('option', { value: String(value), label: String(label ?? value) }),
  /** Tabs as words (no box): the current one white and semibold. props: value, onChange(value); children: ui.option(...) */
  tabs: (props, options) => node('tabs', props, options),
  /** The current choice as quiet text with a chevron; a menu of the others (Arc DropdownMenu). props: value, onChange(value); children: ui.option(...) */
  picker: (props, options) => node('picker', props, options),
  /** A bare icon that acts as a button (no box, even on hover). props: label (accessible name), onClick, disabled,
   *  size ('small' for the corner of a card) */
  iconButton: (icon, props) => node('iconButton', { ...props, icon }),
  /** Animates a change of its child (Arc content swap). Give it a new `id` when the content should change; the
   *  child is then replaced with Arc's roll transition. props: id, direction (1 forward, -1 back). children: one node */
  swap: (props, children) => node('swap', props, children),
  /** A brand mark the host knows, drawn in the foreground colour. name: 'claude' | 'codex'. props: size (px) */
  logo: (name, props) => node('logo', { ...props, name: String(name) }),
  /** Any Arc control from the host's list (Alert, Avatar, Calendar, Checkbox, Countdown, EmptyState, MetricCard, NumberField,
   *  Pagination, Skeleton, Slider, Switch). props are the control's properties in camelCase (isOpen, selectedDate 'YYYY-MM-DD',
   *  tone 'Success'…); onChange(value) reports the user's change for controls that have one. Unknown names or props are ignored. */
  arc: (type, props) => node('arc', { ...props, type: String(type) }),
  /** A page indicator. props: count, index */
  dots: (props) => node('dots', props),
  /** props: tone ('neutral'|'success'|'info'|'warning'|'danger'), small */
  badge: (text, props) => node('badge', { ...props, text: String(text) }),
  /** The island's mascot (Arc AiMascot). props: status ('idle'|'listening'|'thinking'|'writing'|'done'|'error'),
   *  size (24-160 px; below 56 a compact drawing is used) */
  mascot: (props) => node('mascot', props),
});

// Replaces function props with { $h: id }, registering the functions for this activity.
const serialize = (activityId, view, page) => {
  const fns = [];
  const refs = [];
  const walk = (n) => {
    if (n == null || typeof n !== 'object') return null;
    const p = {};
    for (const [name, value] of Object.entries(n.p ?? {})) {
      if (typeof value === 'function') {
        const ref = { $h: 0 };
        fns.push(value);
        refs.push(ref);
        p[name] = ref;
      } else {
        p[name] = value;
      }
    }
    const out = { t: n.t, p, c: (n.c ?? []).map(walk).filter((x) => x !== null) };
    if (n.k !== undefined) out.k = n.k;
    return out;
  };
  const shaped = {
    compact: {
      leading: walk(view?.compact?.leading),
      trailing: walk(view?.compact?.trailing),
    },
    expanded: walk(view?.expanded),
  };
  if (page) shaped.page = page;
  if (view?.status) shaped.status = walk(view.status);
  const ids = internal.registerHandlers(activityId, fns);
  refs.forEach((ref, i) => { ref.$h = ids[i]; });
  return JSON.stringify(shaped);
};

// ---- activities ------------------------------------------------------------------------------------------------
class Activity {
  #id;
  #title;
  #priority;
  #ended = false;

  constructor(id, options) {
    this.#id = id;
    this.#title = options?.title ?? id;
    this.#priority = PRIORITIES.includes(options?.priority) ? options.priority : 'passive';
    this.#page = options?.page == null ? null : String(options.page);
  }

  #page;

  get id() { return this.#id; }

  /** Changes how strongly the activity claims the island; takes effect with the next update(). */
  setPriority(priority) {
    if (!PRIORITIES.includes(priority)) throw new TypeError(`Unknown priority '${priority}'`);
    this.#priority = priority;
  }

  /** Shows `view` ({ compact: { leading, trailing }, expanded }) on the island. */
  update(view) {
    if (this.#ended) throw new Error(`Activity '${this.#id}' has ended`);
    host.activity('update', this.#id, this.#title, this.#priority, serialize(this.#id, view, this.#page));
  }

  /** Removes the activity from the island. */
  end() {
    if (this.#ended) return;
    this.#ended = true;
    internal.dropHandlers(this.#id);
    host.activity('end', this.#id, this.#title, this.#priority, '');
  }
}

// ---- pages -----------------------------------------------------------------------------------------------------
class Page {
  #id;

  constructor(id) { this.#id = id; }

  get id() { return this.#id; }

  /** Shows `view` (a node) as this page; plugin.json must declare the page under contributes.pages.
   *  options.status: a short node for the island header while this page is on show (e.g. "更新于 2 分钟前").
   *  Without one the header stays empty. */
  update(view, options) {
    const key = `page:${this.#id}`;
    host.activity('page', this.#id, '', '', serialize(key, { expanded: view, status: options?.status }));
  }
}
const pages = new Map();

// ---- chips -----------------------------------------------------------------------------------------------------
class Chip {
  #id;
  #instance;

  constructor(id, instance) { this.#id = id; this.#instance = instance; }

  /** Shows `view` (a small node: an icon and a short text, at most 64 pt wide) on the collapsed island.
   *  options: { title } names this instance in the console (e.g. the account). */
  update(view, options) {
    host.activity('chip', this.#id, this.#instance, options?.title == null ? '' : String(options.title),
      serialize(`chip:${this.#id}#${this.#instance}`, { expanded: view }));
  }

  /** Takes the chip off the island. */
  remove() {
    internal.dropHandlers(`chip:${this.#id}#${this.#instance}`);
    host.activity('chipEnd', this.#id, this.#instance, '', '');
  }
}

export const island = Object.freeze({
  /** The chip `id` declared in plugin.json (contributes.chips); `instance` only for chips declared multiple. */
  chip: (id, instance) => new Chip(String(id), instance == null ? '' : String(instance)),
  /** Takes the collapsed island's place for a few seconds. view: { leading, trailing }; options: { seconds } (1-10, 3). */
  alert: (view, options) => host.activity('alert', '', '', String(Number(options?.seconds ?? 3)),
    serialize('alert', { compact: { leading: view?.leading, trailing: view?.trailing } })),
  /** The page `id` declared in plugin.json (contributes.pages): the plugin's full view in the expanded island. */
  page: (id) => {
    id = String(id);
    if (!pages.has(id)) pages.set(id, new Page(id));
    return pages.get(id);
  },
  /** Offers `impl` (an object of methods) as provider `id` of `iface`; plugin.json must declare it under
   *  contributes.providers. Other plugins call it through island.providers; it runs here, with our permissions. */
  provide: (iface, id, impl) => internal.provide(String(iface), String(id), impl),
  providers: Object.freeze({
    /** Calls fn() when a provider plugin starts, stops or is granted permissions; returns a function to stop. */
    onChange: (fn) => internal.onProvidersChanged(fn),
    /** Promise<[{ id, title, plugin, running }]> */
    list: (iface) => call('providers.list', { interface: String(iface) }),
    /** Calls a provider method; arguments and result must be JSON-serialisable. */
    call: (iface, id, method, ...args) =>
      call('providers.call', { interface: String(iface), id: String(id), method: String(method), args }),
  }),
  activity: Object.freeze({
    /** Declares an activity. options: { title, priority: 'ambient'|'passive'|'active'|'critical', page }.
     *  `page`: the page (declared by this plugin) the island opens on while the activity is active or critical. */
    create: (id, options) => new Activity(String(id), options),
  }),
});

// ---- storage (no permission needed; per plugin, persisted, quota-limited) --------------------------------------
export const storage = Object.freeze({
  /** The stored value, or undefined. */
  get(key) {
    const json = natives.storage('get', String(key), '');
    return json === null ? undefined : JSON.parse(json);
  },
  /** Stores any JSON-serialisable value. */
  set(key, value) {
    if (value === undefined) throw new TypeError('Use storage.delete() to remove a key');
    natives.storage('set', String(key), JSON.stringify(value));
  },
  delete: (key) => natives.storage('delete', String(key), ''),
  keys: () => JSON.parse(natives.storage('keys', '', '')),
});

// ---- http (needs network:<host> permissions) -------------------------------------------------------------------
class Response {
  #raw;
  constructor(raw) { this.#raw = raw; }
  get status() { return this.#raw.status; }
  get statusText() { return this.#raw.statusText; }
  get ok() { return this.#raw.status >= 200 && this.#raw.status < 300; }
  get url() { return this.#raw.url; }
  get headers() { return this.#raw.headers; }
  text() { return this.#raw.body; }
  json() { return JSON.parse(this.#raw.body); }
}

export const http = permissions.some((p) => p.startsWith('network:')) ? Object.freeze({
  /** fetch(url, { method, headers, body, timeoutMs }) → Promise<Response>. A body object is sent as JSON. */
  async fetch(url, init) {
    const headers = { ...(init?.headers ?? {}) };
    let body = init?.body;
    if (body !== undefined && typeof body !== 'string') {
      body = JSON.stringify(body);
      if (!Object.keys(headers).some((h) => h.toLowerCase() === 'content-type')) headers['content-type'] = 'application/json';
    }
    const raw = await call('http.fetch', {
      url: String(url), method: init?.method, headers, body, timeoutMs: init?.timeoutMs, auth: init?.auth?.handle ?? init?.auth,
    });
    return new Response(raw);
  },
  /** GETs (or sends) and parses JSON; rejects on a non-2xx status. */
  async getJson(url, init) {
    const res = await http.fetch(url, init);
    if (!res.ok) throw new Error(`HTTP ${res.status} ${res.statusText} for ${res.url}`);
    return res.json();
  },
}) : undefined;

// ---- secrets (needs the secrets permission; stored in the OS keychain, scoped to this plugin) -----------------
export const secrets = has('secrets') ? Object.freeze({
  /** Promise<string | null> */
  get: (key) => call('secrets.get', { key: String(key) }),
  set: (key, value) => call('secrets.set', { key: String(key), value: String(value) }).then(() => undefined),
  /** Promise<boolean>: whether a secret existed. */
  delete: (key) => call('secrets.delete', { key: String(key) }),
}) : undefined;

// ---- localAuth (needs localAuth:<source>; reuses a local client's login, read-only) ---------------------------
export const localAuth = permissions.some((p) => p.startsWith('localAuth:')) ? Object.freeze({
  /** Accounts signed in on this machine: Promise<[{ handle, label, email, plan, accountId, subscriptionUntil }]>.
   *  Pass an account (or its handle) as `auth` to http.fetch; the host adds the credential for the source's own
   *  hosts only. The plugin never sees the token. */
  /** Claude Code only: plan usage from Claude Code's status line, read-only and without any token:
   *  Promise<{ connected, updatedAt?, fiveHour?: { usedPercent, resetsAt? }, sevenDay?: {…} }> */
  statusLine: (account) => call('localAuth.statusLine', { handle: account?.handle ?? String(account) }),
  accounts: (source, options) => call('localAuth.accounts', {
    source: String(source),
    configDirs: Array.isArray(options?.configDirs) ? options.configDirs.map(String) : undefined,
  }),
}) : undefined;

// ---- clipboard (clipboard.read / .write / .watch / .paste; docs 05 §8) ------------------------------------------
// An entry is { kind: 'text'|'url'|'files', text?, files?: [absolute paths], app?, at }. Content other apps mark as
// secret (password managers) is never reported.
export const clipboard = ['clipboard.read', 'clipboard.write', 'clipboard.watch', 'clipboard.paste'].some(has)
  ? Object.freeze({
      /** Promise<entry|null>: what the clipboard holds now. Needs clipboard.read. */
      read: () => call('clipboard.read', {}),
      /** Puts { text } or { files } on the clipboard. Needs clipboard.write. */
      write: (content) => call('clipboard.write', content),
      /** Puts it on the clipboard and pastes it into the app the user was working in. Needs clipboard.paste.
       *  Promise<{ pasted, reason? }>: when it could not paste (e.g. no accessibility permission), it is still copied. */
      paste: (content) => call('clipboard.paste', content),
      /** Calls fn(entry) whenever the clipboard changes; returns a function that stops. Needs clipboard.watch. */
      onChange: (fn) => {
        if (!has('clipboard.watch')) throw new Error('clipboard.onChange needs the clipboard.watch permission');
        return internal.onClipboardChanged(fn);
      },
    })
  : undefined;

// ---- files (files.shelf; docs 05 §7) ---------------------------------------------------------------------------
export const files = has('files.shelf')
  ? Object.freeze({
      /** Calls fn([absolute paths]) when the user drops files on the island; returns a function that stops. */
      onDrop: (fn) => internal.onDrop(fn),
      /** Promise<[{ path, name, exists, dir, size?, modified? }]> */
      info: (paths) => call('files.info', { paths: [...paths].map(String) }),
      /** Shows the file selected in Finder / Explorer / the file manager. */
      reveal: (path) => call('files.reveal', { path: String(path) }),
      /** Opens the file with its default app. */
      open: (path) => call('files.open', { path: String(path) }),
    })
  : undefined;

// ---- system (system.info) ----------------------------------------------------------------------------------------
// { cpu (%), cores, memTotal, memUsed (bytes), battery (%)?, charging?, netIn, netOut (bytes/s) }. A figure the platform
// cannot tell is missing; cpu and the net rates need two calls (the first has none).
export const system = has('system.info')
  ? Object.freeze({
      /** Promise<the figures above> */
      stats: () => call('system.stats', {}),
    })
  : undefined;

// ---- media (media; docs 05 §1) ----------------------------------------------------------------------------------
// What is playing: { app, title?, artist?, album?, playing, position? (s), duration? (s) } or null. The position is as of
// the moment it was reported; while playing, add the time since.
export const media = has('media')
  ? Object.freeze({
      /** Promise<now playing | null> */
      nowPlaying: () => call('media.nowPlaying', {}),
      /** 'play' | 'pause' | 'toggle' | 'next' | 'previous'. Promise<boolean>: whether it could be sent. */
      command: (command) => call('media.command', { command: String(command) }),
      /** Calls fn(now playing | null) when the track or the play state changes; returns a function that stops. */
      onChange: (fn) => internal.onMedia(fn),
    })
  : undefined;

// ---- oauth (oauth:<provider>; docs 03 §7) --------------------------------------------------------------------
// Sign-in with a provider the host knows (google, feishu, dingtalk) in the system browser. The host keeps the tokens
// (keychain) and refreshes them; the plugin gets an account and passes it as `auth` to http.fetch — the host adds the
// token only for that provider's own API hosts.
export const oauth = permissions.some((p) => p.startsWith('oauth:')) ? Object.freeze({
  /** Opens the browser to sign in. options: { clientId, clientSecret?, scopes: [], label? }.
   *  Promise<{ handle, provider, providerName, label?, created }> once the user finishes (5 minutes at most). */
  signIn: (provider, options) => call('oauth.signIn', {
    provider: String(provider),
    clientId: String(options?.clientId ?? ''),
    clientSecret: options?.clientSecret == null ? undefined : String(options.clientSecret),
    scopes: Array.isArray(options?.scopes) ? options.scopes.map(String) : [],
    label: options?.label == null ? undefined : String(options.label),
  }),
  /** Promise<[account]>: the accounts this plugin signed in. */
  accounts: () => call('oauth.accounts', {}),
  /** Names an account (e.g. the user's e-mail after asking the provider's API who it is). */
  setLabel: (account, label) => call('oauth.setLabel', { handle: account?.handle ?? String(account), label: String(label) }),
  /** Forgets the account and deletes its tokens. */
  signOut: (account) => call('oauth.signOut', { handle: account?.handle ?? String(account) }),
}) : undefined;

// ---- notifications (notifications) -------------------------------------------------------------------------
export const notify = has('notifications') ? Object.freeze({
  /** A system notification (Notification Center, Windows toast…), titled "<plugin name> · <title>". Use it for things
   *  the user would want to know while not looking at the island; at most one per 10 seconds and 20 per hour. */
  show: (options) => call('notify.show', { title: String(options?.title ?? ''), body: String(options?.body ?? '') }),
}) : undefined;

// ---- shell (shell.openUrl) ------------------------------------------------------------------------------------
export const shell = has('shell.openUrl') ? Object.freeze({
  /** Opens a link (https, http, mailto, meeting apps) with the system. Only right after the user clicked something
   *  in this plugin (within a few seconds), so call it from an onClick handler. */
  openUrl: (url) => call('shell.openUrl', { url: String(url) }),
}) : undefined;

// ---- mail (mail; docs 05 §9) -------------------------------------------------------------------------------------
// Read-only IMAP. An account is { host | hostSetting, port?, user | userSetting, passwordSetting } — a known provider's
// server or the one the user typed into a setting, and the password from the plugin's own password setting (the host
// reads it; JS never sees it) — or { oauth: handle } for an account from oauth.signIn (Google, Microsoft), whose
// server is fixed by the provider and whose token goes over XOAUTH2.
const mailAccount = (a) => (a?.oauth ? { oauth: String(a.oauth) } : {
  host: a?.host == null ? undefined : String(a.host),
  hostSetting: a?.hostSetting == null ? undefined : String(a.hostSetting),
  port: a?.port == null ? undefined : Number(a.port),
  user: a?.user == null ? undefined : String(a.user),
  userSetting: a?.userSetting == null ? undefined : String(a.userSetting),
  passwordSetting: a?.passwordSetting == null ? undefined : String(a.passwordSetting),
});
export const mail = has('mail') ? Object.freeze({
  /** Everything in one connection: Promise<{ host, user, unread, total, messages: [{ uid, from, address?, subject,
   *  date?, unread, preview? }] }> (newest first). options: { limit (1-50, 10), unreadOnly }. */
  check: (account, options) => call('mail.check', {
    account: mailAccount(account), limit: options?.limit, unreadOnly: options?.unreadOnly === true, recent: true,
  }),
  /** Promise<{ host, user, unread, total }> */
  status: (account) => call('mail.check', { account: mailAccount(account), recent: false }),
  /** Promise<[message]>, newest first. options: { limit, unreadOnly } */
  recent: async (account, options) => (await call('mail.check', {
    account: mailAccount(account), limit: options?.limit, unreadOnly: options?.unreadOnly === true, recent: true,
  })).messages,
}) : undefined;

// ---- settings (declared in plugin.json contributes.settings; edited by the user in the console) ------------------
export const settings = Object.freeze({
  /** The value, the schema default, or undefined. Password settings: use getSecret. */
  get(key) {
    const json = natives.settings('get', String(key));
    return json === null ? undefined : JSON.parse(json);
  },
  /** Every non-password setting with its value or default. */
  all: () => JSON.parse(natives.settings('all', '')),
  /** A password setting, from the OS credential store: Promise<string | null>. */
  getSecret: (key) => call('settings.secret', { key: String(key) }),
  /** Calls fn(key) when the user changes a setting; returns a function that stops listening. */
  onChange: (fn) => internal.onSettingChanged(fn),
});

/** The permissions this plugin holds: what it declared, minus anything the user did not grant. */
export const granted = permissions;
