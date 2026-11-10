// "ai-usage" provider for NewAPI / OneAPI-compatible relay sites: what is left on each token (sk-…) the user entered.
// Uses the OpenAI-style billing endpoints these sites expose for tokens (NewAPI controller/billing.go):
//   GET {base}/v1/dashboard/billing/subscription → { hard_limit_usd }: the token's total quota (remaining + used)
//   GET {base}/v1/dashboard/billing/usage        → { total_usage }:    used quota × 100
// Amounts are in the site's display unit (USD by default, CNY if the site is set so); the user says which.
// Errors come back as HTTP 200 with { error: { message } }.
//
// Network access: a self-hosted site's host cannot be declared in advance, so the manifest asks for
// network:@baseUrl — the host lets requests go only to the address the user typed into 站点地址 (same scheme, host
// and port; plain http only if the user entered http).
import { island, http, settings } from '@island/api';

const UNLIMITED = 100000000; // what NewAPI reports for a token without a quota limit

// Up to three API keys, each an "account". The handle names the slot (key1…), never the key itself; the key stays in
// the OS keychain and is read only to make the request.
const SLOTS = [1, 2, 3];
const mask = (key) => `${key.slice(0, 3)}…${key.slice(-4)}`;

const keyAccounts = async (fallbackName) => {
  const accounts = [];
  for (const n of SLOTS) {
    const key = await settings.getSecret(`key${n}`);
    if (!key) continue;
    const name = String(settings.get(`name${n}`) ?? "").trim();
    accounts.push({ handle: `key${n}`, label: name || (accounts.length === 0 && n === 1 ? fallbackName : mask(key)) });
  }
  return accounts;
};

const keyOf = async (account) => {
  const key = await settings.getSecret(account?.handle ?? "");
  if (!key) throw new Error("这个 API Key 已被删除，请在控制台重新填写");
  return key;
};

const baseUrl = () => {
  const raw = String(settings.get('baseUrl') ?? '').trim().replace(/\/+$/, '').replace(/\/v1$/, '');
  if (!raw) throw new Error('还没有填写站点地址：请在控制台的 NewAPI 插件设置里填写');
  if (!/^https?:\/\//.test(raw)) throw new Error('站点地址要以 https:// 或 http:// 开头');
  return raw;
};

const getJson = async (url, key) => {
  // The host only lets requests go to the address in the 站点地址 setting (network:@baseUrl).
  const res = await http.fetch(url, { headers: { Authorization: `Bearer ${key}`, Accept: 'application/json' } });
  if (res.status === 401 || res.status === 403) throw new Error('令牌无效或已过期：请在控制台重新填写');
  if (!res.ok) throw new Error(`站点返回 ${res.status}`);
  let body;
  try {
    body = res.json();
  } catch {
    throw new Error('站点返回的不是 JSON：请确认站点地址是 NewAPI / OneAPI 兼容站点');
  }
  if (body?.error) throw new Error(`站点返回错误：${body.error.message ?? body.error}`);
  return body;
};

const day = (d) => d.toISOString().slice(0, 10);

export function activate() {
  island.provide('ai-usage', 'newapi', {
    accounts: () => keyAccounts('NewAPI'),
    async usage(account) {
      const base = baseUrl();
      const key = await keyOf(account);
      const sub = await getJson(`${base}/v1/dashboard/billing/subscription`, key);
      const now = new Date();
      const usage = await getJson(
        `${base}/v1/dashboard/billing/usage?start_date=${day(new Date(now.getTime() - 99 * 86400000))}&end_date=${day(now)}`, key);
      const total = Number(sub?.hard_limit_usd);
      const used = Number(usage?.total_usage) / 100;
      if (!Number.isFinite(total) || !Number.isFinite(used)) throw new Error('站点的计费接口格式不认识，暂时无法显示');
      const currency = settings.get('currency') === 'CNY' ? 'CNY' : 'USD';
      if (total >= UNLIMITED) {
        return { updatedAt: now.toISOString(), plan: '不限额度', balance: { amount: used, currency, label: '已用额度', details: [] } };
      }
      const left = Math.max(0, total - used);
      return {
        updatedAt: now.toISOString(),
        balance: {
          amount: left,
          currency,
          label: left <= 0 ? '额度已用完' : '剩余额度',
          details: [
            { label: '总额度', amount: total },
            { label: '已用', amount: used },
          ],
        },
      };
    },
  });
}
