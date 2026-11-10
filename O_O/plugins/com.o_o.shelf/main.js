// First-party file shelf (docs/design/05-builtin-plugins.md §7, "文件列"): drop files on the island to park them,
// drag them out to any app later. It keeps references to the original paths (nothing is copied or deleted); a file
// that was moved or deleted is shown as missing.
import { island, ui, files, settings, storage } from '@island/api';

const PER_PAGE = 8;   // two rows of cards on the island; the side drawer wraps them
const CARD = 104;     // card width (pt)
const IMAGES = /\.(png|jpe?g|gif|webp|bmp)$/i;
const DAY = 24 * 60 * 60 * 1000;

const size = (n) => n == null ? '' : n < 1024 ? `${n} B` : n < 1024 ** 2 ? `${(n / 1024).toFixed(0)} KB`
  : n < 1024 ** 3 ? `${(n / 1024 ** 2).toFixed(1)} MB` : `${(n / 1024 ** 3).toFixed(1)} GB`;
const ICONS = [
  [/\.(png|jpe?g|gif|webp|heic|svg|bmp|tiff?)$/i, 'FileImage'],
  [/\.(mp4|mov|mkv|avi|webm)$/i, 'FileVideo'],
  [/\.(mp3|wav|flac|m4a|aac|ogg)$/i, 'FileAudio'],
  [/\.(zip|rar|7z|tar|gz|bz2|xz)$/i, 'FileArchive'],
  [/\.(js|ts|cs|py|java|go|rs|c|cpp|h|json|xml|ya?ml|sh)$/i, 'FileCode'],
  [/\.(pdf|docx?|pages|txt|md|rtf|xlsx?|numbers|pptx?|key|csv)$/i, 'FileText'],
];
const icon = (f) => f.dir ? 'Folder' : (ICONS.find(([re]) => re.test(f.name))?.[1] ?? 'File');

export function activate() {
  let shelf = storage.get('shelf') ?? [];   // newest first: { path, added }
  let facts = new Map();                     // path → { name, exists, dir, size }
  let page = 0;
  let notice = null;

  const save = () => storage.set('shelf', shelf);

  const expire = () => {
    const cutoff = Date.now() - settings.get('keepDays') * DAY;
    shelf = shelf.filter((f) => new Date(f.added).getTime() >= cutoff);
  };

  const refresh = async () => {
    expire();
    save();
    facts = new Map();
    if (shelf.length > 0) {
      try {
        for (const f of await files.info(shelf.map((s) => s.path))) facts.set(f.path, f);
      } catch (e) {
        notice = e.message;
      }
    }
    render();
  };

  const add = (paths) => {
    const now = new Date().toISOString();
    for (const path of paths) {
      shelf = shelf.filter((s) => s.path !== path);
      shelf.unshift({ path, added: now });
    }
    page = 0;
    refresh();
  };

  const remove = (path) => { shelf = shelf.filter((s) => s.path !== path); save(); render(); };
  const clear = () => { shelf = []; save(); render(); };
  const act = (fn, path) => fn(path).catch((e) => { notice = e.message; render(); });

  // One file as a card: a thumbnail for pictures, else the file type's icon; the name on up to two lines and its size.
  // Click opens it, dragging carries it to another app; the corner glyphs show it in its folder or take it off.
  const card = (s) => {
    const f = facts.get(s.path) ?? { name: s.path.split(/[\\/]/).pop(), exists: true };
    const preview = f.exists && IMAGES.test(f.name)
      ? ui.image({ path: s.path, width: 56, height: 56 })
      : ui.icon(f.exists ? icon(f) : 'FileX', { size: 40, tone: f.exists ? undefined : 'secondary' });
    return ui.card({ key: s.path, width: CARD, gap: 'xs', align: 'center', dragFiles: f.exists ? [s.path] : undefined,
                     onClick: () => f.exists && act(files.open, s.path) }, [
      ui.row({ gap: 'xs', align: 'center', width: CARD - 20 }, [
        f.exists ? ui.iconButton('FolderSearch', { size: 'small', label: '在文件夹中显示', onClick: () => act(files.reveal, s.path) }) : null,
        ui.spacer(),
        ui.iconButton('X', { size: 'small', label: '移出暂存', onClick: () => remove(s.path) }),
      ]),
      preview,
      ui.text(f.name, { size: 'xs', lines: 2, align: 'center', width: CARD - 20, tone: f.exists ? undefined : 'secondary' }),
      ui.text(f.exists ? size(f.size) || (f.dir ? '文件夹' : '') : '已移动或删除', { size: 'xs', tone: 'secondary' }),
    ]);
  };

  function render() {
    const pages = Math.max(1, Math.ceil(shelf.length / PER_PAGE));
    page = Math.min(page, pages - 1);
    island.page('files').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      shelf.length === 0
        ? ui.stack({ gap: 'xs' }, [
            ui.text('把文件拖到岛上暂存', { weight: 'semibold' }),
            ui.text('之后可以从这里把它们拖进任何应用。只保存位置，不复制也不删除文件。', { tone: 'secondary', size: 'sm', wrap: true }),
          ])
        : ui.wrap({ gap: 'sm' }, shelf.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map(card)),
      ui.row({ gap: 'xs', align: 'center' }, [
        pages > 1 ? ui.dots({ count: pages, index: page }) : null,
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
        shelf.length > 0 ? ui.button('全部移出', { variant: 'ghost', onClick: clear }) : null,
      ]),
      notice ? ui.text(notice, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: ui.text(shelf.length === 0 ? '' : `${shelf.length} 个文件`, { tone: 'secondary', size: 'sm' }) });

    if (shelf.length === 0) island.chip('count').remove();
    else island.chip('count').update(ui.row({ gap: 'xs', align: 'center' }, [
      ui.icon('Paperclip', { tone: 'secondary', size: 14 }),
      ui.text(String(shelf.length), { size: 'sm', weight: 'semibold', tabular: true }),
    ]));
  }

  files.onDrop((paths) => {
    notice = null;
    add(paths);
    island.alert({ leading: ui.icon('Paperclip'), trailing: ui.text(`已暂存 ${paths.length} 个`, { size: 'sm', weight: 'semibold' }) }, { seconds: 2 });
  });
  settings.onChange(() => refresh());
  refresh();
  // Files can disappear behind our back; look again now and then.
  setInterval(refresh, 60 * 1000);
}
