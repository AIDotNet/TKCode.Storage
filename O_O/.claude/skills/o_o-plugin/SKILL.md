---
name: o_o-plugin
description: Write, test and package a plugin for O_O (the desktop "Dynamic Island"), producing an installable .zip. Use when the user wants a new O_O plugin, wants to change one, or wants to package / install one.
---

# Writing an O_O plugin

An O_O plugin is a folder with `plugin.json` and an ES module (`main.js`). It runs in its own sandboxed JS engine
(Jaunt) inside O_O and talks to the island only through `@island/api` — island API **2.0**: `plugin.json` must say
`"engines": { "island": "^2.0.0" }` (O_O refuses anything else; the 1.x `island.page`, `island.chip`,
`island.satellite` and `island.activity.create` no longer exist). It cannot touch the file system, the
network or other apps except through capabilities its manifest declares and the user grants.

The authoritative API is `src/Island.Plugins.Host/Runtime/api.js` (every export has a doc comment; when this skill
was installed outside the repo, the same file sits next to this one as `api.js`) — read it before writing code. The
design is docs/design/09-island-v2.md. Good references are the bundled plugins in `plugins/`: `com.o_o.netease`
(a live activity with controls, a home page), `com.o_o.focus` / `com.o_o.todo` (activities, providers, timers,
alerts), `com.o_o.shelf` (files, an ambient item), `com.o_o.im` (alerts and a passive count),
`com.o_o.ai-usage` (ambient items per account), `com.o_o.ai.deepseek` (network + password settings),
`catalog/plugins/com.o_o.calendar` (an aggregator over providers).

## Workflow

1. **Scaffold** — copy `template/` from this skill into a new folder named after the plugin id
   (`com.<you>.<name>`), then fill in `plugin.json` and `main.js`.
2. **Design** — decide what goes where on the island (below). Keep the collapsed island quiet.
3. **Write** — `main.js` exports `activate()` (may be async). Show things with `island.activity(...)`,
   `island.ambient(id).update(view)`, `island.home.page(id).update(view)` and `island.alert(...)`; react to UI events
   through the functions you put in props.
4. **Try it** — copy the folder into O_O's user plugin folder and restart O_O, or install the packed zip (step 6):
   - macOS: `~/Library/Application Support/O_O/plugins/<id>/`
   - Windows: `%APPDATA%\O_O\plugins\<id>\`  ·  Linux: `~/.config/O_O/plugins/<id>/`
   Run O_O with `O_O_DEBUG=1` to see `console.*` output and plugin errors in the terminal. User plugins wait for
   the user's permission decision (console → 插件 → the plugin's card) before they run.
5. **Test** — if you work inside the O_O repo, add an end-to-end test like
   `tests/Island.Plugins.Host.Tests/ShelfTests.cs` (real engine, fake services) and run `dotnet test`.
6. **Package** — `O_O --pack-plugin <folder> [out.zip]` (from a build: `dotnet run --project src/Island.App --
   --pack-plugin <folder>`). It validates exactly like installing (id, manifest, permissions, settings schema, entry
   file) and writes `<id>-<version>.zip`, leaving out `.git`, `node_modules` and editor clutter. Fix every reported
   problem; never hand-zip a plugin that fails validation.
7. **Install** — console → 插件 → 添加插件 → 选择 .zip 插件包. Installing the same id again updates it in place.

## plugin.json

```jsonc
{
  "id": "com.example.weather",          // lower-case reverse domain; letters, digits, . - _
  "name": "天气",
  "version": "1.0.0",
  "description": "当前天气和今天的预报",    // one sentence for the console card
  "icon": "CloudSun",                   // a Lucide icon name (lucide.dev) for the console card; "logo:claude" / "logo:codex" for those vendors' marks
  "engines": { "island": "^2.0.0" },    // required
  "main": "main.js",
  "permissions": ["network:api.example.com"],
  "contributes": {
    "home": [{ "id": "now", "title": "天气", "icon": "CloudSun" }],       // pages of the home dashboard
    "ambient": [{ "id": "temp", "title": "当前温度", "side": "right" }],   // beside the idle island; "multiple": true for one per account
    "activities": [{ "id": "rain", "title": "降雨提醒", "priority": "high", "accent": "warning" }],
    "providers": [{ "interface": "calendar", "id": "mycal", "title": "My calendar" }],
    "settings": {
      "properties": {
        "city": { "type": "string", "title": "城市", "default": "上海" },
        "every": { "type": "integer", "title": "刷新间隔（分钟）", "minimum": 5, "maximum": 120, "default": 15 },
        "apiKey": { "type": "string", "format": "password", "title": "API Key" }   // stored in the OS keychain
      }
    }
  }
}
```

Setting types: `string` (optionally `enum`; `format`: `password` (keychain), `url`, `color`, `date`, `time`,
`hotkey`; `"x-multiline": true` for a multi-line box), `integer` / `number` (`minimum`, `maximum`), `boolean`. The console renders the form; the plugin reads values with `settings.get(key)` /
`await settings.getSecret(key)` and listens with `settings.onChange(fn)`. Plugins cannot write settings.

## Permissions (declare the least you need)

| Permission | Gives |
| --- | --- |
| `network:<host>` / `network:*.<domain>` | `http.fetch` to that host (https only) |
| `network:localhost` | http(s) to this machine |
| `network:@<setting>` | only the site the user typed into that string setting (self-hosted services) |
| `secrets` | `secrets.get/set/delete` in the OS keychain |
| `oauth:google` / `oauth:feishu` / `oauth:dingtalk` / `oauth:microsoft` | `oauth.signIn(...)`; pass the account as `auth` to `http.fetch` — the host adds the token for that provider's API hosts only (Microsoft: mail only) |
| `mail` | read-only IMAP: `mail.check / status / recent(account)`. Account `{ host | hostSetting, port?, user, passwordSetting }` (a known provider's server or one the user typed; the host reads the password setting itself) or `{ oauth: handle }` (Google / Microsoft, XOAUTH2 on the provider's own server) |
| `clipboard.read` / `.write` / `.watch` / `.paste` | the clipboard (`clipboard.*`); `.paste` pastes into the app the user was in |
| `files.shelf` | files dropped on the island (`files.onDrop`), `files.info/reveal/open`, `ui.item({ dragFiles })` |
| `media` | now playing + play/pause/next (`media.*`) |
| `shell.openUrl` | `shell.openUrl(url)`: open https/http/mailto/meeting-app links — only within a few seconds after the user clicked something in the plugin |
| `localAuth:claude-code` / `localAuth:codex-cli` | read-only use of those apps' local logins (always asks the user) |

Unknown permissions make the manifest invalid. Everything else (activities, ambient items, home pages, alerts,
storage, settings, timers, providers) needs no permission.

## The island: what goes where (docs/design/09-island-v2.md)

The island is like Apple's Dynamic Island: it shows **what is happening now** and is quiet otherwise. You fill fixed
regions; the host decides where things show, lays them out, colours them with the user's theme and animates them.

- **Activity** — something under way that ends: playing, a timer, a meeting about to start, a limit nearly reached.
  Declare it in `contributes.activities` (`priority`: passive | normal | high | critical; `accent`: music, timer,
  calendar, message, reminder, warning or #RRGGBB; `multiple` for one per account/timer), then:
  ```js
  const a = island.activity('playing', {
    priority: 'normal',                       // optional; overrides the declared one
    open: { app: 'com.netease.163music' },    // ⌘-click on the island opens it (or { url })
    compact:  { leading: ui.icon('Music2', { tone: 'accent', size: 14 }), trailing: ui.spectrum({ bars: 5 }) },
    minimal:  ui.icon('Music2', { tone: 'accent', size: 14 }),   // as the bubble beside the island
    expanded: { leading, center, trailing, bottom },              // the card opened with a click
  }, { instance });                                               // only for activities declared multiple
  a.update({ compact: { ... } });   // what you give replaces that part; the rest stays
  a.alert({ leading, center, trailing, seconds });                // opens the island briefly for this activity
  a.end();
  ```
  At most two activities show (the user orders them and can switch kinds to 只提醒 / 关闭); a plugin may hold two.
  Compact regions are one short line each; put controls and detail in `expanded` (`bottom` for buttons and progress).
  Use `tone: 'accent'` for what should take the activity's colour.
- **Ambient item** (`contributes.ambient` + `island.ambient(id[, instance]).update(view[, { title }])`, `.remove()`):
  a small reading beside the idle island (temperature, an unread count) — an icon and a few characters. It makes way
  for activities. The user chooses which show and on which side.
- **Home page** (`contributes.home` + `island.home.page(id).update(view[, { status }])`): the full view on the home
  dashboard (a click on the idle island, or the hotkey), one tab per page; `status` is a short node for the header.
- **Alert** (`island.alert({ leading, center, trailing, seconds, priority })`, 1–10 s): a brief notice ("已复制"); a
  plugin's alerts replace each other; 30 a minute at most (`false` when over).

Constant information is an ambient item plus a home page, not an activity; an activity that never ends keeps the
island busy and pushes the user's other activities aside.

## Themes (`contributes.themes`)

A theme pack is a plugin whose `main.js` is empty. Besides `accent`, `appearance`, `corners` and `tokens`, a theme may
set the island itself (all optional; checked when the plugin loads):

```jsonc
"island": {
  "surface": { "material": "solid | gradient | translucent | animated", "color": "#101018",
               "colors": ["#140C29", "#2A1050"], "angle": 135, "opacity": 0.85,
               "animation": "aurora | pulse | waves", "speed": 0.6 },   // drawn by O_O; never around a real notch
  "shape":   { "radius": 1.15 },                                        // 0.5–1.5
  "content": { "accents": { "music": "#C77DFF", "timer": "#FFB86B" } },
  "motion":  { "spring": "calm | default | snappy | bouncy", "split": "slide | droplet" },
  "mascot":  { "kind": "classic | pixel | ghost | cube", "color": "#9D7BFF" },
  "sounds":  { "alert": "sounds/chime.wav" },                           // ≤ 1 MB, in the plugin folder; plays only if the user allows
  "layout":  { "expanded": "standard | stacked | media" }
}
```

## Views (`ui.*`)

Nodes are plain data: `ui.stack({ gap, align }, [...])`, `ui.row(...)`, `ui.item({ onClick, dragFiles }, [...])`
(a whole row as a button), `ui.card({ width, onClick, dragFiles }, [...])` (a tile for things the user picks
up, e.g. files), `ui.wrap({ gap }, [...])` (cards that wrap onto new lines), `ui.image({ path, width, height })` (a local
picture's thumbnail), `ui.spacer()`, `ui.text(text, { size, weight, tone, tabular, wrap, lines, align, grow })`,
`ui.icon(name, { size, tone })`, `ui.button(label, { variant, onClick, disabled })`, `ui.iconButton(icon, { label,
onClick })`, `ui.tabs({ value, onChange }, ui.option(...)…)`, `ui.picker(...)`, `ui.toggle`, `ui.progress`,
`ui.gauge`, `ui.counter`, `ui.dots({ count, index })`, `ui.swap({ id, direction }, [child])`, `ui.badge`,
`ui.mascot`, `ui.logo`. Give list rows a `key`. Functions in props are event handlers; they are re-registered on
every render, so always render again after state changes. Props must be plain values (strings, numbers, booleans).

Island design rules (docs/design/02-island-shell.md §7) — the island is black and is the only container:
- No boxes or bordered panels; use text tabs (`ui.tabs`), bare glyph buttons (`ui.iconButton`), round dots. The one
  exception is `ui.card`: a softly filled tile for objects the user picks up (files, images).
- Paginate long lists (‹ › with `ui.iconButton` + `ui.dots`), ~5 rows per page; there is no scrolling.
- Chinese UI text, short. `tone: 'secondary'` for supporting text; `tabular: true` for changing numbers.
- There is no text input on the island (it would steal focus from the user's app): take input through settings.

## Rules of the sandbox

- Callbacks must return within 50 ms and `activate()` within 2 s; do slow work in async functions awaiting
  `http.fetch` etc. A plugin that keeps failing is restarted, then disabled.
- `storage` is a JSON key-value store (5 MB). Timers: `setTimeout` / `setInterval` (≥ 16 ms).
- No `fetch`, `require`, DOM, `XMLHttpRequest` or Node APIs. Plugins cannot import from each other; share code by
  copying a module into each plugin, or offer it as a provider (`island.provide` / `island.providers.call`).
- Never put secrets in storage or in views; use password settings or `secrets`.

## Checklist before packaging

- [ ] `id`, `name`, `version`, `description`, `icon` filled in; only needed permissions.
- [ ] `"engines": { "island": "^2.0.0" }`.
- [ ] Every activity, ambient item and home page used in code is declared in `contributes`; `multiple` matches how
      it is called (with or without an instance).
- [ ] Activities end when the thing ends; constant readings are ambient items, not activities.
- [ ] Empty, loading and error states render something helpful (in Chinese).
- [ ] The island stays quiet: ambient items only when there is something to show; alerts sparingly.
- [ ] `O_O --pack-plugin <folder>` reports no problems; the zip installs and runs after granting permissions.
