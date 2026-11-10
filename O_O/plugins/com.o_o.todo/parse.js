// "明天 下午3点半 交周报" → { title: '交周报', due: Date, allDay: false, high: false }. Small and forgiving:
// whatever is not recognised stays in the title.
const WEEK = { 日: 0, 天: 0, 一: 1, 二: 2, 三: 3, 四: 4, 五: 5, 六: 6 };
const NUM = { 一: 1, 二: 2, 两: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10, 十一: 11, 十二: 12 };

const startOfDay = (d) => new Date(d.getFullYear(), d.getMonth(), d.getDate());

export function parseTask(line, now = new Date()) {
  let text = line.trim();
  let high = false;
  if (/^[!！]+/.test(text)) {
    high = true;
    text = text.replace(/^[!！]+\s*/, '');
  }
  let day = null;
  const take = (re, fn) => {
    const m = text.match(re);
    if (!m) return false;
    fn(m);
    text = (text.slice(0, m.index) + ' ' + text.slice(m.index + m[0].length)).trim();
    return true;
  };

  const today = startOfDay(now);
  take(/(今天|今日|明天|明日|后天)/, (m) => {
    const add = m[1].startsWith('今') ? 0 : m[1].startsWith('明') ? 1 : 2;
    day = new Date(today.getFullYear(), today.getMonth(), today.getDate() + add);
  })
  || take(/(?:下)?(?:周|星期|礼拜)([一二三四五六日天])/, (m) => {
    const next = m[0].startsWith('下');
    let add = (WEEK[m[1]] - today.getDay() + 7) % 7;
    if (next) add = add === 0 ? 7 : add + (add < 7 - today.getDay() ? 7 : 0);
    day = new Date(today.getFullYear(), today.getMonth(), today.getDate() + add);
  })
  || take(/(\d{1,2})\s*(?:月|\/|-)\s*(\d{1,2})\s*(?:日|号)?/, (m) => {
    day = new Date(today.getFullYear(), Number(m[1]) - 1, Number(m[2]));
    if (day < today) day = new Date(today.getFullYear() + 1, Number(m[1]) - 1, Number(m[2])); // already past: next year
  });

  let hour = null;
  let minute = 0;
  take(/(上午|早上|中午|下午|晚上|傍晚)?\s*(\d{1,2})\s*[:：]\s*(\d{2})/, (m) => {
    hour = Number(m[2]);
    minute = Number(m[3]);
    if (m[1] && /下午|晚上|傍晚/.test(m[1]) && hour < 12) hour += 12;
  })
  || take(/(上午|早上|中午|下午|晚上|傍晚)?\s*(\d{1,2}|[一二两三四五六七八九十]|十[一二])\s*点\s*(半|(\d{1,2})\s*分?)?/, (m) => {
    hour = /^\d+$/.test(m[2]) ? Number(m[2]) : NUM[m[2]];
    minute = m[3] === '半' ? 30 : m[4] ? Number(m[4]) : 0;
    if (m[1] && /下午|晚上|傍晚/.test(m[1]) && hour < 12) hour += 12;
    if (m[1] === '中午' && hour < 11) hour += 12;
  });

  let due = null;
  let allDay = false;
  if (hour !== null && hour < 24 && minute < 60) {
    const base = day ?? today;
    due = new Date(base.getFullYear(), base.getMonth(), base.getDate(), hour, minute);
  } else if (day) {
    due = day;
    allDay = true;
  }
  const title = text.replace(/\s+/g, ' ').trim() || line.trim();
  return { title, due, allDay, high };
}
