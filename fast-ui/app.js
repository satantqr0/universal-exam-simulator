'use strict';

const host = window.chrome?.webview;
const screen = document.querySelector('#screen');
const notice = document.querySelector('#notice');
const promptDialog = document.querySelector('#promptDialog');
const $ = (id) => document.getElementById(id);
const letters = 'ABCDEFGH';
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const formatter = new Intl.NumberFormat('zh-CN');
const numeral = (n) => formatter.format(n);
const points = (halfPoints) => String(halfPoints / 2);
const scoreText = (score) => `${score.PointsOutOf100} / 100`;
const percentPoints = (weight, totalWeight) => Math.round(10000 * weight / totalWeight) / 100;
function clockText(ms) {
  const seconds = Math.floor(ms / 1000);
  return [Math.floor(seconds / 3600), Math.floor(seconds / 60) % 60, seconds % 60]
    .map((part) => String(part).padStart(2, '0')).join(':');
}
function recordedTime({ ElapsedMilliseconds, PartialTiming }) {
  if (ElapsedMilliseconds == null) return '旧版未计时';
  return `${clockText(ElapsedMilliseconds)}${PartialTiming ? '（续答后）' : ''}`;
}
const button = (text, id, styles = '', disabled = false) => `<button type="button" id="${id}" class="${styles}"${disabled ? ' disabled' : ''}>${esc(text)}</button>`;
const on = (id, callback, event = 'click') => $(id).addEventListener(event, callback);
const heading = (title, side = '') => `<div class="head"><h1>${esc(title)}</h1>${side}</div>`;
const stats = (...metrics) => `<div class="glass stats">${metrics.map(([label, value, tone]) =>
  `<div><strong class="${tone || ''}">${esc(value)}</strong><span class="muted">${esc(label)}</span></div>`).join('')}</div>`;

let nextMessage = 0;
const requests = new Map();
host?.addEventListener('message', ({ data }) => {
  const request = requests.get(data.Id);
  if (!request) return;
  requests.delete(data.Id);
  window.clearTimeout(request.timeout);
  if (data.Error) request.reject(new Error(data.Error));
  else request.resolve(data.Data);
});
function rpc(action, fields = {}) {
  if (!host) return Promise.reject(new Error('请通过通用版考试模拟器打开题库。'));
  return new Promise((resolve, reject) => {
    const id = ++nextMessage;
    const timeout = action === 'import-bank' ? null :
      window.setTimeout(() => { requests.delete(id); reject(new Error('当前操作等待超时，请稍后重试。')); }, 30000);
    requests.set(id, { resolve, reject, timeout });
    host.postMessage({ Id: id, Action: action, ...fields });
  });
}

let toastTimer;
function toast(message) {
  notice.textContent = message instanceof Error ? message.message : message;
  notice.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { notice.hidden = true; }, 4400);
}

function ask(title, message, choices = [['cancel', '取消'], ['ok', '确定']]) {
  if (promptDialog.open) return Promise.resolve('cancel');
  $('promptTitle').textContent = title;
  $('promptText').textContent = message;
  $('promptButtons').innerHTML = choices.map(([value, label], i) =>
    `<button value="${value}"${i === choices.length - 1 ? ' class="primary"' : ''}>${esc(label)}</button>`).join('');
  promptDialog.returnValue = 'cancel';
  return new Promise((resolve) => {
    promptDialog.addEventListener('close', () => resolve(promptDialog.returnValue || 'cancel'), { once: true });
    promptDialog.showModal();
    $('promptButtons').querySelector('button')?.focus();
  });
}

let questions = new Map();
let kinds = [];
let halfPoints = [];
let bankList = [];
let activeBank;
let switchingBank = false;
let dashboard;
let homeSettings = { pool: 0, kind: 0, count: '100' };
let draft;
let screenId = 0;
let screenName;
let stopScreen = null;
let editor = null;
let examWrite = null;
let leavingExam = false;
let examKey = null;
let startingRound = false;

function view(name, html, under = name) {
  if (stopScreen) stopScreen();
  stopScreen = null;
  editor = null;
  screen.oninput = null;
  examKey = null;
  screenName = name;
  screen.innerHTML = html;
  screen.scrollTop = 0;
  screen.removeAttribute('aria-busy');
  for (const nav of document.querySelectorAll('nav button')) {
    if (nav.dataset.nav === under) nav.setAttribute('aria-current', 'page');
    else nav.removeAttribute('aria-current');
  }
  return ++screenId;
}

async function navigate(action) {
  if (editor?.dirty()) {
    const save = await ask(editor.title, '有修改未保存。', [['cancel', '取消'], ['discard', '不保存'], ['save', '保存']]);
    if (save === 'cancel' || (save === 'save' && !await editor.save())) return;
  }
  if (screenName === 'exam') {
    if (leavingExam) return;
    leavingExam = true;
    try {
      if (examWrite) await examWrite;
      await rpc('pause-timer');
    } catch (error) { toast(error); return; }
    finally { leavingExam = false; }
  }
  await action();
}

window.canCloseExamMachine = () => !editor?.dirty() || window.confirm('当前编辑有修改未保存，是否退出？');

function setWorkspace(data) {
  const differentBank = activeBank?.Id !== data.ActiveBank?.Id;
  bankList = data.Banks;
  activeBank = data.ActiveBank;
  questions = new Map((data.Questions ?? []).map((question) => [question.Id, question]));
  kinds = data.Kinds;
  halfPoints = data.HalfPoints;
  dashboard = data.Dashboard;
  draft = dashboard?.Draft;
  for (const nav of document.querySelectorAll('nav button:not([data-nav=banks])')) nav.disabled = !dashboard;
  if (differentBank) {
    homeSettings = { pool: 0, kind: 0, count: String(Math.min(100, activeBank?.Count ?? 100)) };
    wrongFilter = 'pending';
    wrongOffset = historyOffset = 0;
    pageCache.clear();
    memorySummary = null;
  }
}

function banks() {
  const rows = bankList.map((bank) => `<li><div><strong>${esc(bank.Name)}</strong><p class="muted">${numeral(bank.Count)} 题</p></div>
    <button type="button" data-bank="${esc(bank.Id)}"${bank.Id === activeBank?.Id ? ' disabled' : ''}>${bank.Id === activeBank?.Id ? '使用中' : '使用此题库'}</button></li>`).join('');
  const token = view('banks', `<div class="stack">
    <section class="glass hero bank-hero"><div class="hero-copy"><h1>${activeBank ? '题库管理' : '导入你的题库'}</h1><p>支持 Excel XLSX、Word DOCX、CSV 和 TSV。</p></div>
      ${button('选择题库文件', 'importBank', 'primary')}</section>
    <section class="glass card stack"><div class="head"><h2>导入格式</h2>${button('查看示例', 'bankFormat')}</div>
      <p class="muted">表格第一行包含「题干」「正确答案」，选择题填写「选项A」「选项B」等列。可加「题型」列，也可让程序识别。</p>
      <p class="muted">Word 可以使用相同表头的表格，或用编号、选项和「答案：」分行编写。</p></section>
    ${rows ? `<section class="glass card stack"><h2>已导入题库</h2><ul id="bankList" class="bank-list">${rows}</ul></section>` : ''}</div>`);
  on('bankFormat', () => ask('题库示例', '表格列名\n题型,题干,选项A,选项B,正确答案\n单选题,1 + 1 等于几？,1,2,B\n\nWord 文本示例\n1. 1 + 1 等于几？\nA. 1\nB. 2\n答案：B\n\n多选答案可填写 AC。判断题可填写「正确」或「错误」。', [['ok', '知道了']]));
  async function changeBank(action, fields = {}) {
    if (switchingBank) return;
    switchingBank = true;
    $('importBank').disabled = true;
    $('importBank').textContent = action === 'import-bank' ? '等待文件或导入…' : '正在载入题库…';
    try {
      const result = await rpc(action, fields);
      if (!result) return;
      setWorkspace(result);
      renderHome();
      if (action === 'import-bank') toast(`已导入 ${numeral(activeBank.Count)} 题`);
    } catch (error) {
      if (token === screenId) await ask('无法导入或打开题库', error.message, [['ok', '确定']]);
    } finally {
      switchingBank = false;
      if (token === screenId) {
        $('importBank').disabled = false;
        $('importBank').textContent = '选择题库文件';
      }
    }
  }
  on('importBank', () => changeBank('import-bank'));
  if ($('bankList')) on('bankList', (event) => {
    const selected = event.target.closest('button[data-bank]');
    if (selected) changeBank('select-bank', { BankId: selected.dataset.bank });
  });
}

function homeMarkup(data) {
  const kindCounts = kinds.map((kind, i) => `${esc(kind)} ${numeral(data.Pools[0][i + 1])}`).join('　·　');
  const resume = draft ? `
    <div class="shortcut"><div><h3>未交卷的练习</h3><p class="muted">已作答 ${draft.Items.filter((item) => item.Answer !== 0).length} / ${draft.Items.length}</p></div>${button('继续答题', 'resume', 'soft')}</div>` :
    `<div class="shortcut"><div><h3>错题本</h3><p class="muted">${numeral(data.Pending)} 道待重测</p></div>${button('打开错题本', 'openWrong', 'soft')}</div>`;
  return `<div class="stack">
    <section class="glass hero"><div class="hero-copy"><h1>${esc(activeBank.Name)}</h1><p>模拟考试与错题练习</p><p class="muted">${kindCounts}</p></div>
    <div class="bank-total"><strong>${numeral(data.Total)}</strong><span>题库总量</span></div></section>
    <div class="two">
      <section class="glass card form"><h2>随机组卷</h2><p class="muted">按历史交卷时实际作答的题目区分已做和未做。</p>
        <div class="segments" aria-label="抽题模式">${['全量题库', '仅抽未做题', '已做题巩固'].map((label, i) => button(label, `pool${i}`)).join('')}</div>
        <div class="fields"><label>题目数量<input id="amount" inputmode="numeric" maxlength="3" autocomplete="off"></label>
          <label>出题范围<select id="kind"><option>全部题型</option>${kinds.map((kind) => `<option>${esc(kind)}</option>`).join('')}</select></label></div>
        <p class="muted status" id="rangeLabel"></p><p class="paper-plan" id="paperPlan" aria-live="polite"></p>
        ${button('开始模拟考试', 'startExam', 'primary')}</section>
      <section class="glass card shortcuts"><h2>继续练习</h2>${resume}
        <div class="shortcut"><div><h3>高速记忆</h3><p class="muted">${numeral(data.Due)} 张闪卡到期</p></div>${button('打开高速记忆', 'openMemory', 'soft')}</div></section>
    </div>${stats(['累计练习', `${numeral(data.HistoryCount)} 场`], ['最近正确率', data.Last], ['累计正确率', data.Overall])}</div>`;
}

function renderHome() {
  const token = view('home', homeMarkup(dashboard));
  $('kind').selectedIndex = homeSettings.kind;
  let available;
  let maximum;
  const requestedAmount = () => {
    if (available < 10) return available;
    const value = homeSettings.count.trim();
    return /^\d+$/.test(value) ? Number(value) : NaN;
  };
  const preview = () => {
    const amount = requestedAmount();
    const kind = homeSettings.kind;
    const minimum = Math.min(available, 10);
    if (!(amount > 0 && amount >= minimum && amount <= maximum)) {
      $('paperPlan').textContent = '';
      return;
    }
    const counts = kind === 0 ? dashboard.PoolPlans[homeSettings.pool][amount] : kinds.map((_, i) => i === kind - 1 ? amount : 0);
    const totalWeight = counts.reduce((sum, count, i) => sum + count * halfPoints[i], 0);
    const breakdown = counts.flatMap((count, i) => count ? [`${kinds[i]} ${count} 题 / ${percentPoints(count * halfPoints[i], totalWeight)} 分`] : []);
    $('paperPlan').textContent = `${breakdown.join(' · ')}　｜　满分 100 分`;
  };
  const update = () => {
    const { pool, kind, count } = homeSettings;
    const poolCounts = dashboard.Pools[pool];
    available = poolCounts[kind];
    maximum = Math.min(100, available);
    const blocked = available === 0;
    const small = available < 10;
    $('amount').disabled = small || blocked;
    $('amount').value = small ? (available || '') : count;
    for (let i = 0; i < 3; i++) $(`pool${i}`).setAttribute('aria-pressed', String(i === pool));
    $('rangeLabel').classList.remove('error');
    $('rangeLabel').textContent = available === 0 ? '当前范围没有可抽取的题目。' : small ?
      `可抽 ${available} 题，本次将练完这些题。` :
      `可抽 ${numeral(available)} 题 · 请输入 10–${maximum} 题。`;
    $('startExam').disabled = blocked;
    $('startExam').textContent = blocked ? '暂无可组试卷' : small ? `练习这 ${available} 题` : pool === 2 ? '开始巩固模拟' : '开始模拟考试';
    preview();
  };
  for (let i = 0; i < 3; i++) on(`pool${i}`, () => { homeSettings.pool = i; update(); });
  on('kind', () => { homeSettings.kind = $('kind').selectedIndex; update(); }, 'change');
  on('amount', () => {
    homeSettings.count = $('amount').value;
    if ($('rangeLabel').classList.contains('error')) update(); else preview();
  }, 'input');
  on('amount', (event) => { if (event.key === 'Enter') $('startExam').click(); }, 'keydown');
  on('startExam', async () => {
    const { pool, kind } = homeSettings;
    const amount = requestedAmount();
    if (available >= 10 && !(amount >= 10 && amount <= maximum)) {
      $('rangeLabel').textContent = maximum === 10 ? '当前范围可组 10 题，请输入 10。' : `请输入 10 到 ${maximum} 之间的整数。`;
      $('rangeLabel').classList.add('error');
      $('amount').focus(); $('amount').select();
      return;
    }
    if (startingRound || !await replaceDraft() || token !== screenId || $('startExam').disabled) return;
    startingRound = true;
    $('startExam').disabled = true;
    try {
      draft = await rpc('start', { Count: amount, Kind: kind === 0 ? null : kinds[kind - 1], Pool: pool });
      if (token === screenId) exam();
    } catch (error) { toast(error); if (token === screenId) update(); }
    finally { startingRound = false; }
  });
  if ($('resume')) on('resume', exam);
  if ($('openWrong')) on('openWrong', mistakes);
  on('openMemory', memory);
  update();
  return token;
}

async function home() {
  if (!dashboard) { banks(); return; }
  const token = renderHome();
  try {
    const current = await rpc('dashboard');
    if (token !== screenId) return;
    const updated = JSON.stringify(current) !== JSON.stringify(dashboard);
    dashboard = current;
    draft = current.Draft;
    if (updated) renderHome();
  } catch (error) { toast(error); }
}

async function replaceDraft() {
  return !draft || await ask('开始新练习', '已有一场未交卷的练习。开始新一场会替换当前进度。') === 'ok';
}

async function startRetry(index) {
  if (startingRound || !await replaceDraft()) return;
  const token = screenId;
  startingRound = true;
  try {
    draft = await rpc('retry', { Index: index });
    if (token === screenId) exam();
  } catch (error) { toast(error); }
  finally { startingRound = false; }
}

function exam() {
  if (!draft) { home(); return; }
  const attempt = draft;
  const total = attempt.Items.length;
  const valueOf = (question) => halfPoints[kinds.indexOf(question.Kind)];
  const paperWeight = attempt.Items.reduce((sum, item) => sum + valueOf(questions.get(item.QuestionId)), 0);
  const actions = `<div class="row"><div class="exam-clock"><span id="examTimeLabel">作答用时</span>
    <strong id="examClock" role="timer" aria-labelledby="examTimeLabel"></strong></div>${button('保存并返回', 'examBack')}</div>`;
  const token = view('exam', `<div class="test">${heading(attempt.Mode, actions)}<div class="test-grid">
    <section class="glass card test-paper"><div class="question-region" id="questionRegion"><div class="row"><span class="chip" id="position"></span>
    <span class="muted" id="questionValue"></span></div>
    <h2 id="stem" class="question"></h2><p class="muted" id="instructions"></p><div id="options" class="answers" aria-label="选项">
    ${Array.from(letters, (letter, i) => `<button id="option${i}" type="button" class="option" data-choice="${i}"><span class="letter">${letter}</span><span class="choice"></span><span class="check" aria-hidden="true">✓</span></button>`).join('')}</div></div>
    <div class="row">${button('清空答案', 'clear')}${button('标记本题', 'flag')}<span class="grow"></span>${button('上一题', 'previous')}${button('下一题', 'next', 'primary')}</div></section>
    <aside class="glass card sheet"><h2>答题卡</h2><p class="muted">满分 100 分</p><p class="tone-green-text" id="progressLabel"></p><progress id="examProgress" max="${total}" aria-label="答题进度"></progress>
    <p class="muted">空白 未答　✓ 已答　⚑ 标记</p><div class="numbers" id="numbers" aria-label="题号">${attempt.Items.map((_, i) => `<button type="button" data-position="${i}">${i + 1}<sup aria-hidden="true"></sup></button>`).join('')}</div>
    ${button('交卷并查看结果', 'submit', 'primary')}</aside></div></div>`, '');

  const numbers = Array.from($('numbers').children);
  const choices = Array.from($('options').children);
  const item = () => attempt.Items[attempt.Cursor];
  const info = () => questions.get(item().QuestionId);
  let displayed = -1;
  let busy = false;
  let elapsedBase = attempt.ElapsedMilliseconds ?? 0;
  let resumedAt = performance.now();
  const clock = $('examClock');
  const refreshClock = () => {
    const readable = clockText(elapsedBase + performance.now() - resumedAt);
    if (clock.textContent !== readable) clock.textContent = readable;
  };
  if (attempt.PartialTiming || attempt.ElapsedMilliseconds == null) $('examTimeLabel').textContent = '续答用时';
  refreshClock();
  rpc('resume-timer').then((timing) => {
    if (token !== screenId) return;
    elapsedBase = timing.ElapsedMilliseconds;
    resumedAt = performance.now();
    attempt.ElapsedMilliseconds = elapsedBase;
    attempt.PartialTiming = timing.PartialTiming;
    refreshClock();
  }).catch((error) => { if (token === screenId) toast(error); });
  const displayClock = setInterval(refreshClock, 250);
  const storeClock = setInterval(() => {
    if (!leavingExam && !busy) rpc('save-timer').catch((error) => { if (token === screenId) toast(error); });
  }, 15000);
  stopScreen = () => { clearInterval(displayClock); clearInterval(storeClock); };
  const drawTile = (index) => {
    const entry = attempt.Items[index];
    const tile = numbers[index];
    tile.classList.toggle('done', !!entry.Answer);
    tile.classList.toggle('flag', entry.Flag);
    tile.classList.toggle('current', attempt.Cursor === index);
    tile.lastChild.textContent = entry.Flag ? ' ⚑' : entry.Answer ? ' ✓' : '';
    tile.setAttribute('aria-label', `第 ${index + 1} 题，${entry.Answer ? '已答' : '未答'}${entry.Flag ? '，已标记' : ''}`);
    if (attempt.Cursor === index) tile.setAttribute('aria-current', 'step'); else tile.removeAttribute('aria-current');
  };
  const draw = () => {
    const index = attempt.Cursor;
    const question = info();
    const active = item();
    const multi = question.Kind === '多选题';
    const last = index + 1 === total;
    const moved = displayed !== index;
    if (moved) {
      $('position').textContent = `${index + 1} / ${total}　·　${question.Kind}`;
      const weight = points(valueOf(question));
      $('questionValue').textContent = paperWeight === 200 ? `本题 ${weight} 分` : `本题权重 ${weight}`;
      $('stem').textContent = question.Prompt;
      $('questionRegion').scrollTop = 0;
    }
    $('instructions').textContent = multi ? (last ? '最后一题，可勾选多项后交卷' : '可勾选多项，选完后点「确认并下一题」') :
      (last ? '这是最后一题，选完后可交卷' : '选择后自动进入下一题');
    choices.forEach((choice, i) => {
      if (moved) { choice.hidden = i >= question.Options.length; choice.querySelector('.choice').textContent = question.Options[i] ?? ''; }
      const selected = !!(active.Answer & (1 << i));
      choice.setAttribute('aria-pressed', String(selected));
      choice.querySelector('.check').hidden = !selected;
    });
    if (displayed < 0) numbers.forEach((_, i) => drawTile(i));
    else { if (moved) drawTile(displayed); drawTile(index); }
    if (moved) numbers[index].scrollIntoView({ block: 'nearest', inline: 'nearest' });
    displayed = index;
    const answered = attempt.Items.reduce((count, entry) => count + Boolean(entry.Answer), 0);
    $('examProgress').value = answered;
    $('progressLabel').textContent = `已作答 ${answered} / ${total}`;
    $('clear').disabled = !active.Answer;
    $('flag').textContent = active.Flag ? '取消标记' : '标记本题';
    $('flag').classList.toggle('tone-amber', active.Flag);
    $('previous').disabled = index === 0;
    $('next').disabled = last;
    $('next').textContent = multi && active.Answer && !last ? '确认并下一题' : '下一题';
  };
  function change(action, fields, paint) {
    if (busy || leavingExam || token !== screenId) return;
    busy = true;
    const former = attempt.Cursor;
    const saved = { ...item() };
    paint(); draw();
    examWrite = rpc(action, fields).catch((error) => {
      attempt.Items[former] = saved;
      attempt.Cursor = former;
      if (token === screenId) draw();
      toast(error);
    }).finally(() => { busy = false; examWrite = null; });
  }
  function pick(index) {
    const question = info();
    if (index >= question.Options.length) return;
    const multi = question.Kind === '多选题';
    const mask = multi ? item().Answer ^ (1 << index) : 1 << index;
    change('answer', { Index: attempt.Cursor, Mask: mask }, () => {
      item().Answer = mask;
      if (!multi && attempt.Cursor + 1 < total) attempt.Cursor++;
    });
  }
  const move = (index) => {
    if (index !== attempt.Cursor && index >= 0 && index < total)
      change('move', { Index: index }, () => { attempt.Cursor = index; });
  };
  on('options', (event) => { const choice = event.target.closest('button[data-choice]'); if (choice) pick(Number(choice.dataset.choice)); });
  on('numbers', (event) => { const tile = event.target.closest('button[data-position]'); if (tile) move(Number(tile.dataset.position)); });
  on('previous', () => move(attempt.Cursor - 1));
  on('next', () => move(attempt.Cursor + 1));
  on('flag', () => change('flag', {}, () => { item().Flag = !item().Flag; }));
  on('clear', () => change('answer', { Index: attempt.Cursor, Mask: 0 }, () => { item().Answer = 0; }));
  on('examBack', () => navigate(home));
  on('submit', async () => {
    if (leavingExam || (busy && !examWrite)) return;
    if (examWrite) await examWrite;
    const blank = attempt.Items.filter((entry) => !entry.Answer).length;
    const message = blank ? `还有 ${blank} 题未作答。交卷后，这些题会计入总题数并进入错题本。` : '交卷后即可查看本场成绩和答案。';
    if (await ask('确认交卷', message) !== 'ok' || token !== screenId || busy || leavingExam) return;
    busy = true;
    pageCache.clear();
    memorySummary = null;
    try {
      const result = await rpc('submit');
      draft = null;
      if (token === screenId) renderResult(result, false);
    } catch (error) { toast(error); }
    finally { busy = false; }
  });
  examKey = (event) => {
    const index = letters.indexOf(event.key.toUpperCase());
    if (event.key.length !== 1 || index < 0 || event.ctrlKey || event.altKey || event.metaKey || promptDialog.open) return;
    event.preventDefault();
    if (!event.repeat) pick(index);
  };
  draw();
}

const table = (columns, rows) => `<table><thead><tr>${columns.map((value) => `<th>${esc(value)}</th>`).join('')}</tr></thead><tbody>${rows}</tbody></table>`;
const tone = (status) => status === '答对' ? 'tone-green-text' : status === '答错' ? 'tone-red-text' : 'tone-amber-text';
const keyText = (question, mask) => question.Options.reduce((selected, option, index) => {
  if (mask & (1 << index)) selected.push(`${letters[index]}. ${option}`);
  return selected;
}, []);
const lines = (options) => `<ul>${options.map((option) => `<li>${esc(option)}</li>`).join('')}</ul>`;
function questionDetail(row, answerLabel) {
  if (!row) return '<p class="muted">当前范围内没有题目。</p>';
  const q = questions.get(row.QuestionId);
  const options = q.Options.map((option, i) => `<li class="${row.Correct & (1 << i) ? 'tone-green-text' : ''}">${letters[i]}. ${esc(option)}</li>`).join('');
  return `<p class="muted">${esc(q.Kind)}　·　原题第 ${q.Number} 题</p><h2>${esc(q.Prompt)}</h2><ul>${options}</ul>
    <h3>${esc(answerLabel)}</h3>${row.Answer ? lines(keyText(q, row.Answer)) : '<p class="tone-amber-text">未作答</p>'}
    <h3 class="tone-green-text">正确答案</h3>${lines(keyText(q, row.Correct))}`;
}

function attachDetails(rows, answerLabel) {
  const group = $('reviewList');
  const select = (index) => {
    for (const entry of group.querySelectorAll('tr[aria-selected]')) entry.removeAttribute('aria-selected');
    group.querySelector(`[data-row="${index}"]`)?.closest('tr')?.setAttribute('aria-selected', 'true');
    $('reviewDetail').innerHTML = questionDetail(rows[index], answerLabel);
    $('reviewDetail').scrollTop = 0;
  };
  group.onclick = (event) => {
    const selected = event.target.closest('[data-row]');
    if (selected) select(Number(selected.dataset.row));
  };
  select(0);
}

function renderResult(result, fromHistory) {
  const score = result.Score;
  const misses = score.Wrong + score.Blank;
  const breakdown = result.ByKind.map(({ Kind, Score }) => {
    const earned = percentPoints(Score.EarnedHalfPoints, score.PossibleHalfPoints);
    const maximum = percentPoints(Score.PossibleHalfPoints, score.PossibleHalfPoints);
    return `${Kind} ${earned} / ${maximum} 分（答对 ${Score.Correct}/${Score.Total}）`;
  }).join('　·　');
  view('result', `<div class="stack">${heading(`${result.Mode}成绩`, button(fromHistory ? '返回练习记录' : '返回首页', 'resultBack'))}
  ${stats(['卷面得分', scoreText(score), 'points-stat'], ['正确率', score.Accuracy], ['答对', `${score.Correct} 题`, 'tone-green-text'], ['答错', `${score.Wrong} 题`, 'tone-red-text'], ['未作答', `${score.Blank} 题`, 'tone-amber-text'])}
  <p class="muted">共 ${score.Total} 题　·　${esc(breakdown)}</p>
  <div class="row muted result-time" id="resultTime" aria-label="本场练习时间">
    <span>作答用时 <strong>${recordedTime(result)}</strong></span><span>开始 ${esc(result.StartedAt)}</span><span>交卷 ${esc(result.FinishedAt)}</span></div>
  <div class="head"><label>查看题目<select id="resultFilter"><option>全部</option><option>答错与未作答</option><option>答错</option><option>未作答</option><option>答对</option></select></label>
  ${button('重测本场错题', 'retryResult', 'primary', !misses)}</div>
  <div class="split"><div class="glass card scroll-pane" id="reviewList"></div><article class="glass card scroll-pane detail-text" id="reviewDetail"></article></div></div>`, fromHistory ? 'history' : '');
  $('resultFilter').selectedIndex = misses ? 1 : 0;
  const filter = () => {
    const group = $('resultFilter').value;
    const rows = result.Rows.filter((row) => group === '全部' || group === row.Status || (group === '答错与未作答' && row.Status !== '答对'));
    $('reviewList').innerHTML = table(['题号', '结果', '题目'], rows.map((row, i) => `<tr><td>${row.Index}</td><td class="${tone(row.Status)}">${esc(row.Status)}</td>
      <td><button class="row-button" data-row="${i}">${esc(questions.get(row.QuestionId).Prompt)}</button></td></tr>`).join(''));
    attachDetails(rows, '本次作答');
  };
  on('resultFilter', filter, 'change');
  on('retryResult', () => startRetry(result.Index));
  on('resultBack', fromHistory ? history : home);
  filter();
}

const pager = (data) => data.Total <= data.PageSize ? '' : `<div class="row pager"><span class="muted">${data.Offset + 1}–${Math.min(data.Total, data.Offset + data.PageSize)} / ${numeral(data.Total)}</span>
  <span class="row">${button('上一页', 'listPrev', '', data.Offset === 0)}${button('下一页', 'listNext', '', data.Offset + data.PageSize >= data.Total)}</span></div>`;
function bindPager(data, load) {
  if (!$('listPrev')) return;
  on('listPrev', () => load(data.Offset - data.PageSize));
  on('listNext', () => load(data.Offset + data.PageSize));
}

let wrongFilter = 'pending';
let wrongOffset = 0;
let historyOffset = 0;
const pageCache = new Map();
let memorySummary = null;

function cachePage(key, data) {
  if (pageCache.size >= 24) pageCache.delete(pageCache.keys().next().value);
  pageCache.set(key, data);
}

function mistakes() {
  const token = view('mistakes', `<div class="stack">${heading('错题本', button('开始错题重测', 'retryWrong', 'primary', true))}
    <div id="wrongStats"></div><div class="head"><p class="muted">答错和未答的题会进入错题本，下次答对后移出待重测列表。</p>
    <label>错题范围<select id="wrongFilter"><option value="pending">待重测</option><option value="mastered">已答对</option><option value="all">全部错题</option></select></label></div>
    <div class="split"><div class="glass card" id="wrongTable"><div class="scroll-pane" id="reviewList"></div><div id="wrongPager"></div></div><article class="glass card scroll-pane detail-text" id="reviewDetail"></article></div></div>`);
  $('wrongFilter').value = wrongFilter;
  let version = 0;
  function paint(data) {
    wrongOffset = data.Offset;
    $('wrongStats').innerHTML = stats(['待重测', `${data.Pending} 题`, 'tone-amber-text'], ['累计答错', `${data.Wrong} 次`], ['累计未作答', `${data.Blank} 次`, 'tone-amber-text']);
    $('retryWrong').disabled = data.Pending === 0;
    $('reviewList').innerHTML = table(['题目', '答错', '未答', '状态'], data.Rows.map((row, index) => `<tr>
      <td><button class="row-button" data-row="${index}">${esc(questions.get(row.QuestionId).Prompt)}</button></td>
      <td>${row.Wrong}</td><td>${row.Blank}</td><td>${row.Pending ? '待重测' : '已答对'}</td></tr>`).join(''));
    $('wrongPager').innerHTML = pager(data);
    bindPager(data, load);
    attachDetails(data.Rows, '上次答错或未作答时的答案');
  }
  async function load(offset) {
    const key = `mistakes:${wrongFilter}:${offset}`;
    const request = ++version;
    const cached = pageCache.get(key);
    if (cached) { paint(cached); return; }
    try {
      const data = await rpc('mistakes', { Index: offset, Filter: wrongFilter });
      if (token !== screenId || version !== request) return;
      cachePage(key, data);
      paint(data);
    } catch (error) { toast(error); }
  }
  on('wrongFilter', () => { wrongFilter = $('wrongFilter').value; load(0); }, 'change');
  on('retryWrong', () => startRetry(-1));
  load(wrongOffset);
}

function history() {
  const token = view('history', `<div class="stack">${heading('练习记录')}<p class="muted" id="historySummary"></p><section class="glass card" id="historyList"><p class="muted">正在载入练习记录…</p></section></div>`);
  let version = 0;
  function paint(data) {
    historyOffset = data.Offset;
    $('historySummary').textContent = `已完成 ${numeral(data.Total)} 场练习`;
    if (!data.Total) { $('historyList').innerHTML = '<p class="muted">完成并交卷后，这里会显示本场成绩。</p>'; return; }
    const rows = data.Rows.map((entry) => {
      const { Index, Started, Date, Mode, Score } = entry;
      return `<tr><td class="history-dates"><button class="row-button" data-result="${Index}" aria-label="查看 ${esc(Date)} 的成绩">交卷 ${esc(Date)}</button>
        <div class="muted">开始 ${esc(Started)}</div></td><td>${esc(Mode)}</td><td class="elapsed-cell">${recordedTime(entry)}</td><td class="score-cell">${scoreText(Score)}</td>
        <td>${Score.Total}</td><td>${Score.Correct}</td><td>${Score.Wrong}</td><td>${Score.Blank}</td><td>${esc(Score.Accuracy)}</td></tr>`;
    }).join('');
    $('historyList').innerHTML = `<div class="scroll-pane">${table(['练习时间', '练习方式', '作答用时', '得分', '题数', '答对', '答错', '未答', '正确率'], rows)}</div>${pager(data)}`;
    bindPager(data, load);
  }
  $('historyList').onclick = async (event) => {
    const selected = event.target.closest('[data-result]');
    if (!selected) return;
    try { const result = await rpc('result', { Index: Number(selected.dataset.result) }); if (token === screenId) renderResult(result, true); }
    catch (error) { toast(error); }
  };
  async function load(offset) {
    const key = `history:${offset}`;
    const cached = pageCache.get(key);
    const request = ++version;
    if (cached) { paint(cached); return; }
    try {
      const data = await rpc('history', { Index: offset });
      if (token !== screenId || version !== request) return;
      cachePage(key, data);
      paint(data);
    } catch (error) { toast(error); }
  }
  load(historyOffset);
}

const memoryTile = (title, description, label, id, enabled = true) => `<section class="glass card memory-tile"><h2>${esc(title)}</h2>
  <p class="muted">${esc(description)}</p>${button(label, id, 'primary', !enabled)}</section>`;

async function memory() {
  const today = new Date().toDateString();
  if (memorySummary?.day === today) { paint(memorySummary.data); return; }
  const token = view('memory', `${heading('高速记忆')}<p class="glass wait">正在载入复习安排…</p>`);
  try {
    const data = await rpc('memory');
    if (token !== screenId) return;
    memorySummary = { day: today, data };
    paint(data);
  } catch (error) { toast(error); }

  function paint(data) {
    view('memory', `<div class="stack">${heading('高速记忆')}
      <p class="muted">先回忆再翻卡。按首次想起后的第 1、3、7、16、35 天安排复习。</p>
      ${stats(['到期复习（含逾期）', `${data.Due} 张`, data.Due ? 'tone-amber-text' : ''], ['已学习', `${data.Learned} 张`], ['完成复习计划', `${data.Finished} 张`, 'tone-green-text'])}
      <div class="memory-grid">
      ${memoryTile('到期闪卡', data.Due ? '先回忆答案，再按实际熟练度自评。' : '今天没有到期的闪卡。可以先学习新卡。', '复习到期卡', 'dueDeck', Boolean(data.Due))}
      ${memoryTile('学习新卡', '每批最多 20 张，优先学习错题本中还未学过的题。', '抽取新卡', 'newDeck', Boolean(data.New))}
      ${memoryTile('睡前难点', data.Difficult ? '集中回顾错题和上次回忆不熟的闪卡，最多 30 分钟。' : '先做题或学闪卡，错题和回忆不熟的卡会加入这里。', '开始 30 分钟复习', 'nightDeck', Boolean(data.Difficult))}</div>
      <div class="flash-footer">${button('白纸复盘 · 组块 · 费曼', 'notes')}${button('编辑记忆宫殿', 'palace')}${button('开始 40 秒闭目复盘', 'recall40')}${button('练习策略速查', 'strategies')}</div></div>`);
    on('dueDeck', () => beginCards('due'));
    on('newDeck', () => beginCards('new'));
    on('nightDeck', () => beginCards('difficult'));
    on('notes', () => notes(null, memory));
    on('palace', palace);
    on('recall40', recallTimer);
    on('strategies', strategies);
  }
}

function strategies() {
  view('strategies', `<div class="stack">${heading('练习策略速查', button('返回高速记忆', 'strategyBack'))}<div class="two">
    ${memoryTile('海量零散知识', '主动提取 + 间隔重复。先猜答案，再翻卡核对，优先处理到期和不熟的卡。', '进入闪卡', 'strategyCards')}
    ${memoryTile('需要按顺序记的列表', '记忆宫殿 + 夸张联结。沿熟悉的路线停在每一站，回忆放在那里的画面。', '进入记忆宫殿', 'strategyPalace')}
    ${memoryTile('复杂概念和体系', '费曼讲解 + 白纸复盘。先写框架，用自己的话讲给初学者听，再回资料补卡点。', '进入复盘工作台', 'strategyNotes')}
    ${memoryTile('短期突击备考', '先默写框架，然后从错题本开始重测，找出仍然答错或答不出的题。', '进入错题本', 'strategyWrong')}</div></div>`, 'memory');
  on('strategyBack', memory);
  on('strategyCards', async () => {
    const token = screenId;
    try { const data = await rpc('memory'); if (token === screenId) beginCards(data.Due ? 'due' : 'new'); }
    catch (error) { toast(error); }
  });
  on('strategyPalace', palace);
  on('strategyNotes', () => notes(null, strategies));
  on('strategyWrong', mistakes);
}

async function beginCards(kind) {
  const token = screenId;
  try {
    const cards = await rpc('deck', { Filter: kind });
    if (token !== screenId) return;
    if (!cards.length) { toast('当前没有可练习的闪卡。'); return; }
    showCards({ cards, title: { due: '到期闪卡', new: '学习新卡', difficult: '睡前难点' }[kind], index: 0, right: 0,
      phase: 'recall', mask: 0, feedback: '', deadline: kind === 'difficult' ? Date.now() + 1800000 : null });
  } catch (error) { toast(error); }
}

function showCards(session) {
  if (session.deadline && Date.now() >= session.deadline) { finishCards(session); return; }
  const token = view('flash', `<div class="flash">${heading(session.title, `<div class="row"><strong id="nightClock"></strong>${button('结束本轮', 'finishCards')}</div>`)}
    <p id="cardProgress" class="muted"></p><section class="glass card flash-body" id="flashBody"><span class="chip" id="flashPhase"></span><h2 class="question" id="flashQuestion"></h2><div id="flashContent"></div></section>
    <div class="flash-footer" id="frontActions">${button('显示选项提示', 'hint', 'tone-amber')}${button('翻卡核对', 'flip', 'primary')}</div>
    <div class="flash-footer" id="rateActions" hidden>${button('没想起来', 'forgot', 'tone-red')}${button('有点模糊', 'fuzzy', 'tone-amber')}${button('想起来了', 'recalled', 'tone-green')}
    ${button('为本题做笔记', 'cardNote')}${button('40 秒闭目复盘', 'cardTimer')}</div></div>`, 'memory');
  let busy = false;
  const q = () => questions.get(session.cards[session.index].Id);
  function draw() {
    const question = q();
    $('cardProgress').textContent = `${session.index + 1} / ${session.cards.length}　·　${question.Kind}${session.feedback ? `　·　${session.feedback}` : ''}`;
    $('flashBody').dataset.phase = session.phase;
    $('flashBody').scrollTop = 0;
    $('flashQuestion').textContent = question.Prompt;
    const revealed = session.phase === 'answer';
    $('flashPhase').textContent = { recall: '主动回忆', hint: '借助提示', answer: '核对答案' }[session.phase];
    $('flashContent').innerHTML = revealed ? `<div class="key tone-green-text"><span>正确答案</span>${lines(keyText(question, session.mask))}</div>
      <p class="muted">对照后选择熟练度。如果借助选项才想起来，可以选「有点模糊」。</p>` : session.phase === 'hint' ?
      lines(question.Options.map((option, i) => `${letters[i]}. ${option}`)) : '<p class="muted">先试着说出答案，再翻卡核对。</p>';
    $('frontActions').hidden = revealed;
    $('rateActions').hidden = !revealed;
    $('hint').disabled = session.phase === 'hint';
  }
  async function rate(value) {
    if (busy) return;
    busy = true;
    memorySummary = null;
    try {
      const info = await rpc('rate', { QuestionId: q().Id, Rating: value });
      session.index++;
      if (value === 2) session.right++;
      session.feedback = info.DueDay ? `上张卡下次复习 ${info.DueDay}` : '上张卡已完成复习计划';
      session.phase = 'recall';
      if (token === screenId) { if (session.index >= session.cards.length) finishCards(session); else draw(); }
    } catch (error) { toast(error); }
    finally { busy = false; }
  }
  on('hint', () => { session.phase = 'hint'; draw(); });
  on('flip', () => {
    if (busy) return;
    session.mask = session.cards[session.index].Mask;
    session.phase = 'answer';
    draw();
  });
  on('forgot', () => rate(0)); on('fuzzy', () => rate(1)); on('recalled', () => rate(2));
  on('finishCards', () => { if (!busy) finishCards(session); });
  on('cardNote', () => { if (!busy) notes(q().Id, () => showCards(session)); });
  on('cardTimer', recallTimer);
  if (session.deadline) {
    const tick = () => {
      const seconds = Math.max(0, Math.ceil((session.deadline - Date.now()) / 1000));
      if (!seconds && !busy) finishCards(session); else $('nightClock').textContent = timeText(seconds);
    };
    const clock = setInterval(tick, 300);
    stopScreen = () => clearInterval(clock);
    tick();
  }
  if (token === screenId) draw();
}

function finishCards(session) {
  const token = view('card-result', `<div class="stack">${heading('本轮回忆完成')}<div id="recallStats"></div>
    <p class="muted">${esc(session.feedback || '已完成自评的闪卡会自动保存安排。')}</p>
    <div class="row">${button('返回高速记忆', 'cardsBack', 'primary')}${button('40 秒闭目复盘', 'resultTimer')}</div></div>`, 'memory');
  const draw = (due) => { $('recallStats').innerHTML = stats(['已自评', `${session.index} 张`], ['想起来了', `${session.right} 张`, 'tone-green-text'], ['剩余到期闪卡', due === null ? '—' : `${due} 张`]); };
  draw(null);
  rpc('memory').then((data) => { if (token === screenId) draw(data.Due); }).catch(toast);
  on('cardsBack', memory);
  on('resultTimer', recallTimer);
}

const timeText = (seconds) => `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
function recallTimer() {
  if (promptDialog.open) return;
  $('promptTitle').textContent = '闭目回忆刚学过的要点';
  $('promptText').innerHTML = '<span class="timer" id="recallClock">00:40</span><br><span id="recallHint">计时结束后，可以补写刚才卡住的地方。</span>';
  $('promptButtons').innerHTML = '<button value="close">结束计时</button>';
  const end = Date.now() + 40000;
  let audio;
  try { audio = new AudioContext(); audio.resume().catch(() => {}); } catch { /* timer also works without audio */ }
  const clock = setInterval(() => {
    const seconds = Math.max(0, Math.ceil((end - Date.now()) / 1000));
    $('recallClock').textContent = timeText(seconds);
    if (seconds) return;
    clearInterval(clock);
    $('recallHint').textContent = '40 秒已到。';
    $('promptButtons').firstElementChild.textContent = '完成复盘';
    if (audio?.state === 'running') {
      const oscillator = audio.createOscillator();
      const volume = audio.createGain(); volume.gain.value = .06;
      oscillator.frequency.value = 660; oscillator.connect(volume).connect(audio.destination);
      oscillator.start(); oscillator.stop(audio.currentTime + .2);
    }
  }, 180);
  promptDialog.addEventListener('close', () => { clearInterval(clock); audio?.close().catch(() => {}); }, { once: true });
  promptDialog.showModal();
}

const noteGroups = [
  ['白纸复盘', [['Recall', '凭记忆写核心要点，可用缩进画出层级'], ['Gaps', '对照资料后补上遗漏与错误']]],
  ['组块与口诀', [['Chunks', '把相关信息归到几个有意义的组'], ['Mnemonic', '首字、谐音或一段带画面的口诀']]],
  ['费曼讲解', [['Explanation', '用简单的话讲给从未学过的人听'], ['StickingPoint', '哪一步讲不清，需要重看资料？'], ['Analogy', '写一个生活中的类比，再讲一遍']]]
];
async function notes(questionId, back) {
  const token = view('notes-load', `<p class="glass wait">正在载入复盘主题…</p>`, 'memory');
  try {
    const list = await rpc('notes');
    if (token !== screenId) return;
    const linked = questionId ? list.find((note) => note.QuestionId === questionId) : list.at(-1);
    renderNotes(list, linked || { QuestionId: questionId, Topic: questions.get(questionId)?.Prompt ?? '' }, back);
  } catch (error) { toast(error); }
}

function renderNotes(list, initial, back) {
  let note = initial;
  const token = view('notes', `<div class="stack">${heading('复盘工作台', button('返回记忆练习', 'noteBack'))}<p class="muted">先合上资料，从记忆写下框架。随后对照原资料，补上盲区。</p>
    <div class="note-grid"><aside class="glass card note-list"><h2>复盘主题</h2><div class="note-list" id="noteList"></div>${button('新建主题', 'newNote')}</aside>
    <section class="glass card form"><label>主题名称<input id="noteTopic" maxlength="160" value="${esc(note.Topic)}"></label>
    <div class="editor-tabs">${noteGroups.map(([title], i) => button(title, `noteTab${i}`)).join('')}</div>
    ${noteGroups.map(([, fields], i) => `<div class="editor-fields" id="noteGroup${i}"${i ? ' hidden' : ''}>${fields.map(([key, label]) =>
      `<label>${esc(label)}<textarea id="note${key}" maxlength="10000" rows="3">${esc(note[key])}</textarea></label>`).join('')}</div>`).join('')}
    <div class="head"><p class="muted" id="noteStatus"></p><div class="row">${button('核对题库答案', 'noteAnswer', '', !note.QuestionId)}${button('保存复盘', 'noteSave', 'primary')}</div></div></section></div></div>`, 'memory');
  const read = () => {
    const result = { Id: note.Id || null, QuestionId: note.QuestionId || null, Topic: $('noteTopic').value.trim() };
    for (const [, fields] of noteGroups) for (const [key] of fields) result[key] = $(`note${key}`).value;
    return result;
  };
  let original = JSON.stringify(read());
  const dirty = () => JSON.stringify(read()) !== original;
  function displayList() {
    $('noteList').innerHTML = list.map((entry, i) => `<button type="button" data-note="${i}" aria-pressed="${String(entry.Id === note.Id)}">${esc(entry.Topic)}</button>`).join('');
  }
  async function save() {
    if ($('noteSave').disabled) return false;
    const changed = read();
    if (!changed.Topic) { $('noteStatus').textContent = '请填写主题名称。'; $('noteTopic').focus(); return false; }
    $('noteSave').disabled = true;
    try {
      const saved = await rpc('save-note', { Note: changed });
      const index = list.findIndex((entry) => entry.Id === saved.Id);
      if (index < 0) list.push(saved); else list[index] = saved;
      note = saved;
      original = JSON.stringify({ ...changed, Id: saved.Id });
      if (token === screenId) { displayList(); $('noteStatus').textContent = dirty() ? '有修改未保存' : '已保存到本机'; }
      return true;
    } catch (error) { toast(error); return false; }
    finally { if (token === screenId) $('noteSave').disabled = false; }
  }
  editor = { dirty, save, title: '保存复盘' };
  displayList();
  let selected = 0;
  function select(index) {
    selected = index;
    noteGroups.forEach((_, i) => {
      $(`noteGroup${i}`).hidden = i !== selected;
      $(`noteTab${i}`).setAttribute('aria-pressed', String(i === selected));
    });
  }
  noteGroups.forEach((_, i) => on(`noteTab${i}`, () => select(i)));
  select(0);
  $('noteList').onclick = (event) => {
    const entry = event.target.closest('[data-note]');
    if (entry) navigate(() => renderNotes(list, list[Number(entry.dataset.note)], back));
  };
  screen.oninput = () => { if (token === screenId) $('noteStatus').textContent = dirty() ? '有修改未保存' : ''; };
  on('noteSave', save);
  on('noteBack', () => navigate(back));
  on('newNote', () => navigate(() => renderNotes(list, {}, back)));
  on('noteAnswer', async () => {
    if (!note.QuestionId) return;
    const q = questions.get(note.QuestionId);
    try {
      const key = await rpc('key', { QuestionId: q.Id });
      if (token === screenId) await ask('题库答案', `${q.Prompt}\n\n${keyText(q, key).join('\n')}`, [['ok', '确定']]);
    } catch (error) { toast(error); }
  });
}

async function palace() {
  const token = view('palace-load', `<p class="glass wait">正在载入记忆宫殿…</p>`, 'memory');
  try { const places = await rpc('palace'); if (token === screenId) palaceEditor(places); }
  catch (error) { toast(error); }
}

function palaceEditor(places) {
  const token = view('palace', `<div class="stack">${heading('记忆宫殿', button('返回高速记忆', 'palaceBack'))}<p class="muted">按熟悉的路线填 10–20 个位置。为每个位置设计一个包含动作、声音或画面的联结。</p>
  <section class="glass card"><h2>回忆路线</h2><div class="palace-row"><span>序</span><strong>熟悉的位置</strong><strong>要记的内容与夸张联结</strong></div>
  ${Array.from({ length: 20 }, (_, i) => `<div class="palace-row"><span>${i + 1}</span><input aria-label="第 ${i + 1} 站位置" id="cue${i}" maxlength="500" value="${esc(places[i]?.Cue)}">
  <textarea aria-label="第 ${i + 1} 站联结" id="scene${i}" rows="1" maxlength="10000">${esc(places[i]?.Scene)}</textarea></div>`).join('')}</section>
  <div class="head"><p class="muted" id="palaceStatus"></p><div class="row">${button('保存路线', 'palaceSave')}${button('沿路线回忆', 'startPalace', 'primary')}</div></div></div>`, 'memory');
  const read = () => Array.from({ length: 20 }, (_, i) => ({ Cue: $(`cue${i}`).value.trim(), Scene: $(`scene${i}`).value.trim() }));
  let original = JSON.stringify(read());
  const dirty = () => JSON.stringify(read()) !== original;
  let saving = false;
  async function save() {
    if (saving) return false;
    const current = read();
    if (current.some((place) => !place.Cue && place.Scene)) { $('palaceStatus').textContent = '请为写过联结的行填写位置。'; return false; }
    saving = true;
    try {
      await rpc('save-palace', { Places: current });
      original = JSON.stringify(current);
      if (token === screenId) $('palaceStatus').textContent = dirty() ? '有修改未保存' : '路线已保存到本机';
      return true;
    } catch (error) { toast(error); return false; }
    finally { saving = false; }
  }
  editor = { dirty, save, title: '保存路线' };
  screen.oninput = () => { if (token === screenId) $('palaceStatus').textContent = dirty() ? '有修改未保存' : ''; };
  on('palaceSave', save);
  on('palaceBack', () => navigate(memory));
  on('startPalace', async () => {
    if (!await save() || token !== screenId) return;
    const route = read().map((place, i) => ({ ...place, slot: i + 1 })).filter((place) => place.Cue && place.Scene);
    if (!route.length) { $('palaceStatus').textContent = '请先填写至少一个位置和对应的联结。'; return; }
    palaceRecall(route);
  });
}

function palaceRecall(route) {
  let index = 0;
  let revealed = false;
  view('palace-exam', `<div class="flash">${heading('沿路线回忆', button('返回编辑路线', 'routeBack'))}<p id="routeProgress" class="muted"></p>
  <section class="glass card flash-body" id="routeBody"><h2 id="routeCue"></h2><p id="routeScene" class="detail-text"></p></section>
  <div class="row">${button('上一站', 'routePrev')}${button('显示联结', 'routeReveal', 'primary')}<span class="grow"></span>${button('下一站', 'routeNext')}</div></div>`, 'memory');
  function draw() {
    const place = route[index];
    $('routeProgress').textContent = `第 ${index + 1} / ${route.length} 站　·　记忆桩 ${place.slot}`;
    $('routeCue').textContent = place.Cue;
    $('routeScene').textContent = revealed ? place.Scene : '闭眼想象站在这里，回忆当时放下的画面和要记的信息。';
    $('routeBody').dataset.phase = revealed ? 'answer' : 'recall';
    $('routePrev').disabled = index === 0;
    $('routeNext').textContent = index + 1 === route.length ? '完成路线' : '下一站';
    $('routeReveal').textContent = revealed ? '隐藏联结' : '显示联结';
  }
  on('routeBack', palace);
  on('routePrev', () => { if (index) { index--; revealed = false; draw(); } });
  on('routeNext', () => { if (++index === route.length) memory(); else { revealed = false; draw(); } });
  on('routeReveal', () => { revealed = !revealed; draw(); });
  draw();
}

const routes = { home, mistakes, history, memory, banks };
document.querySelector('nav').addEventListener('click', (event) => {
  const destination = event.target.closest('[data-nav]')?.dataset.nav;
  if (!switchingBank && destination && (dashboard || destination === 'banks') && destination !== screenName) navigate(routes[destination]);
});
document.addEventListener('keydown', (event) => { if (examKey) examKey(event); });

rpc('boot').then((data) => {
  setWorkspace(data);
  if (dashboard) renderHome(); else banks();
}).catch((error) => {
  screen.removeAttribute('aria-busy');
  screen.innerHTML = `<p class="glass wait error">${esc(error.message)}</p>`;
});
