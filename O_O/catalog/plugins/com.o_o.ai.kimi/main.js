// "ai-usage" provider for Kimi (Moonshot AI open platform): the balance of each API key the user entered.
// Documented endpoint: GET /v1/users/me/balance on api.moonshot.cn (CNY) or api.moonshot.ai (USD)
// (https://platform.kimi.com/docs/api/balance). Response: { code: 0, status: true, data: { available_balance,
// voucher_balance, cash_balance } }.
import { island, http, settings } from '@island/api';

const SITES = {
  中国站: { base: 'https://api.moonshot.cn', currency: 'CNY' },
  国际站: { base: 'https://api.moonshot.ai', currency: 'USD' },
};

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
  island.provide('ai-usage', 'kimi', {
    accounts: () => keyAccounts('Kimi'),
    async usage(account) {
      const site = SITES[settings.get('region')] ?? SITES['中国站'];
      const res = await http.fetch(`${site.base}/v1/users/me/balance`, {
        headers: { Authorization: `Bearer ${await keyOf(account)}`, Accept: 'application/json' },
      });
      if (res.status === 401 || res.status === 403) {
        throw new Error('Kimi API Key 无效：请在控制台重新填写，并确认站点（中国站 / 国际站）选对了');
      }
      if (!res.ok) throw new Error(`Kimi 余额接口返回 ${res.status}`);
      const body = res.json();
      const d = body?.data;
      if ((body?.code !== 0 && body?.status !== true) || typeof d?.available_balance !== 'number') {
        throw new Error('Kimi 余额接口的格式变化了，暂时无法显示');
      }
      return {
        updatedAt: new Date().toISOString(),
        balance: {
          amount: d.available_balance,
          currency: site.currency,
          label: d.available_balance <= 0 ? '余额不足' : '可用余额',
          details: [
            { label: '现金', amount: d.cash_balance ?? 0 },
            { label: '代金券', amount: d.voucher_balance ?? 0 },
          ],
        },
      };
    },
  });
}
