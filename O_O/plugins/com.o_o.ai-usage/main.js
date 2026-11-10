// The AI usage aggregator (docs/design/05-builtin-plugins.md §10): asks every "ai-usage" provider plugin for its
// accounts and their usage, and shows ONE provider at a time — tabs and ‹ › arrows switch providers, a second row of
// tabs switches accounts when a provider has several. It holds no credentials and no network permission: providers
// do that work in their own plugins and return data only.
//
// Provider protocol (interface "ai-usage"):
//   accounts() → [{ handle, label, plan? }]
//   usage(account) → {
//     plan?, updatedAt?,
//     windows?: [{ name, usedPercent, resetsAt? }],                       // quota plans (Claude, Codex…)
//     balance?: { amount, currency: 'CNY'|'USD'|…, label?, details?: [{ label, amount }] }   // top-up accounts
//   }
import { island, ui, settings, storage } from '@island/api';

const IFACE = 'ai-usage';
const CURRENCY = { CNY: '¥', USD: '$', EUR: '€' };

const until = (iso) => {
  if (!iso) return '';
  const ms = new Date(iso).getTime() - Date.now();
  if (!(ms > 0)) return '即将重置';
  const h = Math.floor(ms / 3600000);
  const m = Math.round((ms % 3600000) / 60000);
  return h >= 24 ? `${Math.floor(h / 24)} 天后重置` : h > 0 ? `${h} 小时后重置` : `${m} 分钟后重置`;
};

const ago = (iso) => {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  return s < 90 ? '刚刚' : s < 3600 ? ` ${Math.round(s / 60)} 分钟前` : s < 86400 ? ` ${Math.round(s / 3600)} 小时前` : ` ${Math.round(s / 86400)} 天前`;
};

// The client's mark where the host has one; otherwise a neutral wallet (balance providers such as DeepSeek, Kimi).
const LOGOS = ['claude', 'codex'];
const mark = (id, size) => LOGOS.includes(id) ? ui.logo(id, { size }) : ui.icon('Wallet', { size, tone: 'secondary' });

const money = (amount, currency) => `${CURRENCY[currency] ?? ''}${Number(amount).toFixed(2)}`;

export async function activate() {
  // The collapsed island gets chips (docs 07 §4): the busiest quota overall, and — if the user turns them on — one
  // per account. Past the alert threshold an alert shows once and an active activity makes opening the island go
  // straight to the usage page.
  let act = null;
  let alerted = false;
  const accountChips = new Set();
  let providers = [];          // [{ id, title, plugin, running }] in a stable order
  const data = new Map();      // provider id → { accounts: [{ account, usage?, error? }], error? }
  let current = storage.get('provider') ?? null;
  const accountIndex = new Map();
  let timer = null;
  let direction = 1;           // which way the last change moved, so the content rolls the same way

  const select = (id, dir) => {
    const from = providers.findIndex((p) => p.id === current);
    const to = providers.findIndex((p) => p.id === id);
    direction = dir ?? (to >= from ? 1 : -1);
    current = id;
    storage.set('provider', id);
    render();
  };
  const step = (delta) => {
    if (providers.length === 0) return;
    const i = Math.max(0, providers.findIndex((p) => p.id === current));
    select(providers[(i + delta + providers.length) % providers.length].id, delta < 0 ? -1 : 1);
  };

  // Every window gets its own ring, in a fixed order (shortest window first), so neither the 5-hour nor the weekly
  // limit hides behind the other and rings never swap places as usage changes. Only beyond three windows do the
  // rest become thin bars.
  const ORDER = ['5 小时', '每日', '每周', '每周 · Opus', '每月'];
  const rank = (w) => { const i = ORDER.indexOf(w.name); return i < 0 ? ORDER.length : i; };

  const quotaView = (provider, entry) => {
    const windows = [...(entry.usage.windows ?? [])].sort((a, b) => rank(a) - rank(b));
    const rings = windows.slice(0, 3);
    const rest = windows.slice(3);
    const alertAt = settings.get('alertAt');
    return ui.row({ gap: 'md', align: 'center' }, [
      ui.stack({ gap: 'xs', width: 150 }, [
        identity(provider, entry),
        ...rest.map((w) => ui.progress({
          value: Math.round(w.usedPercent), max: 100, showValue: true,
          label: [w.name, until(w.resetsAt)].filter(Boolean).join(' · '),
        })),
        entry.usage.updatedAt ? ui.text(`更新于${ago(entry.usage.updatedAt)}`, { tone: 'secondary', size: 'xs' }) : null,
      ]),
      ...rings.map((w) => ui.gauge({
        value: Math.round(w.usedPercent), max: 100, size: 76,
        label: w.name, detail: until(w.resetsAt),
        warnAt: Math.min(75, alertAt), dangerAt: alertAt,
      })),
    ]);
  };

  const balanceView = (provider, entry) => {
    const b = entry.usage.balance;
    return ui.row({ gap: 'xl', align: 'center' }, [
      ui.stack({ gap: 'xs', width: 260 }, [
        identity(provider, entry),
        ui.text(b.label ?? '可用余额', { tone: 'secondary', size: 'xs' }),
        ui.counter({ value: b.amount, prefix: CURRENCY[b.currency] ?? '', decimals: 2, size: '4xl', label: b.label ?? '可用余额' }),
      ]),
      ui.stack({ gap: 'xs' }, (b.details ?? []).map((d) => ui.row({ gap: 'md' }, [
        ui.text(d.label, { tone: 'secondary', size: 'sm', width: 64 }),
        ui.text(money(d.amount, b.currency), { size: 'sm', tabular: true }),
      ]))),
    ]);
  };

  // Whose numbers these are. The provider is already the selected tab, so: the account, then its plan — words in
  // white and grey, no pills (the island is the only container).
  // With several accounts the picker above already names the account, so only the plan is said here.
  // The client's mark sits to the left of the account, so it is clear whose numbers these are.
  const identity = (provider, entry) => {
    const plan = entry.usage?.plan ?? entry.account.plan;
    const several = (data.get(provider.id)?.accounts.length ?? 0) > 1;
    const text = several
      ? (plan ? ui.text(plan, { weight: 'semibold', size: 'base' }) : null)
      : ui.stack({ gap: 'xs' }, [
          ui.text(entry.account.label || provider.title, { weight: 'semibold', size: 'base' }),
          plan ? ui.text(plan, { tone: 'secondary', size: 'xs' }) : null,
        ]);
    return ui.row({ gap: 'sm', align: 'center' }, [mark(provider.id, 22), text]);
  };

  const message = (title, detail) => ui.stack({ gap: 'xs' }, [
    ui.text(title, { weight: 'semibold' }),
    detail ? ui.text(detail, { tone: 'secondary', size: 'sm', wrap: true }) : null,
  ]);

  const body = (provider) => {
    if (!provider.running) {
      return message(`${provider.title} 还没有获得授权`, '在控制台的"插件"页允许它读取本机信息后，这里会显示用量。');
    }
    const d = data.get(provider.id);
    if (!d) return message('正在读取…');
    if (d.error) return message(`${provider.title} 暂时无法读取`, d.error);
    if (d.accounts.length === 0) return message(`本机没有已登录的 ${provider.title} 账户`);
    const i = Math.min(accountIndex.get(provider.id) ?? 0, d.accounts.length - 1);
    const entry = d.accounts[i];
    if (entry.error || !entry.usage) return ui.stack({ gap: 'sm' }, [identity(provider, entry), ui.text(entry.error ?? '没有数据', { tone: 'secondary', size: 'sm', wrap: true })]);
    if (entry.usage.balance) return balanceView(provider, entry);
    if (entry.usage.windows?.length) return quotaView(provider, entry);
    return ui.stack({ gap: 'sm' }, [identity(provider, entry), ui.text('没有数据', { tone: 'secondary', size: 'sm' })]);
  };

  // Compact island: the most pressing number across every provider.
  const summary = () => {
    let top = null;
    for (const d of data.values()) {
      for (const e of d.accounts ?? []) {
        for (const w of e.usage?.windows ?? []) top = Math.max(top ?? 0, w.usedPercent);
      }
    }
    return top;
  };

  // Chips: the busiest quota overall, and one per account (a provider's mark and its busiest window).
  const chipText = (value, alerting) => ui.row({ gap: 'xs', align: 'center' }, [
    ui.icon(alerting ? 'TriangleAlert' : 'Gauge', { tone: alerting ? 'accent' : 'secondary', size: 14 }),
    ui.text(value === null ? '—' : `${Math.round(value)}%`, { size: 'sm', weight: 'semibold', tabular: true }),
  ]);
  const renderChips = (top, alerting) => {
    if (top === null) island.ambient('summary').remove();
    else island.ambient('summary').update(chipText(top, alerting));
    const seen = new Set();
    for (const provider of providers) {
      for (const e of data.get(provider.id)?.accounts ?? []) {
        const windows = e.usage?.windows ?? [];
        if (windows.length === 0) continue;
        // Keyed by who the account is, not by its handle: handles are new every session, and the user's choices
        // (shown or not, which side) must still apply next time. The same login found twice gets a number.
        const base = `${provider.id}:${e.account.label || e.account.handle}`.slice(0, 60);
        let instance = base;
        for (let n = 2; seen.has(instance); n++) instance = `${base}#${n}`;
        seen.add(instance);
        const busiest = Math.max(...windows.map((w) => w.usedPercent));
        island.ambient('account', instance).update(ui.row({ gap: 'xs', align: 'center' }, [
          mark(provider.id, 14),
          ui.text(`${Math.round(busiest)}%`, { size: 'sm', weight: 'semibold', tabular: true }),
        ]), { title: `${provider.title} · ${e.account.label || e.account.handle}` });
      }
    }
    for (const instance of accountChips) if (!seen.has(instance)) island.ambient('account', instance).remove();
    accountChips.clear();
    for (const instance of seen) accountChips.add(instance);
  };

  const render = () => {
    const withData = [...data.values()].some((d) => d.accounts?.some((e) => e.usage));
    if (!providers.some((p) => p.id === current)) current = providers[0]?.id ?? null;

    const provider = providers.find((p) => p.id === current);
    const d = data.get(current);
    const alertAt = settings.get('alertAt');
    const top = summary();
    const alerting = top !== null && top >= alertAt;

    const accounts = d?.accounts ?? [];
    const ai = Math.min(accountIndex.get(current) ?? 0, Math.max(0, accounts.length - 1));
    // The island header says the one thing worth a glance: a warning while a limit is near, else how fresh the numbers are.
    const updated = accounts[ai]?.usage?.updatedAt;
    const status = alerting
      ? ui.row({ gap: 'xs', align: 'center' }, [
          ui.icon('TriangleAlert', { tone: 'accent', size: 14 }),
          ui.text(`额度 ${Math.round(top)}%`, { size: 'sm', weight: 'semibold', tabular: true }),
        ])
      : updated ? ui.text(`更新于${ago(updated)}`, { tone: 'secondary', size: 'sm' }) : null;
    renderChips(top, alerting);
    // A limit close to running out is a live activity (docs/design/09-island-v2.md), announced once.
    if (alerting) {
      const spec = {
        compact: {
          leading: ui.icon('TriangleAlert', { tone: 'accent', size: 14 }),
          trailing: ui.text(`${Math.round(top)}%`, { size: 'sm', weight: 'semibold', tabular: true }),
        },
        minimal: ui.icon('TriangleAlert', { tone: 'accent', size: 14 }),
        expanded: {
          leading: ui.icon('TriangleAlert', { tone: 'accent', size: 24 }),
          center: ui.stack({ gap: 'xs' }, [
            ui.text(`AI 额度已用 ${Math.round(top)}%`, { weight: 'semibold' }),
            ui.text(`超过了提醒阈值 ${alertAt}%，详情在主页的“AI 用量”。`, { tone: 'secondary', size: 'sm', wrap: true }),
          ]),
        },
      };
      if (act) act.update(spec); else act = island.activity('usage', spec);
      if (!alerted) {
        act.alert({ leading: ui.icon('TriangleAlert', { tone: 'accent' }), center: ui.text(`AI 额度 ${Math.round(top)}%`, { size: 'sm', weight: 'semibold' }), seconds: 5 });
      }
    } else {
      act?.end();
      act = null;
    }
    alerted = alerting;
    if (providers.length === 0) {
      island.home.page('usage').update(ui.stack({ gap: 'sm' }, [
        ui.text('还没有可用的 AI 提供商', { weight: 'semibold' }),
        ui.text('在控制台的"插件"页启用 Codex、Claude 等提供商插件后，这里会显示它们的额度。', { tone: 'secondary', wrap: true }),
      ]));
      return;
    }
    island.home.page('usage').update(ui.stack({ gap: 'md', align: 'stretch' }, [
        // Provider tabs, and — only when this provider has several — a compact account picker at the far end.
        // The provider is switched with the ‹ › arrows below (its mark says which one is shown), so no tab row here.
        accounts.length > 1
          ? ui.row({ gap: 'xs', align: 'center' }, [
              ui.spacer(),
              ui.picker({ value: String(ai), onChange: (v) => { accountIndex.set(current, Number(v)); render(); } },
                accounts.map((e, i) => ui.option(String(i), e.account.label || `账号 ${i + 1}`))),
            ])
          : null,
        // ‹ content ›: the arrows sit at the card's edges, beside what they switch, like a carousel.
        providers.length > 1
          ? ui.row({ gap: 'xs', align: 'center' }, [
              ui.iconButton('ChevronLeft', { label: '上一个', onClick: () => step(-1) }),
              ui.spacer(),
              ui.swap({ id: current, direction }, [provider ? body(provider) : null]),
              ui.spacer(),
              ui.iconButton('ChevronRight', { label: '下一个', onClick: () => step(1) }),
            ])
          : (provider ? body(provider) : null),
        providers.length > 1 ? ui.dots({ count: providers.length, index: providers.findIndex((p) => p.id === current) }) : null,
      ]), { status });
  };

  // One refresh at a time; a request during a refresh runs once more afterwards.
  let refreshing = false;
  let again = false;
  const refresh = async () => {
    if (refreshing) {
      again = true;
      return;
    }
    refreshing = true;
    try {
      await refreshOnce();
    } finally {
      refreshing = false;
      if (again) {
        again = false;
        refresh();
      }
    }
  };

  const refreshOnce = async () => {
    providers = await island.providers.list(IFACE);
    for (const provider of providers) {
      if (!provider.running) {
        data.delete(provider.id);
        continue;
      }
      try {
        const accounts = await island.providers.call(IFACE, provider.id, 'accounts');
        const entries = [];
        for (const account of accounts) {
          try {
            entries.push({ account, usage: await island.providers.call(IFACE, provider.id, 'usage', account) });
          } catch (e) {
            entries.push({ account, error: e.message });
          }
        }
        data.set(provider.id, { accounts: entries });
      } catch (e) {
        data.set(provider.id, { accounts: [], error: e.message });
      }
    }
    render();
    clearTimeout(timer);
    timer = setTimeout(refresh, settings.get('every') * 60 * 1000);
  };

  settings.onChange(() => refresh());
  island.providers.onChange(() => refresh()); // a provider started, or the user granted it access
  await refresh();
}
