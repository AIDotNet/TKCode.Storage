// GitHub: unread notifications (GET /notifications) and pull requests waiting for my review
// (GET /search/issues?q=is:pr is:open review-requested:@me). A Personal Access Token from the settings; a chip shows
// the unread count, the page lists both. Clicking a row opens it in the browser.
import { island, ui, http, settings, storage, shell } from '@island/api';

const API = 'https://api.github.com';
const PER_PAGE = 5;
const cut = (s, n) => ([...s].length > n ? `${[...s].slice(0, n).join('')}…` : s);

// Notification subject.url is an API URL; the page the user wants is the html one.
const webUrl = (n) => {
  const u = n.subject?.url ?? '';
  const m = /repos\/([^/]+\/[^/]+)\/(pulls|issues)\/(\d+)/.exec(u);
  if (m) return `https://github.com/${m[1]}/${m[2] === 'pulls' ? 'pull' : 'issues'}/${m[3]}`;
  return n.repository?.html_url ?? 'https://github.com/notifications';
};
const ICONS = { PullRequest: 'GitPullRequest', Issue: 'CircleDot', Release: 'Tag', Discussion: 'MessageSquare' };

export async function activate() {
  const cached = storage.get('cache') ?? { notes: [], reviews: [], at: null };
  let { notes, reviews } = cached;
  let at = cached.at;
  let error = null;
  let tab = 'notes';
  let page = 0;
  let timer = null;
  let known = new Set(storage.get('seen') ?? []);

  const get = async (path, token) => {
    const res = await http.fetch(`${API}${path}`, {
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' },
    });
    if (res.status === 401) throw new Error('GitHub Token 无效或已过期，请在控制台重新填写');
    if (res.status === 403 || res.status === 429) throw new Error('GitHub 请求过于频繁，稍后会自动重试');
    if (!res.ok) throw new Error(`GitHub 返回 ${res.status}`);
    return res.json();
  };

  const refresh = async () => {
    const token = await settings.getSecret('token');
    if (!token) { error = '先在控制台 › 插件 › GitHub 中填写 Personal Access Token'; render(); return; }
    try {
      const [n, r] = await Promise.all([
        get('/notifications?per_page=30', token),
        get('/search/issues?q=' + encodeURIComponent('is:pr is:open review-requested:@me archived:false') + '&per_page=20', token),
      ]);
      notes = n.map((x) => ({ id: x.id, title: x.subject?.title ?? '', repo: x.repository?.full_name ?? '', type: x.subject?.type, reason: x.reason, url: webUrl(x) }));
      reviews = (r.items ?? []).map((x) => ({ id: String(x.id), title: x.title, repo: x.repository_url?.split('/').slice(-2).join('/') ?? '', url: x.html_url }));
      at = new Date().toISOString();
      storage.set('cache', { notes, reviews, at });
      error = null;
      // One alert when a review request that was not there before shows up.
      const fresh = reviews.filter((x) => !known.has(x.id));
      if (fresh.length > 0) {
        if (known.size > 0) island.alert({ leading: ui.icon('GitPullRequest'), trailing: ui.text(`待 review · ${cut(fresh[0].title, 10)}`, { size: 'sm', weight: 'semibold' }) }, { seconds: 5 });
        known = new Set(reviews.map((x) => x.id));
        storage.set('seen', [...known]);
      }
    } catch (e) {
      error = e.message;
    }
    render();
  };

  const open = async (url) => { try { await shell.openUrl(url); } catch (e) { error = e.message; render(); } };

  const render = () => {
    const unread = notes.length;
    if (unread > 0 || reviews.length > 0) {
      island.chip('unread').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.icon(reviews.length > 0 ? 'GitPullRequest' : 'Bell', { tone: 'secondary', size: 14 }),
        ui.text(String(unread + reviews.length), { size: 'sm', weight: 'semibold', tabular: true }),
      ]));
    } else {
      island.chip('unread').remove();
    }

    const list = tab === 'notes' ? notes : reviews;
    const pages = Math.max(1, Math.ceil(list.length / PER_PAGE));
    page = Math.min(page, pages - 1);
    const rows = list.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map((x) => ui.item({ key: x.id, gap: 'sm', align: 'center', onClick: () => open(x.url) }, [
      ui.icon(tab === 'notes' ? (ICONS[x.type] ?? 'Bell') : 'GitPullRequest', { tone: 'secondary', size: 16 }),
      ui.text(x.title, { grow: true, weight: 'medium' }),
      ui.text(cut(x.repo, 22), { size: 'xs', tone: 'secondary' }),
    ]));

    island.page('inbox').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      ui.row({ gap: 'xs', align: 'center' }, [
        ui.tabs({ value: tab, onChange: (v) => { tab = v; page = 0; render(); } },
          [ui.option('notes', `通知 ${notes.length}`), ui.option('reviews', `待 review ${reviews.length}`)]),
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
      ]),
      rows.length > 0 ? ui.stack({ gap: 'xs', align: 'stretch' }, rows)
        : ui.text(error ? '' : at ? (tab === 'notes' ? '没有未读通知。' : '没有等你 review 的 PR。') : '正在读取…', { tone: 'secondary' }),
      pages > 1 ? ui.dots({ count: pages, index: page }) : null,
      error ? ui.text(error, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: ui.text(at ? `更新于 ${new Date(at).toTimeString().slice(0, 5)}` : '', { tone: 'secondary', size: 'sm' }) });
  };

  const schedule = () => {
    clearInterval(timer);
    timer = setInterval(refresh, Math.max(1, Number(settings.get('every') ?? 5)) * 60 * 1000);
  };
  settings.onChange(() => { schedule(); refresh(); });
  render();
  schedule();
  await refresh();
}
