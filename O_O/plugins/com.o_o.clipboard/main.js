// First-party clipboard history (docs/design/05-builtin-plugins.md §8). Records what the user copies (the host never
// reports content password managers mark as secret), keeps the newest `max` entries plus pinned ones, and pastes an
// entry back into the app the user was working in when it is clicked.
import { island, ui, clipboard, settings, storage } from '@island/api';

const PER_PAGE = 5;
const FILTERS = [['all', '全部'], ['text', '文本'], ['url', '链接'], ['image', '图片'], ['files', '文件']];
const ICONS = { text: 'Type', url: 'Link', files: 'File' };

const same = (a, b) => a.kind === b.kind && a.text === b.text && a.image === b.image && (a.files ?? []).join('\n') === (b.files ?? []).join('\n');
const name = (path) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

// One line, whatever the entry: newlines shown as ⏎, files by name.
const preview = (e) => {
  if (e.kind === 'image') return e.width ? `图片 ${e.width}×${e.height}` : '图片';
  if (e.kind === 'files') {
    return e.files.length === 1 ? name(e.files[0]) : `${name(e.files[0])} 等 ${e.files.length} 个文件`;
  }
  const t = e.text.replace(/\s*\n\s*/g, ' ⏎ ').trim();
  return t.length > 200 ? `${t.slice(0, 200)}…` : t;
};

const ago = (iso) => {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  return s < 60 ? '刚刚' : s < 3600 ? `${Math.floor(s / 60)} 分钟前` : s < 86400 ? `${Math.floor(s / 3600)} 小时前` : `${Math.floor(s / 86400)} 天前`;
};

export function activate() {
  let items = storage.get('items') ?? [];   // newest first: { id, kind, text?, files?, app?, at, pinned? }
  let nextId = items.reduce((m, e) => Math.max(m, e.id), 0) + 1;
  let filter = 'all';
  let page = 0;
  let notice = null;                          // a line under the list, e.g. why it could not paste

  const save = () => storage.set('items', items);

  // The newest `max` unpinned entries stay; pinned ones always do.
  const trim = () => {
    const max = settings.get('max');
    let unpinned = 0;
    items = items.filter((e) => e.pinned || ++unpinned <= max);
  };

  const record = (entry) => {
    const existing = items.find((e) => same(e, entry));
    items = items.filter((e) => e !== existing);
    items.unshift({ ...(existing ?? { id: nextId++ }), ...entry, pinned: existing?.pinned ?? false });
    trim();
    save();
    page = 0;
    render();
  };

  const content = (e) => (e.kind === 'files' ? { files: e.files } : e.kind === 'image' ? { image: e.image } : { text: e.text });

  const use = async (e) => {
    notice = null;
    try {
      if (settings.get('directPaste')) {
        const r = await clipboard.paste(content(e));
        if (!r.pasted) {
          notice = r.reason ?? '没有粘贴，已复制到剪贴板。';
          island.alert({ leading: ui.icon('Copy'), trailing: ui.text('已复制', { size: 'sm', weight: 'semibold' }) });
        }
      } else {
        await clipboard.write(content(e));
        island.alert({ leading: ui.icon('Copy'), trailing: ui.text('已复制', { size: 'sm', weight: 'semibold' }) });
      }
    } catch (err) {
      notice = err.message;
    }
    render();
  };

  const togglePin = (e) => { e.pinned = !e.pinned; trim(); save(); render(); };
  const remove = (e) => { items = items.filter((x) => x !== e); save(); render(); };

  const row = (e) => ui.item({ key: e.id, gap: 'sm', align: 'center', onClick: () => use(e) }, [
    e.kind === 'image' ? ui.image({ path: e.image, width: 32, height: 22 }) : ui.icon(ICONS[e.kind] ?? 'Type', { tone: 'secondary', size: 16 }),
    ui.text(preview(e), { grow: true }),
    ui.text(e.app ? `${e.app} · ${ago(e.at)}` : ago(e.at), { tone: 'secondary', size: 'xs' }),
    ui.iconButton(e.pinned ? 'PinOff' : 'Pin', { label: e.pinned ? '取消置顶' : '置顶', onClick: () => togglePin(e) }),
    ui.iconButton('X', { label: '删除', onClick: () => remove(e) }),
  ]);

  function render() {
    const shown = items
      .filter((e) => filter === 'all' || e.kind === filter)
      .sort((a, b) => (b.pinned ? 1 : 0) - (a.pinned ? 1 : 0)); // pinned first, otherwise newest first
    const pages = Math.max(1, Math.ceil(shown.length / PER_PAGE));
    page = Math.min(page, pages - 1);
    const slice = shown.slice(page * PER_PAGE, (page + 1) * PER_PAGE);

    island.page('history').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.row({ gap: 'xs', align: 'center' }, [
        ui.tabs({ value: filter, onChange: (v) => { filter = v; page = 0; render(); } },
          FILTERS.map(([v, l]) => ui.option(v, l))),
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
      ]),
      slice.length === 0
        ? ui.text(items.length === 0 ? '复制的内容会出现在这里。' : '没有这一类的内容。', { tone: 'secondary' })
        : ui.stack({ gap: 'xs', align: 'stretch' }, slice.map(row)),
      pages > 1 ? ui.dots({ count: pages, index: page }) : null,
      notice ? ui.text(notice, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: ui.text(items.length === 0 ? '' : `${items.length} 条`, { tone: 'secondary', size: 'sm' }) });
  }

  clipboard.onChange(record);
  settings.onChange((key) => { if (key === 'max') { trim(); save(); } render(); });
  render();
  // Keep the "n 分钟前" labels fresh.
  setInterval(render, 60 * 1000);
}
