// First-party mail notifier (docs/design/05-builtin-plugins.md §9): read-only IMAP for up to three mailboxes set up in
// the console (address + app password; the host reads the password itself), plus Gmail / Outlook accounts signed in
// with Google / Microsoft (OAuth; the host uses the token over XOAUTH2 — this plugin never sees it). Shows the unread
// count as a chip, alerts once per check about new mail, and lists recent messages; a click opens the web mailbox.
import { island, ui, mail, oauth, settings, storage, shell, notify } from '@island/api';

// A system notification as well, when the user wants one (setting "systemNotify"); never fails the caller.
const systemNotify = (title, body) => {
  if (notify && settings.get('systemNotify') !== false) notify.show({ title, body }).catch(() => {});
};


const PER_PAGE = 5;
const MINUTE = 60 * 1000;

// Preset → IMAP server and web mailbox.
const PRESETS = {
  'QQ 邮箱': ['imap.qq.com', 'https://mail.qq.com'],
  '163 邮箱': ['imap.163.com', 'https://mail.163.com'],
  '126 邮箱': ['imap.126.com', 'https://mail.126.com'],
  '腾讯企业邮': ['imap.exmail.qq.com', 'https://exmail.qq.com'],
  '阿里邮箱': ['imap.aliyun.com', 'https://mail.aliyun.com'],
  iCloud: ['imap.mail.me.com', 'https://www.icloud.com/mail'],
  Gmail: ['imap.gmail.com', 'https://mail.google.com'],
  Outlook: ['outlook.office365.com', 'https://outlook.live.com/mail/'],
};
const OAUTH = {
  google: { title: 'Google', clientId: 'googleClientId', secret: 'googleClientSecret', web: 'https://mail.google.com',
            scopes: ['openid', 'email', 'https://mail.google.com/'] },
  microsoft: { title: 'Microsoft', clientId: 'microsoftClientId', web: 'https://outlook.live.com/mail/',
               scopes: ['openid', 'email', 'offline_access', 'https://outlook.office.com/IMAP.AccessAsUser.All'] },
};

const pad = (n) => String(n).padStart(2, '0');
const when = (iso) => {
  if (!iso) return '';
  const d = new Date(iso);
  const now = new Date();
  return d.toDateString() === now.toDateString() ? `${pad(d.getHours())}:${pad(d.getMinutes())}` : `${d.getMonth() + 1}月${d.getDate()}日`;
};

export function activate() {
  let accounts = [];                 // { key, title, spec, web, oauth? }
  const results = new Map();         // key → { unread, total, messages, error }
  const seen = storage.get('seen') ?? {}; // key → highest uid already announced
  let current = storage.get('current') ?? null;
  let page = 0;
  let signingIn = null;
  let notice = null;
  let timer = null;

  const slotAccounts = () => {
    const list = [];
    for (const n of [1, 2, 3]) {
      const user = String(settings.get(`user${n}`) ?? '').trim();
      if (!user) continue;
      const preset = settings.get(`preset${n}`);
      const known = PRESETS[preset];
      list.push({
        key: `slot${n}`,
        title: user,
        web: known?.[1] ?? null,
        spec: known
          ? { host: known[0], user, passwordSetting: `password${n}` }
          : { hostSetting: `host${n}`, port: settings.get(`port${n}`), user, passwordSetting: `password${n}` },
      });
    }
    return list;
  };

  const loadAccounts = async () => {
    const signedIn = (await oauth.accounts()).filter((a) => OAUTH[a.provider]);
    accounts = [
      ...slotAccounts(),
      ...signedIn.map((a) => ({ key: a.handle, title: a.label ?? a.providerName, spec: { oauth: a.handle }, web: OAUTH[a.provider].web, oauth: a })),
    ];
    if (!accounts.some((a) => a.key === current)) current = accounts[0]?.key ?? null;
  };

  const check = async () => {
    clearTimeout(timer);
    try {
      await loadAccounts();
      let fresh = 0;
      for (const account of accounts) {
        try {
          const r = await mail.check(account.spec, { limit: PER_PAGE * 3 });
          results.set(account.key, r);
          const newest = Math.max(0, ...r.messages.map((m) => m.uid));
          if (seen[account.key] === undefined) {
            seen[account.key] = newest; // first look: what is there is not "new"
          } else {
            fresh += r.messages.filter((m) => m.unread && m.uid > seen[account.key]).length;
            seen[account.key] = Math.max(seen[account.key], newest);
          }
        } catch (e) {
          results.set(account.key, { error: e.message });
        }
      }
      storage.set('seen', seen);
      if (fresh > 0) {
        island.alert({ leading: ui.icon('Mail'), trailing: ui.text(`${fresh} 封新邮件`, { size: 'sm', weight: 'semibold' }), seconds: 4 });
        systemNotify(`${fresh} 封新邮件`, '');
      }
    } catch (e) {
      notice = e.message;
    }
    render();
    timer = setTimeout(check, settings.get('every') * MINUTE);
  };

  const signIn = async (provider) => {
    const p = OAUTH[provider];
    notice = null;
    signingIn = provider;
    render();
    try {
      await oauth.signIn(provider, {
        clientId: settings.get(p.clientId),
        clientSecret: p.secret ? (await settings.getSecret(p.secret)) ?? undefined : undefined,
        scopes: p.scopes,
      });
    } catch (e) {
      notice = e.message;
    }
    signingIn = null;
    await check();
  };

  const signOut = async (account) => {
    await oauth.signOut(account.oauth);
    results.delete(account.key);
    delete seen[account.key];
    await check();
  };

  const open = async (web) => {
    if (!web) return;
    try {
      await shell.openUrl(web);
    } catch (e) {
      notice = e.message;
      render();
    }
  };

  const row = (m, web) => ui.item({ key: m.uid, gap: 'sm', align: 'center', onClick: () => open(web) }, [
    ui.text(m.unread ? '●' : '', { tone: 'accent', size: 'xs', width: 10 }),
    ui.text(m.from || '（无发件人）', { weight: m.unread ? 'semibold' : undefined, width: 110 }),
    ui.text(m.subject || '（无主题）', { grow: true, tone: m.unread ? undefined : 'secondary' }),
    ui.text(when(m.date), { tone: 'secondary', size: 'xs', tabular: true }),
  ]);

  const signInRows = () => Object.entries(OAUTH)
    .filter(([id, p]) => String(settings.get(p.clientId) ?? '').trim() && !accounts.some((a) => a.oauth?.provider === id))
    .map(([id, p]) => ui.item({ key: `signin:${id}`, gap: 'sm', align: 'center', onClick: () => signIn(id) }, [
      ui.icon('LogIn', { tone: 'secondary', size: 16 }),
      ui.text(signingIn === id ? `正在浏览器中登录 ${p.title}…` : `用 ${p.title} 登录`, { grow: true }),
    ]));

  function render() {
    const totalUnread = accounts.reduce((sum, a) => sum + (results.get(a.key)?.unread ?? 0), 0);
    if (totalUnread > 0) {
      island.ambient('unread').update(ui.row({ gap: 'xs', align: 'center' }, [
        ui.icon('Mail', { tone: 'secondary', size: 14 }),
        ui.text(totalUnread > 999 ? '999+' : String(totalUnread), { size: 'sm', weight: 'semibold', tabular: true }),
      ]));
    } else {
      island.ambient('unread').remove();
    }

    const account = accounts.find((a) => a.key === current);
    const r = account ? results.get(account.key) : null;
    const messages = r?.messages ?? [];
    const pages = Math.max(1, Math.ceil(messages.length / PER_PAGE));
    page = Math.min(page, pages - 1);

    let body;
    if (accounts.length === 0) {
      body = ui.stack({ gap: 'xs' }, [
        ui.text('还没有邮箱', { weight: 'semibold' }),
        ui.text('在控制台 › 插件 › 邮件 中填写邮箱地址和授权码；Gmail / Outlook 也可以填好客户端 ID 后在这里登录。', { tone: 'secondary', size: 'sm', wrap: true }),
      ]);
    } else if (!r) {
      body = ui.text('正在检查…', { tone: 'secondary' });
    } else if (r.error) {
      body = ui.text(r.error, { tone: 'secondary', size: 'sm', wrap: true });
    } else if (messages.length === 0) {
      body = ui.text('收件箱是空的。', { tone: 'secondary' });
    } else {
      body = ui.stack({ gap: 'xs', align: 'stretch' }, messages.slice(page * PER_PAGE, (page + 1) * PER_PAGE).map((m) => row(m, account.web)));
    }

    island.home.page('inbox').update(ui.stack({ gap: 'sm', align: 'stretch' }, [
      accounts.length > 1
        ? ui.tabs({ value: current, onChange: (v) => { current = v; storage.set('current', v); page = 0; render(); } },
            accounts.map((a) => ui.option(a.key, a.title)))
        : null,
      body,
      ...signInRows(),
      ui.row({ gap: 'xs', align: 'center' }, [
        pages > 1 ? ui.dots({ count: pages, index: page }) : null,
        ui.spacer(),
        pages > 1 ? ui.iconButton('ChevronLeft', { label: '上一页', disabled: page === 0, onClick: () => { page--; render(); } }) : null,
        pages > 1 ? ui.iconButton('ChevronRight', { label: '下一页', disabled: page >= pages - 1, onClick: () => { page++; render(); } }) : null,
        account?.oauth ? ui.button('退出登录', { variant: 'ghost', onClick: () => signOut(account) }) : null,
      ]),
      notice ? ui.text(notice, { tone: 'secondary', size: 'xs', wrap: true }) : null,
    ]), { status: ui.text(r && !r.error ? `${r.unread} 封未读` : '', { tone: 'secondary', size: 'sm' }) });
  }

  settings.onChange(() => check());
  render();
  check(); // not awaited: activate() must return quickly; the first check renders when it is done
}
