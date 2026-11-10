// Sample "ai-usage" provider with fixed numbers: shows how to write a provider plugin (see com.o_o.ai-usage for the
// protocol) and lets the AI usage view be previewed without signing in to anything.
import { island } from '@island/api';

const inHours = (h) => new Date(Date.now() + h * 3600 * 1000).toISOString();

export function activate() {
  // A top-up style provider (like DeepSeek or a NewAPI relay): a balance instead of quota windows.
  island.provide('ai-usage', 'demo-balance', {
    accounts: () => [{ handle: 'key', label: 'sk-…3f9a' }],
    usage: () => ({
      balance: { amount: 4.18, currency: 'CNY', label: '可用余额', details: [{ label: '充值', amount: 4.18 }, { label: '赠送', amount: 0 }] },
    }),
  });

  island.provide('ai-usage', 'demo', {
    accounts: () => [
      { handle: 'work', label: 'work@example.com', plan: 'Max 20x' },
      { handle: 'home', label: 'home@example.com', plan: 'Free' },
    ],
    usage: (account) => account.handle === 'work'
      ? {
          windows: [
            { name: '5 小时', usedPercent: 11, resetsAt: inHours(2.4) },
            { name: '每周', usedPercent: 40, resetsAt: inHours(24 * 6) },
          ],
          updatedAt: new Date(Date.now() - 4 * 60 * 1000).toISOString(),
        }
      : { windows: [{ name: '每月', usedPercent: 0, resetsAt: inHours(24 * 29) }] },
  });
}
