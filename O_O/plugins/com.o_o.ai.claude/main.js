// "ai-usage" provider for Claude subscriptions (Pro / Max).
// Data comes from Claude Code's documented status line (https://code.claude.com/docs/en/statusline): once the user
// connects it in the O_O console, every status line refresh hands O_O the plan's 5-hour and weekly usage. No token is
// read and no network request is made; the numbers are as fresh as the last Claude Code activity.
import { island, localAuth, settings } from '@island/api';

export function activate() {
  // A changed directory list means different accounts: tell the aggregator to look again.
  settings.onChange(() => island.providers.call('ai-usage', 'claude', 'accounts').catch(() => {}));

  island.provide('ai-usage', 'claude', {
    async accounts() {
      const configDirs = (settings.get('configDirs') ?? '').split('\n').map((d) => d.trim()).filter(Boolean);
      return (await localAuth.accounts('claude-code', { configDirs }))
        .map((a) => ({ handle: a.handle, label: a.email ?? a.label, plan: a.plan }));
    },
    async usage(account) {
      const s = await localAuth.statusLine(account);
      if (!s.connected) throw new Error('还没有接入：在控制台"插件"页的 Claude 用量里点"接入"');
      const windows = [];
      if (s.fiveHour) windows.push({ name: '5 小时', usedPercent: s.fiveHour.usedPercent, resetsAt: s.fiveHour.resetsAt });
      if (s.sevenDay) windows.push({ name: '每周', usedPercent: s.sevenDay.usedPercent, resetsAt: s.sevenDay.resetsAt });
      if (windows.length === 0) throw new Error('已接入，等待数据：在 Claude Code 里发一条消息后就会更新');
      return { plan: account.plan, windows, updatedAt: s.updatedAt };
    },
  });
}
