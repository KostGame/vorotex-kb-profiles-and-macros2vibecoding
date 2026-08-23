(() => {
  'use strict';

  const state = {
    document: null,
    originalText: '',
    filename: '',
    kind: null,
    groups: [],
    bindings: [],
    selectedMacro: null,
    dirty: false,
    expectedExtension: null,
  };

  const el = (id) => document.getElementById(id);
  const fileInput = el('fileInput');

  function decodeUtf16Array(value) {
    if (!Array.isArray(value)) return String(value ?? '');
    return value.map((n) => String.fromCharCode(Number(n) || 0)).join('').replace(/\u0000+$/g, '');
  }

  function encodeUtf16Array(text) {
    return Array.from(String(text), (ch) => ch.charCodeAt(0));
  }

  function detectKind(doc) {
    if (doc && Array.isArray(doc.MacroInfo) && Object.prototype.hasOwnProperty.call(doc, 'GrpGuid')) {
      return 'macro';
    }
    if (doc && doc.KBconfig && Array.isArray(doc.MacroGrpInfo)) {
      return 'profile';
    }
    return null;
  }

  function normalizeGroups(doc, kind) {
    const rawGroups = kind === 'macro' ? [doc] : (doc.MacroGrpInfo || []);
    return rawGroups.map((group, groupIndex) => ({
      raw: group,
      index: groupIndex,
      guid: group.GrpGuid || '',
      name: decodeUtf16Array(group.GrpName),
      macros: (group.MacroInfo || []).map((macro, macroIndex) => ({
        raw: macro,
        group,
        groupIndex,
        index: macroIndex,
        guid: macro.MacroGuid || '',
        name: decodeUtf16Array(macro.MacroName),
      })),
    }));
  }

  function findBindings(doc, kind, groups) {
    if (kind !== 'profile') return [];
    const map = doc?.KBconfig?.KBKeyMacro;
    if (!map || typeof map !== 'object') return [];

    const macroByGuid = new Map();
    for (const group of groups) {
      for (const macro of group.macros) {
        macroByGuid.set(macro.guid, { group, macro });
      }
    }

    return Object.entries(map)
      .filter(([, binding]) => binding && typeof binding === 'object' && binding.macGuid)
      .map(([key, binding]) => {
        const resolved = macroByGuid.get(binding.macGuid);
        return {
          key,
          memMacId: binding.MemMacId,
          grpGuid: binding.grpGuid || '',
          macGuid: binding.macGuid || '',
          groupName: resolved?.group?.name || '',
          macroName: resolved?.macro?.name || '',
        };
      });
  }

  function countMacros(groups) {
    return groups.reduce((sum, group) => sum + group.macros.length, 0);
  }

  function setStatus(message, mode = 'ok') {
    const node = el('statusText');
    node.textContent = message;
    node.className = `status ${mode}`;
  }

  function setDirty(value = true) {
    state.dirty = value;
    el('dirtyBadge').classList.toggle('hidden', !value);
  }

  function syncRawJson() {
    el('rawJson').value = JSON.stringify(state.document, null, 2);
  }

  function applyRawJson() {
    try {
      const next = JSON.parse(el('rawJson').value);
      const kind = detectKind(next);
      if (!kind) throw new Error('Структура не похожа на поддерживаемый экспорт VOROTEX.');
      state.document = next;
      state.kind = kind;
      state.groups = normalizeGroups(next, kind);
      state.bindings = findBindings(next, kind, state.groups);
      state.selectedMacro = null;
      setDirty(true);
      renderAll();
      setStatus('Raw JSON применён.', 'ok');
      return true;
    } catch (error) {
      setStatus(`Ошибка JSON: ${error.message}`, 'error');
      return false;
    }
  }

  function renderAll() {
    el('emptyState').classList.add('hidden');
    el('workspace').classList.remove('hidden');
    el('fileName').textContent = state.filename || 'без имени';
    el('fileType').textContent = state.kind === 'macro' ? '.Macro.Config' : '.KB.Config';
    el('groupCount').textContent = state.groups.length;
    el('macroCount').textContent = countMacros(state.groups);
    el('bindingCount').textContent = state.bindings.length;

    renderGroups();
    renderBindings();
    renderMacroEditor();
    syncRawJson();
  }

  function renderGroups() {
    const root = el('groupList');
    root.innerHTML = '';

    for (const group of state.groups) {
      const wrap = document.createElement('div');
      wrap.className = 'group';

      const header = document.createElement('div');
      header.className = 'group-header';
      header.textContent = `${group.name || '(без имени)'} · ${group.macros.length}`;
      wrap.appendChild(header);

      for (const macro of group.macros) {
        const button = document.createElement('button');
        button.className = 'macro-btn';
        if (state.selectedMacro?.raw === macro.raw) button.classList.add('active');
        button.textContent = macro.name || '(без имени)';
        button.addEventListener('click', () => {
          state.selectedMacro = macro;
          renderGroups();
          renderMacroEditor();
        });
        wrap.appendChild(button);
      }
      root.appendChild(wrap);
    }
  }

  function renderBindings() {
    const panel = el('bindingsPanel');
    const root = el('bindingList');
    root.innerHTML = '';

    if (!state.bindings.length) {
      panel.classList.add('hidden');
      return;
    }

    panel.classList.remove('hidden');
    for (const binding of state.bindings) {
      const row = document.createElement('div');
      row.className = 'binding';
      const key = document.createElement('span');
      key.textContent = binding.key.replace(/^btn_KBKey_/, '');
      const macro = document.createElement('span');
      macro.textContent = binding.macroName || binding.macGuid.slice(0, 8);
      row.append(key, macro);
      root.appendChild(row);
    }
  }

  function eventRows(macro) {
    const data = macro?.raw?.macData;
    if (!data) return [];
    const count = Math.max(0, Math.min(Number(data.num) || 0, 500));
    const rows = [];
    for (let i = 0; i < count; i++) {
      rows.push({
        index: i,
        value: data.macVal?.[i],
        state: data.macSta?.[i],
        delay: data.macDly?.[i],
        ext: Array.isArray(data.extVal?.[i]) ? data.extVal[i].join(', ') : '',
      });
    }
    return rows;
  }

  function renderEventTable(macro) {
    const wrap = el('eventTableWrap');
    const rows = eventRows(macro);
    if (!rows.length) {
      wrap.innerHTML = '<p class="hint">События не найдены.</p>';
      return;
    }

    const table = document.createElement('table');
    table.innerHTML = '<thead><tr><th>#</th><th>macVal</th><th>macSta</th><th>delay</th><th>extVal</th></tr></thead>';
    const body = document.createElement('tbody');
    for (const row of rows) {
      const tr = document.createElement('tr');
      for (const value of [row.index, row.value, row.state, row.delay, row.ext]) {
        const td = document.createElement('td');
        td.textContent = value ?? '';
        tr.appendChild(td);
      }
      body.appendChild(tr);
    }
    table.appendChild(body);
    wrap.replaceChildren(table);
  }

  function renderMacroEditor() {
    const macro = state.selectedMacro;
    const none = el('noMacro');
    const editor = el('macroEditor');

    if (!macro) {
      el('macroTitle').textContent = '—';
      none.classList.remove('hidden');
      editor.classList.add('hidden');
      return;
    }

    none.classList.add('hidden');
    editor.classList.remove('hidden');
    el('macroTitle').textContent = macro.name || '(без имени)';
    el('macroNameInput').value = macro.name;
    el('macroGuidInput').value = macro.guid;
    el('macroEventCount').value = macro.raw?.macData?.num ?? 0;
    el('cycleSelect').value = 'preserve';
    renderEventTable(macro);
  }

  function renameSelectedMacro() {
    const macro = state.selectedMacro;
    if (!macro) return;
    const text = el('macroNameInput').value;
    macro.raw.MacroName = encodeUtf16Array(text);
    macro.name = text;
    setDirty(true);
    renderGroups();
    el('macroTitle').textContent = text || '(без имени)';
    syncRawJson();
    setStatus('Имя макроса изменено.', 'ok');
  }

  function applyCycleOne() {
    const macro = state.selectedMacro;
    if (!macro || el('cycleSelect').value !== 'cycle1') return;
    if (!macro.raw.macData) return;
    macro.raw.macData.macRpt = 1;
    macro.raw.macData.rptType = 0;
    setDirty(true);
    syncRawJson();
    setStatus('Установлено: Цикл = 1 → macRpt=1, rptType=0.', 'ok');
  }

  function validateDocument() {
    if (!applyRawJson()) return;
    const issues = [];

    if (state.kind === 'macro') {
      if (!Array.isArray(state.document.MacroInfo)) issues.push('MacroInfo отсутствует.');
      if (!state.document.GrpGuid) issues.push('GrpGuid пуст.');
    } else {
      if (!state.document.KBconfig) issues.push('KBconfig отсутствует.');
      if (!Array.isArray(state.document.MacroGrpInfo)) issues.push('MacroGrpInfo отсутствует.');
    }

    for (const group of state.groups) {
      if (!group.guid) issues.push(`У группы «${group.name || '?'}» пустой GUID.`);
      for (const macro of group.macros) {
        const data = macro.raw?.macData;
        if (!macro.guid) issues.push(`У макроса «${macro.name || '?'}» пустой GUID.`);
        if (data && Number(data.num) > 500) issues.push(`Макрос «${macro.name}»: num > 500.`);
        if (data && data.macRpt === 1 && data.rptType !== 0) {
          issues.push(`Макрос «${macro.name}»: macRpt=1, но rptType=${data.rptType}; это не подтверждённая сериализация GUI «Цикл = 1».`);
        }
      }
    }

    if (issues.length) {
      setStatus(`Проверка завершена: ${issues.length} замечаний. Первое: ${issues[0]}`, 'warn');
    } else {
      setStatus('Проверка PASS: поддерживаемая структура, критических замечаний нет.', 'ok');
    }
  }

  function downloadDocument() {
    if (!applyRawJson()) return;
    const text = JSON.stringify(state.document, null, 2);
    const blob = new Blob([text], { type: 'application/json;charset=utf-8' });
    const link = document.createElement('a');
    const suffix = state.kind === 'macro' ? '.Macro.Config' : '.KB.Config';
    let name = state.filename || `vorotex-export${suffix}`;
    if (!name.toLowerCase().endsWith(suffix.toLowerCase())) name += suffix;
    const dot = name.toLowerCase().lastIndexOf(suffix.toLowerCase());
    if (dot >= 0) name = `${name.slice(0, dot)}.edited${name.slice(dot)}`;

    link.href = URL.createObjectURL(blob);
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(link.href);
    setStatus(`Экспортирован файл ${name}`, 'ok');
    setDirty(false);
  }

  async function openFile(file) {
    try {
      const text = await file.text();
      const doc = JSON.parse(text.replace(/^\uFEFF/, ''));
      const kind = detectKind(doc);
      if (!kind) throw new Error('Не удалось определить .Macro.Config или .KB.Config.');
      if (state.expectedExtension === 'macro' && kind !== 'macro') {
        throw new Error('Выбран файл профиля, ожидался .Macro.Config.');
      }
      if (state.expectedExtension === 'profile' && kind !== 'profile') {
        throw new Error('Выбран файл макросов, ожидался .KB.Config.');
      }

      state.document = doc;
      state.originalText = text;
      state.filename = file.name;
      state.kind = kind;
      state.groups = normalizeGroups(doc, kind);
      state.bindings = findBindings(doc, kind, state.groups);
      state.selectedMacro = state.groups.flatMap((g) => g.macros)[0] || null;
      setDirty(false);
      renderAll();
      setStatus(`Открыт ${file.name}.`, 'ok');
    } catch (error) {
      setStatus(error.message, 'error');
      alert(`Не удалось открыть файл:\n${error.message}`);
    } finally {
      fileInput.value = '';
    }
  }

  function chooseFile(kind) {
    state.expectedExtension = kind;
    fileInput.accept = kind === 'macro' ? '.Config,.Macro.Config' : '.Config,.KB.Config';
    fileInput.click();
  }

  function closeDocument() {
    state.document = null;
    state.groups = [];
    state.bindings = [];
    state.selectedMacro = null;
    state.filename = '';
    state.kind = null;
    setDirty(false);
    el('workspace').classList.add('hidden');
    el('emptyState').classList.remove('hidden');
  }

  el('openMacroBtn').addEventListener('click', () => chooseFile('macro'));
  el('openProfileBtn').addEventListener('click', () => chooseFile('profile'));
  fileInput.addEventListener('change', () => fileInput.files?.[0] && openFile(fileInput.files[0]));
  el('closeBtn').addEventListener('click', closeDocument);
  el('macroNameInput').addEventListener('change', renameSelectedMacro);
  el('cycleSelect').addEventListener('change', applyCycleOne);
  el('formatBtn').addEventListener('click', () => {
    if (applyRawJson()) syncRawJson();
  });
  el('validateBtn').addEventListener('click', validateDocument);
  el('downloadBtn').addEventListener('click', downloadDocument);
  el('rawJson').addEventListener('input', () => setDirty(true));
})();
