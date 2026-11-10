// "ai-usage" provider for Codex (ChatGPT plans), reusing the Codex CLI's login on this machine (read-only).
// UNOFFICIAL: the usage endpoint is the one the Codex client uses for its rate-limit display; it is not a public API
// and may change. Unknown response shapes are reported as such instead of being guessed at.
import { island, http, localAuth } from '@island/api';

const URL = 'https://chatgpt.com/backend-api/wham/usage';
const PLANS = { free: 'Free', plus: 'Plus', pro: 'Pro', team: 'Team', business: 'Business', enterprise: 'Enterprise', edu: 'Edu' };

const windowName = (seconds) => {
  if (!seconds) return '额度';
  const hours = seconds / 3600;
  if (hours === 5) return '5 小时';
  if (hours === 24 * 7) return '每周';
  if (hours >= 24 * 28 && hours <= 24 * 31) return '每月';   // free plans: a 30-day window
  return hours >= 48 ? `${Math.round(hours / 24)} 天` : `${Math.round(hours)} 小时`;
};

const toWindow = (w) => {
  if (!w || typeof w.used_percent !== 'number') return null;
  const resetsAt = typeof w.reset_at === 'number' ? new Date(w.reset_at * 1000).toISOString()
    : typeof w.reset_after_seconds === 'number' ? new Date(Date.now() + w.reset_after_seconds * 1000).toISOString()
    : undefined;
  return { name: windowName(w.limit_window_seconds), usedPercent: w.used_percent, resetsAt };
};

export function activate() {
  island.provide('ai-usage', 'codex', {
    async accounts() {
      return (await localAuth.accounts('codex-cli')).map((a) => ({
        handle: a.handle, label: a.email ?? a.label, plan: PLANS[a.plan] ?? a.plan, accountId: a.accountId,
      }));
    },
    async usage(account) {
      const res = await http.fetch(URL, {
        auth: account.handle,
        headers: account.accountId ? { 'ChatGPT-Account-Id': account.accountId } : {},
      });
      if (res.status === 401 || res.status === 403) throw new Error('Codex 登录已失效：请运行一次 Codex 重新登录');
      if (!res.ok) throw new Error(`Codex 用量接口返回 ${res.status}`);
      const data = res.json();
      const limits = data.rate_limit ?? data.rate_limits;
      const windows = [toWindow(limits?.primary_window ?? limits?.primary), toWindow(limits?.secondary_window ?? limits?.secondary)]
        .filter(Boolean);
      if (windows.length === 0) throw new Error('Codex 用量接口的格式变化了，暂时无法显示');
      return { plan: PLANS[data.plan_type] ?? data.plan_type ?? account.plan, windows };
    },
  });
}
