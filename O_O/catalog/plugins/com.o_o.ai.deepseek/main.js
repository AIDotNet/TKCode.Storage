// "ai-usage" provider for DeepSeek: the account balance of each API key the user entered in the console.
// Documented endpoint: GET https://api.deepseek.com/user/balance (https://api-docs.deepseek.com/api/get-user-balance).
import { island, http, settings } from '@island/api';

const BASE = 'https://api.deepseek.com';

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

export function activate() {
  island.provide('ai-usage', 'deepseek', {
    accounts: () => keyAccounts('DeepSeek'),
    async usage(account) {
      const res = await http.fetch(`${BASE}/user/balance`, {
        headers: { Authorization: `Bearer ${await keyOf(account)}`, Accept: 'application/json' },
      });
      if (res.status === 401 || res.status === 403) throw new Error('DeepSeek API Key 无效：请在控制台重新填写');
      if (!res.ok) throw new Error(`DeepSeek 余额接口返回 ${res.status}`);
      const data = res.json();
      const infos = Array.isArray(data.balance_infos) ? data.balance_infos : [];
      // One balance per currency; an account normally has one. Prefer CNY, the currency DeepSeek bills most keys in.
      const info = infos.find((b) => b.currency === 'CNY') ?? infos[0];
      if (!info) throw new Error('DeepSeek 余额接口的格式变化了，暂时无法显示');
      const num = (v) => Number.parseFloat(v ?? '0') || 0;
      return {
        updatedAt: new Date().toISOString(),
        balance: {
          amount: num(info.total_balance),
          currency: info.currency,
          label: data.is_available === false ? '余额不足' : '可用余额',
          details: [
            { label: '充值', amount: num(info.topped_up_balance) },
            { label: '赠送', amount: num(info.granted_balance) },
          ],
        },
      };
    },
  });
}
