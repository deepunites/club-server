// Экраны «Рабочие станции» (с зонами мест), «Библиотека игр», «Образы Windows», «Бездиск», «Сеть» и «Сохранения». Без сборки и внешних зависимостей: панель работает в клубе без интернета.
(() => {
  "use strict";

  const API = "/panel/api/v1";
  const REFRESH_MS = 5000;
  const $ = (id) => document.getElementById(id);

  let lang = safeGet(localStorage, "panel.lang") || "ru";
  let token = safeGet(sessionStorage, "panel.token");
  let data = null;
  let dataJson = "";
  let timer = null;
  let editing = null;
  let view = "machines";
  let net = null;
  let netJson = "";
  let netSettings = null;
  let formDirty = false;
  let imgs = null;
  let imgsJson = "";
  let reimaging = null;
  let lib = null;
  let libJson = "";
  // Библиотеки сервера (GET /libraries): G: («Online Games») и по ключу — H: («Offline Games»). libKey — открытая на
  // экране «Библиотека игр», запоминается в браузере.
  let libs = [];
  let libKey = safeGet(localStorage, "panel.library") || "games";
  let libPath = "/library"; // путь API библиотеки, чьи данные сейчас на экране (lib): по нему — её кнопки; null — данных нет
  let libSwitchKey = "";
  let dl = null;
  let dlJson = "";
  let zonesKey = "";
  let renamingZone = null;
  // «Сохранения»: состояние диска, папки и страница игроков (svPage, поиск svQuery) — из одного захода loadSaves.
  let svStatus = null;
  let svEntries = null;
  let svPlayers = null;
  let svJson = "";
  let svSeq = 0;
  let svPage = 1;
  let svQuery = "";
  let svQueryTimer = null;
  let svSettingsDirty = false;
  let svEditing = null;
  const openContents = new Set();
  const NET_FIELDS = ["subnet", "interface", "dhcpServer", "gateway", "dnsServers", "poolStart", "poolEnd", "reservedStart", "leaseTimeSec", "keaSubnetId"];

  function safeGet(storage, key) {
    try { return storage.getItem(key); } catch { return null; }
  }

  function safeSet(storage, key, value) {
    try { value === null ? storage.removeItem(key) : storage.setItem(key, value); } catch { /* приватный режим */ }
  }

  function t(key, vars) {
    const text = (window.I18N[lang] && window.I18N[lang][key]) ?? window.I18N.ru[key] ?? key;
    return vars ? text.replace(/\{(\w+)\}/g, (_, name) => vars[name] ?? "") : text;
  }

  function applyStaticTexts() {
    document.documentElement.lang = lang;
    document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
    document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => { el.placeholder = t(el.dataset.i18nPlaceholder); el.setAttribute("aria-label", el.placeholder); });
    $("lang").value = lang;
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props)) {
      if (k === "class") node.className = v;
      else if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
      else node.setAttribute(k, v);
    }
    for (const child of children.flat()) {
      if (child !== null && child !== undefined) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  // Прочерк вместо отсутствующего или невменяемого значения (сервер уже отбросил невозможные).
  const dash = () => el("span", { class: "dash" }, "—");
  const orDash = (value, mono) => (value === null || value === undefined || value === "" ? dash() : el("span", { class: mono ? "mono" : "" }, value));

  async function api(path, options = {}) {
    const response = await fetch(API + path, {
      ...options,
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json", ...(options.headers || {}) },
    });
    if (response.status === 401) {
      logout();
      throw new Error("unauthorized");
    }
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      const error = new Error(body?.error?.message || `HTTP ${response.status}`);
      error.status = response.status;
      error.reason = body?.error?.details?.reason;
      error.details = body?.error?.details;
      throw error;
    }
    return response.status === 204 ? null : response.json();
  }

  // ---------- Диалоги ----------

  // Вопрос и сообщение — свой <dialog> вместо confirm()/alert(): встроенный браузер приложения Claude их не показывает,
  // и кнопки с подтверждением («Отменить» перезаливку) выглядели мёртвыми. Окна — по очереди: сообщение об ошибке
  // одного действия ждёт, пока закроют вопрос другого. Esc и «Отмена» — false; ответ приходит один раз.
  let dialogQueue = Promise.resolve();

  function showDialog(text, okText, danger, withCancel) {
    const shown = dialogQueue.then(() => new Promise((resolve) => {
      const dialog = $("ask");
      const ok = $("ask-ok");
      const back = document.activeElement;
      $("ask-text").textContent = text;
      ok.textContent = okText;
      ok.className = danger ? "danger-solid" : "";
      $("ask-cancel").hidden = !withCancel;
      dialog.returnValue = "";
      dialog.addEventListener("close", () => {
        // Фокус — обратно на кнопку, с которой спросили, если её не перерисовали.
        if (back && back !== document.body && back.isConnected) back.focus();
        resolve(dialog.returnValue === "ok");
      }, { once: true });
      dialog.showModal();
      // Опасное действие не подтверждается случайным Enter: фокус на «Отмена».
      (danger && withCancel ? $("ask-cancel") : ok).focus();
    }));
    dialogQueue = shown;
    return shown;
  }

  // Вопрос «да/нет»: true — подтвердили. okText — подпись кнопки, danger — кнопка красная (стирает, отключает насильно).
  function confirmDialog(text, { okText, danger = false } = {}) {
    return showDialog(text, okText ?? t("ok"), danger, true);
  }

  // Сообщение с одной кнопкой; промис выполняется, когда его закрыли.
  function alertDialog(text) {
    return showDialog(text, t("ok"), false, false);
  }

  // Обработчик кнопки с вопросом: пока идут вопрос и запрос, повторные щелчки (и зажатый Enter на кнопке, куда вернулся
  // фокус) не запускают действие второй раз. Кнопка не выключается, чтобы на неё вернулся фокус после окна.
  function oneAtATime(handler) {
    let running = false;
    return async (event) => {
      if (running) return;
      running = true;
      try {
        await handler(event);
      } finally {
        running = false;
      }
    };
  }

  function agoText(iso) {
    const seconds = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
    return seconds < 60 ? t("agoSeconds", { n: seconds })
      : seconds < 3600 ? t("agoMinutes", { n: Math.floor(seconds / 60) })
      : seconds < 86400 ? t("agoHours", { n: Math.floor(seconds / 3600) })
      : t("agoDays", { n: Math.floor(seconds / 86400) });
  }

  // Относительное время обновляется на месте, без перерисовки: кнопки под курсором не пересоздаются.
  function ago(iso) {
    if (!iso) return dash();
    return el("span", { title: new Date(iso).toLocaleString(lang), "data-ago": iso }, agoText(iso));
  }

  function refreshAgo() {
    document.querySelectorAll("[data-ago]").forEach((node) => { node.textContent = agoText(node.dataset.ago); });
  }

  const STATUS = {
    online: ["ok", "online"],
    offline: ["bad", "offline"],
    neverSeen: ["idle", "neverSeen"],
    pendingApproval: ["warn", "pendingApproval"],
    maintenance: ["info", "maintenanceStatus"],
    reimaging: ["warn", "reimaging"],
  };

  const REIMAGE = {
    requested: "info",
    deploying: "info",
    failed: "bad",
    booting: "info",
    done: "ok",
    cancelled: "idle",
  };

  const VOLUME = {
    none: ["idle", "volNone"],
    mounting: ["info", "volMounting"],
    mounted: ["ok", "volMounted"],
    switchPending: ["warn", "volSwitchPending"],
    failed: ["bad", "volFailed"],
  };

  function badge(kind, text, title) {
    return el("span", { class: `badge ${kind}`, ...(title ? { title } : {}) }, text);
  }

  function volumeCell(volume) {
    const [kind, key] = VOLUME[volume.state] || ["idle", volume.state];
    const badges = [badge(kind, volume.libraryVersion ? `${volume.libraryVersion} · ${t(key)}` : t(key), volume.error || "")];
    if (volume.outdated) badges.push(badge("warn", t("volOutdated")));
    if (volume.personal) badges.push(badge("idle", t("volPersonal"), t("volPersonalHint")));
    else if (volume.state === "mounted" && volume.readOnlyVerified !== true) badges.push(badge("bad", t("volNotVerified")));
    return el("div", { class: "badges" }, badges);
  }

  // Том библиотеки у ПК. Есть вторая библиотека (m.libraries — только тогда) — по строке на букву: «G:» и «H:»; помощник,
  // который её не знает (старше minHelper), диска H: не получает — вместо тома «нужен помощник 1.6.0».
  function libraryCell(m) {
    if (!m.libraries) return el("td", {}, volumeCell(m.volume));
    const games = libs.find((l) => l.key === "games");
    const line = (letter, content) => el("div", { class: "lib-line" }, el("span", { class: "lib-letter mono" }, `${letter}:`), content);
    return el("td", {},
      line(games?.driveLetter ?? "G", volumeCell(m.volume)),
      m.libraries.map((l) => {
        const info = libs.find((x) => x.key === l.key);
        const v = info?.minHelper ?? "1.6.0";
        return line(l.driveLetter, l.supported ? volumeCell(l.volume)
          : el("div", { class: "badges" }, badge("idle", t("needsHelper", { v }), t("needsHelperHint", { name: info?.title ?? l.key, letter: l.driveLetter, v }))));
      }));
  }

  // Отметка мастер-тома на запись у ПК: буква — из настройки сервера (MasterDriveLetter библиотеки: у G: M или G, у H: — H);
  // причина — во всплывающей подсказке.
  function masterBadge(state, error, letter) {
    const hint = error ? `${t(`mm_${state}`)}: ${error}` : t(`mm_${state}`);
    return el("div", {}, badge(state === "failed" ? "bad" : "warn", t("masterBadge", { letter }), hint));
  }

  // Версия образа на ПК и ход перезаливки: этап, проценты, причина сбоя.
  function windowsCell(m) {
    const r = m.reimage;
    const parts = [m.bootMode === "diskless" ? badge("info", t("bootDiskless"), t("bootDisklessHint")) : m.imageVersion ? el("span", { class: "mono" }, m.imageVersion) : dash()];
    if (m.masterState) parts.push(masterBadge(m.masterState, m.masterError, data?.masterDriveLetter ?? "M"));
    for (const l of m.libraries ?? []) {
      if (l.masterState) parts.push(masterBadge(l.masterState, l.masterError, data?.libraries?.find((x) => x.key === l.key)?.masterDriveLetter ?? l.driveLetter));
    }
    const sb = m.secureBoot;
    if (sb && sb.enabled !== null && sb.enabled !== undefined) {
      const title = `db: CA 2011 ${sb.thirdPartyCa2011 ? "✓" : "✗"}, CA 2023 ${sb.windowsCa2023 ? "✓" : "✗"}; dbx: PCA 2011 ${sb.pca2011Revoked ? "✗" : "—"}`;
      const sbBadges = [badge(sb.enabled ? "ok" : "idle", t(sb.enabled ? "sbOn" : "sbOff"), title)];
      if (sb.enabled && sb.thirdPartyCa2011 === false) sbBadges.push(badge("bad", t("sbNoCa2011"), title));
      if (sb.enabled && sb.pca2011Revoked) sbBadges.push(badge("info", t("sbRevoked"), title));
      parts.push(el("div", { class: "badges" }, sbBadges));
    }
    if (r) {
      let text = t(`rs_${r.state}`);
      if (r.state === "deploying" && r.step) text += ` · ${t(`st_${r.step}`)}${r.percent !== null && r.percent !== undefined ? ` ${r.percent}%` : ""}`;
      if (r.state === "failed" && r.failure) {
        const key = `fr_${r.failure}`;
        text += ` · ${t(key) === key ? t(`st_${r.failure}`) : t(key)}`;
      }
      if (r.attempts > 1) text += ` (#${r.attempts})`;
      parts.push(el("div", {}, badge(REIMAGE[r.state] || "idle", text, r.message || "")));
      if (r.state === "deploying" && r.step === "download" && r.percent !== null && r.percent !== undefined) {
        parts.push(el("div", { class: "progress" }, el("span", { style: `width:${r.percent}%` })));
      }
    }
    return el("td", { class: "reimage-cell" }, parts);
  }

  function reimageActions(m) {
    const r = m.reimage;
    const active = r && ["requested", "deploying", "failed", "booting"].includes(r.state);
    const buttons = [el("button", { class: "ghost small", onclick: () => openEdit(m) }, t("edit"))];
    const toDiskless = m.bootMode !== "diskless";
    buttons.push(el("button", { class: "ghost small", title: t(toDiskless ? "bootToDisklessHint" : "bootToLocalHint"), onclick: oneAtATime(async () => {
      if (!(await confirmDialog(t(toDiskless ? "bootToDisklessConfirm" : "bootToLocalConfirm", { name: m.name }), { okText: t(toDiskless ? "bootToDiskless" : "bootToLocal") }))) return;
      try {
        await api(`/machines/${m.id}/boot-mode`, { method: "PUT", body: JSON.stringify({ mode: toDiskless ? "diskless" : "local" }) });
      } catch (error) {
        await alertDialog(error.message);
      }
      dataJson = "";
      await load();
    }) }, t(toDiskless ? "bootToDiskless" : "bootToLocal")));
    if (r && ["requested", "failed"].includes(r.state) && !r.diskTouched) {
      buttons.push(el("button", { class: "danger small", onclick: oneAtATime(async () => {
        if (await confirmDialog(t("reimageConfirmCancel", { name: m.name }), { okText: t("reimageCancelYes") })) await act(`/machines/${m.id}/reimage/cancel`);
      }) }, t("reimageCancel")));
    }
    if (m.bootMode !== "diskless" && (!active || r.state === "booting" || r.state === "failed")) {
      buttons.push(el("button", { class: "ghost small", onclick: () => openReimage(m) }, t("reimage")));
    }
    return el("td", {}, el("div", { class: "row-actions" }, buttons));
  }

  function zoneName(id) {
    return data?.zones.find((z) => z.id === id)?.name ?? id;
  }

  function renderSummary() {
    const chip = (value, key) => el("span", { class: "chip" }, el("b", {}, value), " ", t(key));
    const chips = [chip(data.online, "sumOnline"), chip(data.offline, "sumOffline")];
    if (data.pending) chips.push(chip(data.pending, "sumPending"));
    if (data.maintenance) chips.push(chip(data.maintenance, "sumMaintenance"));
    if (data.reimaging) chips.push(chip(data.reimaging, "sumReimaging"));
    // Со второй библиотекой — версия каждой под её названием («Online Games», «Offline Games»), без неё — как раньше.
    const libChip = (title, version) => el("span", { class: "chip" }, `${title}: `, version ? el("b", {}, version) : dash());
    if (data.libraries) {
      chips.push(libChip(libs.find((l) => l.key === "games")?.title ?? t("sumLibrary"), data.currentLibraryVersion));
      for (const l of data.libraries) chips.push(libChip(l.title, l.currentVersion));
    } else {
      chips.push(libChip(t("sumLibrary"), data.currentLibraryVersion));
    }
    chips.push(el("span", { class: "chip" }, `${t("sumImage")}: `, data.currentImageVersion ? el("b", {}, data.currentImageVersion) : dash()));
    const feed = data.machineFeed;
    if (feed) {
      const vars = { host: feed.target, n: feed.machines };
      chips.push(el("span", { class: "chip" }, feed.state === "failing"
        ? [badge("bad", t("feedFailing", vars), feed.error), " ", ago(feed.lastAttemptAt)]
        : feed.state === "pending" ? badge("idle", t("feedPending", vars))
        : [badge("ok", t("feedOk", vars)), " ", ago(feed.lastDeliveredAt)]));
    }
    $("summary").replaceChildren(...chips);
  }

  function renderPending() {
    const pending = data.machines.filter((m) => m.status === "pendingApproval");
    $("pending").hidden = pending.length === 0;
    $("pending-list").replaceChildren(...pending.map((m) => el("div", { class: "pending-item" },
      el("div", {}, el("b", {}, m.hostname), " · ", el("span", { class: "muted" }, `№ ${m.number}`)),
      el("div", { class: "mono muted" }, m.macAddresses.join(", ")),
      el("div", { class: "muted" }, m.ipAddress ? `IP ${m.ipAddress} · ` : "", t("registered"), " ", ago(m.registeredAt)),
      el("div", { class: "actions" },
        el("button", { onclick: () => act(`/machines/${m.id}/approve`) }, t("approve")),
        el("button", { class: "danger", onclick: oneAtATime(async () => {
          if (await confirmDialog(t("rejectConfirm", { name: m.hostname }), { okText: t("reject"), danger: true })) await act(`/machines/${m.id}/reject`);
        }) }, t("reject"))))));
  }

  function renderRows() {
    const machines = data.machines.filter((m) => m.status !== "pendingApproval");
    $("empty").hidden = machines.length > 0;
    $("rows").replaceChildren(...machines.map((m) => {
      const [kind, key] = STATUS[m.status] || ["idle", m.status];
      return el("tr", {},
        el("td", { class: "num" }, m.number),
        el("td", {}, m.name, m.hostname && m.hostname !== m.name ? el("div", { class: "muted" }, m.hostname) : null),
        el("td", {}, badge(kind, t(key))),
        el("td", { class: "mono" }, orDash(m.ipAddress, true)),
        el("td", { class: "mono" }, m.macAddresses.length ? m.macAddresses.join(", ") : dash()),
        el("td", {}, zoneName(m.zone)),
        libraryCell(m),
        windowsCell(m),
        el("td", {}, orDash(m.helperVersion, true)),
        el("td", {}, ago(m.lastSeenAt)),
        reimageActions(m));
    }));
  }

  // ---------- Зоны ----------

  // Список зон и выбор зоны для «Назначить места» перестраиваются, только когда изменились сами зоны (или язык):
  // обновление раз в 5 с не сбрасывает выбранную зону и не пересоздаёт кнопки под курсором.
  function renderZones() {
    const key = lang + JSON.stringify(data.zones);
    if (key === zonesKey) return;
    zonesKey = key;
    $("zone-rows").replaceChildren(...data.zones.map((z) => {
      // Зону по умолчанию и непустую удалить нельзя: кнопка выключена, причина — во всплывающей подсказке (и в тексте карточки).
      const blocked = z.isDefault ? t("zoneDeleteDefault") : z.machines > 0 ? t("zoneDeleteInUse", { n: z.machines }) : "";
      const remove = el("button", { class: "danger small", onclick: oneAtATime(() => deleteZone(z)), ...(blocked ? { disabled: "" } : {}) }, t("zoneDelete"));
      return el("tr", {},
        el("td", {}, z.name, z.isDefault ? [" ", badge("idle", t("zoneDefault"), t("zoneDefaultHint"))] : null),
        el("td", { class: "num" }, z.machines),
        el("td", {}, el("div", { class: "row-actions" },
          el("button", { class: "ghost small", onclick: () => openZoneRename(z) }, t("zoneRename")),
          blocked ? el("span", { title: blocked }, remove) : remove)));
    }));
    fillZoneSelect($("zone-assign-zone"), $("zone-assign-zone").value);
  }

  // Выбор зоны (в «Назначить места» и в окне «Изменить»): выбранной остаётся picked, если такая зона ещё есть.
  function fillZoneSelect(select, picked) {
    select.replaceChildren(...data.zones.map((z) => el("option", { value: z.id }, z.name)));
    if (data.zones.some((z) => z.id === picked)) select.value = picked;
  }

  // Текст ошибки API зон: причина из ответа сервера — переводом, 404 — «зоны уже нет».
  function zoneError(error) {
    if (error.status === 404) return t("zoneGone");
    const key = `zoneErr_${error.reason}`;
    return t(key) !== key ? t(key, { n: error.details?.machines ?? "" }) : error.message;
  }

  const MAX_SEATS = 1000;

  // Места «11-20, 25» → [11…20, 25]: номера и диапазоны через запятую, точку с запятой или пробел; тире любое,
  // пробелы вокруг тире можно. Ошибка — { error } с текстом для оператора.
  function parseSeats(text) {
    const parts = text.replace(/\s*[-–—]\s*/g, "-").split(/[\s,;]+/).filter(Boolean);
    if (!parts.length) return { error: t("zoneSeatsEmpty") };
    const numbers = new Set();
    for (const part of parts) {
      const match = /^(\d+)(?:-(\d+))?$/.exec(part);
      if (!match) return { error: t("zoneSeatsBad", { part }) };
      const from = Number(match[1]);
      const to = match[2] === undefined ? from : Number(match[2]);
      if (from < 1 || to > 9999) return { error: t("zoneSeatsRange", { part }) };
      if (from > to) return { error: t("zoneSeatsReversed", { part }) };
      for (let n = from; n <= to; n++) {
        numbers.add(n);
        if (numbers.size > MAX_SEATS) return { error: t("zoneSeatsTooMany", { max: MAX_SEATS }) };
      }
    }
    return { numbers: [...numbers].sort((a, b) => a - b) };
  }

  // [7, 8, 9, 12] → «7-9, 12».
  function formatSeats(numbers) {
    const ranges = [];
    for (const n of numbers) {
      const last = ranges[ranges.length - 1];
      if (last && n === last[1] + 1) last[1] = n;
      else ranges.push([n, n]);
    }
    return ranges.map(([a, b]) => (a === b ? `${a}` : `${a}-${b}`)).join(", ");
  }

  async function addZone(event) {
    event.preventDefault();
    const result = $("zone-add-result");
    let created = null;
    $("zone-add-button").disabled = true;
    try {
      created = await api("/zones", { method: "POST", body: JSON.stringify({ name: $("zone-add-name").value.trim() }) });
      result.className = "small ok-text";
      result.textContent = t("zoneAdded", { name: created.name });
      $("zone-add-name").value = "";
    } catch (error) {
      result.className = "error small";
      result.textContent = zoneError(error);
    } finally {
      $("zone-add-button").disabled = false;
    }
    result.hidden = false;
    dataJson = "";
    await load();
    // Новую зону — сразу в «Назначить места».
    if (created && data?.zones.some((z) => z.id === created.id)) $("zone-assign-zone").value = created.id;
  }

  async function assignSeats(event) {
    event.preventDefault();
    const result = $("zone-assign-result");
    const zoneId = $("zone-assign-zone").value;
    const parsed = parseSeats($("zone-assign-seats").value);
    result.hidden = false;
    if (parsed.error) {
      result.className = "error small";
      result.textContent = parsed.error;
      return;
    }
    $("zone-assign-button").disabled = true;
    try {
      const done = await api(`/zones/${encodeURIComponent(zoneId)}/machines`, { method: "POST", body: JSON.stringify({ numbers: parsed.numbers }) });
      const text = [t("zoneAssigned", { zone: zoneName(zoneId), n: done.assigned })];
      if (done.unknown.length) text.push(t("zoneUnknownSeats", { seats: formatSeats(done.unknown) }));
      result.className = done.unknown.length ? "small warn-text" : "small ok-text";
      result.textContent = text.join(" ");
      if (!done.unknown.length) $("zone-assign-seats").value = "";
    } catch (error) {
      result.className = "error small";
      result.textContent = zoneError(error);
    } finally {
      $("zone-assign-button").disabled = false;
    }
    dataJson = "";
    await load();
  }

  function openZoneRename(zone) {
    renamingZone = zone;
    $("zone-rename-name").value = zone.name;
    $("zone-rename-error").hidden = true;
    $("zone-rename").showModal();
    $("zone-rename-name").select();
  }

  async function saveZoneRename(event) {
    event.preventDefault();
    const name = $("zone-rename-name").value.trim();
    if (name === renamingZone.name) {
      $("zone-rename").close();
      return;
    }
    $("zone-rename-save").disabled = true;
    try {
      await api(`/zones/${encodeURIComponent(renamingZone.id)}`, { method: "PATCH", body: JSON.stringify({ name }) });
      $("zone-rename").close();
      dataJson = "";
      await load();
    } catch (error) {
      $("zone-rename-error").textContent = zoneError(error);
      $("zone-rename-error").hidden = false;
      // Зону удалили или название заняли в другой вкладке: список за окном — свежий («список обновлён» в тексте ошибки),
      // обновление раз в 5 с на паузе, пока окно открыто.
      dataJson = "";
      await load();
    } finally {
      $("zone-rename-save").disabled = false;
    }
  }

  async function deleteZone(zone) {
    if (!(await confirmDialog(t("zoneDeleteConfirm", { name: zone.name }), { okText: t("zoneDelete"), danger: true }))) return;
    try {
      await api(`/zones/${encodeURIComponent(zone.id)}`, { method: "DELETE" });
    } catch (error) {
      await alertDialog(zoneError(error));
    }
    dataJson = "";
    await load();
  }

  function render() {
    if (!data) return;
    renderSummary();
    renderPending();
    renderZones();
    renderRows();
  }

  // ---------- Сеть ----------

  // Красная плашка на любом экране: чужой DHCP раздаёт адреса в сети клуба.
  function renderDhcpAlert() {
    const foreign = net?.foreignDhcp ?? [];
    $("dhcp-alert").hidden = foreign.length === 0;
    $("dhcp-alert").replaceChildren(...foreign.map((f) => el("p", {}, t("dhcpAlert", { server: f.server, machines: f.seenBy.join(", ") }))));
  }

  function warningText(w) {
    const key = `warn_${w.kind}`;
    const text = t(key, { subject: w.subject });
    return text === key ? w.message : text;
  }

  function renderNetSummary() {
    const chips = [];
    const chip = (kind, text, title) => chips.push(el("span", { class: "chip" }, badge(kind, text, title)));
    if (!net.keaEnabled) chip("idle", t("keaOff"));
    else if (!net.sync) chip("idle", t("keaNever"));
    else if (net.sync.error) chip("bad", t("keaUnavailable"), net.sync.error);
    else if (!net.sync.schemaOk) chip("bad", t("keaSchemaBad", { v: net.sync.schemaVersion ?? "—" }));
    else chip("ok", t("keaSchema", { v: net.sync.schemaVersion }));
    if (net.sync?.at) chips.push(el("span", { class: "chip" }, `${t("keaSynced")}: `, ago(net.sync.at)));
    $("net-summary").replaceChildren(...chips);
  }

  function renderNetwork() {
    if (!net) return;
    renderDhcpAlert();
    if (view !== "network") return;
    renderNetSummary();
    $("net-unconfigured").hidden = net.configured;
    $("net-warnings").hidden = net.warnings.length === 0;
    $("net-warning-list").replaceChildren(...net.warnings.map((w) => el("li", { title: w.message }, warningText(w), " · ", ago(w.lastSeen))));
    $("net-rows").replaceChildren(...net.reservations.map((r) => el("tr", {},
      el("td", { class: "num" }, r.seat),
      el("td", {}, r.name),
      el("td", { class: "mono" }, orDash(r.mac, true)),
      el("td", { class: "mono" }, orDash(r.ip, true)),
      el("td", { class: "mono" }, orDash(r.hostname, true)),
      el("td", {}, r.problem ? badge("warn", t(`problem_${r.problem}`)) : dash()))));
  }

  function renderCapacity() {
    if (!netSettings) return;
    $("net-capacity").textContent = t("netCapacity", { n: netSettings.seatCapacity, start: netSettings.reservedStart });
  }

  function fillForm(s) {
    const form = $("net-form");
    for (const name of NET_FIELDS) {
      const value = name === "dnsServers" ? s.dnsServers.join(", ") : name === "leaseTimeSec" ? Math.round(s.leaseTimeSec / 60) : s[name];
      form.elements[name].value = value;
      form.elements[name].classList.remove("invalid");
    }
    form.querySelectorAll(".field-error").forEach((node) => node.remove());
    $("net-interfaces").replaceChildren(...(s.serverInterfaces ?? []).map((name) => el("option", { value: name })));
    formDirty = false;
    renderCapacity();
  }

  async function loadSettings(force) {
    if (formDirty && !force) return;
    try {
      netSettings = await api("/network/settings");
      fillForm(netSettings);
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("net-error").textContent = t("loadFailed", { error: error.message });
      $("net-error").hidden = false;
    }
  }

  async function loadNetwork() {
    try {
      const fresh = await api("/network/status");
      const json = JSON.stringify(fresh);
      $("net-error").hidden = true;
      if (json !== netJson) {
        net = fresh;
        netJson = json;
        renderNetwork();
      }
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("net-error").textContent = t("loadFailed", { error: error.message });
      $("net-error").hidden = false;
    }
  }

  async function saveNetwork(event) {
    event.preventDefault();
    const form = $("net-form");
    const value = (name) => form.elements[name].value.trim();
    const body = {
      subnet: value("subnet"),
      interface: value("interface"),
      dhcpServer: value("dhcpServer"),
      gateway: value("gateway"),
      dnsServers: value("dnsServers").split(/[\s,;]+/).filter(Boolean),
      poolStart: value("poolStart"),
      poolEnd: value("poolEnd"),
      reservedStart: value("reservedStart"),
      leaseTimeSec: Math.round(Number(value("leaseTimeSec")) * 60),
      keaSubnetId: Number(value("keaSubnetId")),
    };
    form.querySelectorAll(".field-error").forEach((node) => node.remove());
    form.querySelectorAll("input.invalid").forEach((node) => node.classList.remove("invalid"));
    $("net-form-error").hidden = true;
    $("net-saved").hidden = true;
    $("net-save").disabled = true;
    try {
      netSettings = await api("/network/settings", { method: "PUT", body: JSON.stringify(body) });
      fillForm(netSettings);
      $("net-saved").hidden = false;
      netJson = "";
      await loadNetwork();
    } catch (error) {
      const errors = error.details?.errors ?? (error.details?.field ? [error.details] : []);
      for (const e of errors) {
        const input = form.elements[e.field];
        if (!input) continue;
        input.classList.add("invalid");
        input.after(el("span", { class: "field-error" }, t(`reason_${e.reason}`)));
      }
      if (errors.length === 0) {
        $("net-form-error").textContent = error.message;
        $("net-form-error").hidden = false;
      }
    } finally {
      $("net-save").disabled = false;
    }
  }

  async function downloadConfig() {
    try {
      const response = await fetch(API + "/network/kea-dhcp4.conf", { headers: { Authorization: `Bearer ${token}` } });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.error?.details?.reason === "notConfigured" ? t("netUnconfigured") : body?.error?.message || `HTTP ${response.status}`);
      }
      const url = URL.createObjectURL(await response.blob());
      const link = el("a", { href: url, download: "kea-dhcp4.conf" });
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) {
      $("net-form-error").textContent = error.message;
      $("net-form-error").hidden = false;
    }
  }

  // ---------- Перезаливка ----------

  function problemText(error) {
    const key = `re_${error.reason}`;
    return error.reason && t(key) !== key ? t(key) : error.message;
  }

  async function openReimage(machine) {
    reimaging = machine;
    $("reimage-name").textContent = machine.name;
    $("reimage-new-disk").checked = false;
    $("reimage-error").hidden = true;
    let overview = imgs;
    try { overview = await api("/images"); } catch { /* покажем то, что есть */ }
    const options = [];
    if (overview?.current) options.push(el("option", { value: overview.current }, `${overview.current} (${t("imgCurrent")})`));
    if (overview?.rollback) options.push(el("option", { value: overview.rollback }, `${overview.rollback} (${t("imgRollback")})`));
    $("reimage-image").replaceChildren(...options);
    $("reimage-start").disabled = options.length === 0;
    if (options.length === 0) {
      $("reimage-error").textContent = t("re_noImage");
      $("reimage-error").hidden = false;
    }
    $("reimage").showModal();
  }

  async function startReimage(event) {
    event.preventDefault();
    try {
      await api(`/machines/${reimaging.id}/reimage`, {
        method: "POST",
        body: JSON.stringify({ image: $("reimage-image").value, allowNewDisk: $("reimage-new-disk").checked }),
      });
      $("reimage").close();
      dataJson = "";
      await load();
    } catch (error) {
      $("reimage-error").textContent = problemText(error);
      $("reimage-error").hidden = false;
    }
  }

  // ---------- Библиотека игр ----------

  const LIB_STATE = { publishing: "info", published: "ok", retiring: "idle", retired: "idle", failed: "bad" };
  const OP_STATUS = { pending: "idle", running: "info", done: "ok", failed: "bad" };
  const PUBLISH_STEPS = ["snapshot", "clone", "target", "extent", "lun", "verify", "promote"];
  const MASTER_OPEN_STEPS = ["auth", "initiator", "extent", "target", "verify"];
  const MASTER_CLOSE_STEPS = ["target", "extent", "initiator", "auth"];

  // Работающие библиотеки и открытая на экране. Нет списка (не загрузился) — только G: по прежнему пути /library.
  const activeLibs = () => libs.filter((l) => l.enabled);
  const currentLib = () => activeLibs().find((l) => l.key === libKey) ?? null;
  const libBase = () => currentLib()?.path ?? "/library";
  // Название на переключателе и в вопросах: «Offline Games (H:)» — из настройки сервера (Library:Title,
  // Libraries:<ключ>:Title), на всех языках панели одинаковое.
  const libName = (l) => `${l.title} (${l.driveLetter}:)`;

  async function loadLibraries() {
    try {
      libs = await api("/libraries");
    } catch (error) {
      // Сбой связи — прежний список: открытый экран не перескакивает на G: (ошибку покажет запрос самой библиотеки).
      if (error.message === "unauthorized") throw error;
      if (!libs.length) return;
    }
    // Выбранную выключили на сервере (или её нет) — экран G:, с чистого листа (метка, набранная для той, G: не достаётся).
    if (!currentLib() && libKey !== "games") {
      libKey = "games";
      resetLibrary();
    }
  }

  // Экран библиотеки, чьих данных ещё нет (переключили, выбранную выключили на сервере): ни версий, ни кнопок прежней — иначе
  // «Закончить правку», «Открыть для правки» или «Повторить» ушли бы по её пути (libPath), пока новая грузится или если
  // загрузка не удалась. Данные, раскрытый состав и метка — тоже чужие: всё заново.
  function resetLibrary() {
    lib = null;
    libJson = "";
    libPath = null;
    openContents.clear();
    $("lib-label").value = "";
    $("lib-label").dataset.auto = "1";
    $("lib-publish-error").hidden = true;
    $("lib-publish-button").disabled = true;
    $("lib-error").hidden = true;
    $("lib-disabled").hidden = true;
    $("lib-warnings").hidden = true;
    $("lib-ops").hidden = true;
    $("lib-empty").hidden = true;
    $("lib-master").parentElement.classList.remove("open");
    for (const id of ["lib-summary", "lib-warning-list", "lib-adoption", "lib-master", "lib-op-list", "lib-rows", "lib-history"]) $(id).replaceChildren();
  }

  // Переключатель «Online Games (G:) / Offline Games (H:)» — только когда работающих библиотек больше одной.
  // Перерисовывается, только когда изменились библиотеки, выбор или язык: кнопка под курсором не пересоздаётся.
  function renderLibSwitch() {
    const active = activeLibs();
    const key = lang + libKey + JSON.stringify(active);
    if (key === libSwitchKey) return;
    libSwitchKey = key;
    const box = $("lib-switch");
    box.hidden = active.length < 2;
    box.setAttribute("aria-label", t("libSwitch"));
    box.replaceChildren(...(active.length < 2 ? [] : active.map((l) => el("button", {
      type: "button",
      class: l.key === libKey ? "lib-seg active" : "lib-seg",
      "aria-pressed": String(l.key === libKey),
      onclick: () => switchLibrary(l.key),
    }, libName(l)))));
  }

  function switchLibrary(key) {
    if (key === libKey) return;
    libKey = key;
    safeSet(localStorage, "panel.library", key === "games" ? null : key);
    resetLibrary();
    renderLibSwitch();
    loadLibrary();
  }

  // Библиотека, чьи данные на экране (lib), и вопрос перед действием с ней — с её названием первой строкой, когда
  // работающих библиотек больше одной.
  const shownLib = () => libs.find((l) => l.path === libPath) ?? null;

  function libQuestion(text) {
    const shown = shownLib();
    return activeLibs().length > 1 && shown ? `${libName(shown)}\n${text}` : text;
  }

  function suggestLabel() {
    const d = new Date();
    const base = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    const taken = new Set((lib?.versions ?? []).map((v) => v.label));
    if (!taken.has(base)) return base;
    for (let i = 2; ; i++) if (!taken.has(`${base}-${i}`)) return `${base}-${i}`;
  }

  function renderAdoption() {
    const a = lib.machines;
    if (!a || a.online === 0) {
      $("lib-adoption").replaceChildren(el("p", { class: "muted" }, t("libNoOnline")));
      return;
    }
    const parts = [
      ["onCurrent", "seg-ok"], ["onOlder", "seg-info"], ["switchPending", "seg-warn"], ["failed", "seg-bad"], ["notMounted", ""],
    ].filter(([key]) => a[key] > 0);
    $("lib-adoption").replaceChildren(
      el("div", { class: "muted small" }, t("libOnlineOf", { n: a.online })),
      el("div", { class: "adoption-bar" }, parts.map(([key, cls]) => el("span", { class: cls, style: `width:${(100 * a[key]) / a.online}%`, title: `${t(`ad_${key}`)}: ${a[key]}` }))),
      el("div", { class: "legend" }, parts.map(([key, cls]) => el("span", {}, el("i", { class: cls || "seg-idle", style: cls ? "" : "background:var(--idle-bg)" }), `${t(`ad_${key}`)}: `, el("b", {}, a[key])))));
  }

  function renderOperation(op) {
    const steps = { publish: PUBLISH_STEPS, masterOpen: MASTER_OPEN_STEPS, masterClose: MASTER_CLOSE_STEPS }[op.kind] ?? ["promote"];
    const at = op.status === "done" ? steps.length : Math.max(0, steps.indexOf(op.step ?? steps[0]));
    return el("div", { class: "op" },
      el("div", { class: "op-head" },
        el("div", {}, el("b", {}, t(`op_${op.kind}`)), op.versionLabel ? el("span", { class: "mono" }, ` ${op.versionLabel}`) : null, " ",
          badge(OP_STATUS[op.status] || "idle", t(`os_${op.status}`)), op.attempts > 1 ? el("span", { class: "muted small" }, ` #${op.attempts}`) : null),
        op.status === "failed" ? el("button", { class: "small", onclick: () => libAct(`${libPath}/operations/${op.id}/retry`) }, t("libRetry")) : null),
      el("div", { class: "steps" }, steps.map((step, i) => el("span", {
        class: `step ${i < at ? "done" : i === at ? (op.status === "failed" ? "failed" : "active") : ""}`,
      }, t(`step_${step}`)))),
      op.lastError ? el("div", { class: "error small" }, op.lastError) : null);
  }

  const MASTER_STATE = { closed: "idle", opening: "info", open: "warn", closing: "info", failed: "bad" };
  const MASTER_MACHINE = { mounted: "ok", mounting: "info", failed: "bad", none: "idle" };

  function renderMaster() {
    const m = lib.master;
    const box = $("lib-master");
    box.parentElement.classList.toggle("open", !!m && m.state !== "closed");
    if (!m) {
      box.replaceChildren();
      $("lib-publish-button").disabled = false; // после resetLibrary кнопка выключена до данных библиотеки
      return;
    }
    const parts = [];
    const head = [badge(MASTER_STATE[m.state] || "idle", t(`ms_${m.state}`))];
    if (m.machineName && m.state !== "closed") head.push(el("span", {}, " ", t("masterOnPc", { name: m.machineName, letter: m.driveLetter })));
    if (m.state === "open") {
      head.push(" ", badge(MASTER_MACHINE[m.machineState ?? "none"] || "idle", t(`mm_${m.machineState ?? "none"}`)));
      if (m.openedAt) head.push(el("span", { class: "muted small" }, ` · ${t("masterSince")} `, ago(m.openedAt)));
    }
    parts.push(el("div", { class: "master-row" }, head));
    if (m.state === "closing") parts.push(el("div", { class: "muted small" }, t("masterWaiting")));
    // Причина из отчёта помощника (буква занята томом библиотеки, открыты файлы, CHAP) — то, что администратору делать дальше.
    if (m.machineError && m.state !== "closed") parts.push(el("div", { class: "error small" }, m.machineError));
    // У открытого тома lastError — оговорка открытия (таргет не виден ПК), а не ошибка.
    if (m.lastError) parts.push(el("div", { class: m.state === "open" ? "notice small" : "error small" }, m.lastError));
    if (m.dirty && m.state === "closed") parts.push(el("p", { class: "notice small" }, t("masterDirty")));

    const actions = [];
    if (m.state === "closed") {
      const candidates = lib.masterCandidates ?? [];
      if (candidates.length === 0) {
        parts.push(el("p", { class: "muted small" }, t("masterNoCandidates")));
      } else {
        // supported: false (только у H:) — помощник ПК эту библиотеку не знает (409 helperTooOld); reporting: false — знает,
        // но её цикл на ПК не идёт (helper.json "Libraries": [], стоит; 409 libraryNotReported). Отметка — прямо в списке.
        const v = shownLib()?.minHelper ?? "1.6.0";
        const letter = shownLib()?.driveLetter ?? "H";
        const mark = (c) => c.supported === false ? ` · ${t("needsHelper", { v })}` : c.reporting === false ? ` · ${t("noLibraryReports", { letter })}` : "";
        const select = el("select", { "aria-label": t("masterPc") }, candidates.map((c) =>
          el("option", { value: c.id }, `${c.name}${c.online ? ` · ${t("online")}` : ""}${mark(c)}`)));
        actions.push(select, el("button", { onclick: () => libAct(`${libPath}/master/open`, { machineId: select.value }) }, t("masterOpen")));
      }
    }
    if (m.state === "open") actions.push(el("button", { onclick: () => libAct(`${libPath}/master/close`, {}) }, t("masterFinish")));
    if (m.state === "failed") actions.push(el("button", { onclick: () => libAct(`${libPath}/master/close`, {}) }, t("masterCleanup")));
    if ((m.state === "open" || m.state === "closing") && !m.forceClose) {
      actions.push(el("button", { class: "danger", onclick: oneAtATime(async () => {
        if (await confirmDialog(libQuestion(t("masterForceConfirm")), { okText: t("masterForce"), danger: true })) await libAct(`${libPath}/master/close`, { force: true });
      }) }, t("masterForce")));
    }
    if (actions.length) parts.push(el("div", { class: "master-row" }, actions));
    box.replaceChildren(...parts);

    const blocked = m.state !== "closed";
    $("lib-publish-button").disabled = blocked;
    $("lib-publish-button").title = blocked ? t("masterBlocksPublish") : "";
  }

  function contentsCell(v) {
    if (!v.contents) return el("td", { class: "muted small", title: t("libContentsHint") }, v.state === "published" ? t("libNoContents") : "");
    const details = el("details", { title: t("libContentsHint") },
      el("summary", {}, t("libFolders", { n: v.contents.length })),
      el("div", { class: "folders" }, v.contents.map((name) => el("span", { class: "folder" }, name))));
    details.open = openContents.has(v.label);
    details.addEventListener("toggle", () => { if (details.open) openContents.add(v.label); else openContents.delete(v.label); });
    return el("td", { class: "wrap" }, details);
  }

  function renderLibrary(history) {
    if (!lib || view !== "library") return;
    $("lib-disabled").hidden = lib.storageEnabled;

    const chips = [];
    const chip = (key, label) => chips.push(el("span", { class: "chip" }, `${t(key)}: `, label ? el("b", {}, label) : dash()));
    chip("imgCurrent", lib.current?.label);
    chip("imgRollback", lib.rollback?.label);
    if (lib.rollback) {
      chips.push(el("button", { class: "ghost small", onclick: oneAtATime(async () => {
        if (await confirmDialog(libQuestion(t("libRollbackConfirm", { label: lib.rollback.label })), { okText: t("libRollback") })) await libAct(`${libPath}/rollback`);
      }) }, t("libRollback")));
    }
    $("lib-summary").replaceChildren(...chips);

    $("lib-warnings").hidden = lib.warnings.length === 0;
    $("lib-warning-list").replaceChildren(...lib.warnings.map((w) => el("li", { title: w.message }, warningText(w), " · ", ago(w.lastSeen))));

    renderAdoption();
    renderMaster();
    if (!$("lib-label").value || $("lib-label").dataset.auto === "1") {
      $("lib-label").value = suggestLabel();
      $("lib-label").dataset.auto = "1";
    }

    $("lib-ops").hidden = lib.openOperations.length === 0;
    $("lib-op-list").replaceChildren(...lib.openOperations.map(renderOperation));

    const visible = lib.versions.filter((v) => v.state !== "retired").concat(lib.versions.filter((v) => v.state === "retired").slice(0, 5));
    $("lib-empty").hidden = visible.length > 0;
    $("lib-rows").replaceChildren(...visible.map((v) => {
      const badges = [badge(LIB_STATE[v.state] || "idle", t(`ls_${v.state}`), v.lastError || "")];
      if (v.role) badges.push(badge(v.role === "current" ? "ok" : "info", t(v.role === "current" ? "imgCurrent" : "imgRollback")));
      return el("tr", { class: v.state === "retired" ? "retired" : "" },
        el("td", { class: "mono" }, el("b", {}, v.label)),
        el("td", {}, el("div", { class: "badges" }, badges), v.lastError ? el("div", { class: "error small" }, v.lastError) : null),
        el("td", { class: "num" }, v.mountedOn || dash()),
        contentsCell(v),
        el("td", {}, v.publishedAt ? ago(v.publishedAt) : dash()),
        el("td", { class: "mono small" }, orDash(v.targetIqn, true)));
    }));

    if (history) {
      $("lib-history").replaceChildren(...history.map((op) => el("tr", {},
        el("td", {}, ago(op.createdAt)),
        el("td", {}, t(`op_${op.kind}`)),
        el("td", { class: "mono" }, orDash(op.versionLabel, true)),
        el("td", {}, badge(OP_STATUS[op.status] || "idle", t(`os_${op.status}`), op.lastError || ""),
          op.step && op.status !== "done" ? el("span", { class: "muted small" }, ` ${t(`step_${op.step}`)}`) : null),
        el("td", { class: "num" }, op.attempts),
        el("td", {}, op.status === "failed" ? el("button", { class: "small ghost", onclick: () => libAct(`${libPath}/operations/${op.id}/retry`) }, t("libRetry")) : null))));
    }
  }

  async function loadLibrary() {
    try {
      await loadLibraries();
      renderLibSwitch();
      const key = libKey;
      const base = libBase();
      const [fresh, history] = await Promise.all([api(base), api(`${base}/operations?limit=15`)]);
      // Пока шёл запрос, переключили библиотеку — ответ уже не для экрана.
      if (key !== libKey) return;
      const json = JSON.stringify([base, fresh, history]);
      $("lib-error").hidden = true;
      if (json !== libJson) {
        lib = fresh;
        libPath = base;
        libJson = json;
        renderLibrary(history);
      }
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("lib-error").textContent = t("loadFailed", { error: error.message });
      $("lib-error").hidden = false;
    }
  }

  async function libAct(path, body) {
    try {
      await api(path, { method: "POST", ...(body ? { body: JSON.stringify(body) } : {}) });
    } catch (error) {
      await alertDialog(problemText(error));
    }
    libJson = "";
    await loadLibrary();
  }

  async function publishLibrary(event) {
    event.preventDefault();
    if (!lib || !libPath) return; // данные выбранной библиотеки ещё не пришли — публиковать нечего и некуда
    const label = $("lib-label").value.trim();
    $("lib-publish-error").hidden = true;
    const dirty = lib?.master?.dirty === true;
    // Красная всегда: публикация разбирает самую старую версию (а с недописанным мастер-томом — ещё и рискованна).
    if (!(await confirmDialog(libQuestion(t(dirty ? "libPublishDirtyConfirm" : "libPublishConfirm", { label })), { okText: t("imgPublish"), danger: true }))) return;
    $("lib-publish-button").disabled = true;
    try {
      await api(`${libPath}/versions`, { method: "POST", body: JSON.stringify({ label, allowDirtyMaster: dirty }) });
      $("lib-label").value = "";
      $("lib-label").dataset.auto = "1";
      libJson = "";
      await loadLibrary();
    } catch (error) {
      const key = `reason_${error.reason}`;
      $("lib-publish-error").textContent = error.reason === "format" ? t("libLabelHint") : t(key) !== key ? t(key) : error.message;
      $("lib-publish-error").hidden = false;
    } finally {
      $("lib-publish-button").disabled = false;
    }
  }

  // ---------- Образы Windows ----------

  const IMAGE_STATE = { importing: "info", ready: "ok", failed: "bad" };

  function size(bytes) {
    if (bytes === null || bytes === undefined) return dash();
    if (bytes >= 1 << 30) return `${(bytes / (1 << 30)).toFixed(1)} GiB`;
    return bytes >= 1 << 20 ? `${Math.round(bytes / (1 << 20))} MiB` : `${Math.max(1, Math.round(bytes / 1024))} KiB`;
  }

  function renderImages() {
    if (!imgs || view !== "images") return;
    $("img-disabled").hidden = imgs.enabled;
    const chips = [];
    const chip = (key, label) => chips.push(el("span", { class: "chip" }, `${t(key)}: `, label ? el("b", {}, label) : dash()));
    chip("imgCurrent", imgs.current);
    chip("imgRollback", imgs.rollback);
    if (imgs.rollback) {
      chips.push(el("button", { class: "ghost small", onclick: oneAtATime(async () => {
        if (await confirmDialog(t("imgRollbackConfirm", { label: imgs.rollback }), { okText: t("imgRollbackBtn") })) await imageAct("/images/rollback", "POST");
      }) }, t("imgRollbackBtn")));
    }
    $("img-summary").replaceChildren(...chips);

    $("img-warnings").hidden = imgs.warnings.length === 0;
    $("img-warning-list").replaceChildren(...imgs.warnings.map((w) => el("li", { title: w.message }, warningText(w), " · ", ago(w.lastSeen))));

    $("img-incoming-empty").hidden = imgs.incoming.length > 0 || !imgs.enabled;
    $("img-incoming").replaceChildren(...imgs.incoming.map((f) => {
      const suggested = f.name.replace(/\.(wim|esd)$/i, "").toLowerCase().replace(/[^a-z0-9._-]+/g, "-").replace(/^[^a-z0-9]+/, "").slice(0, 40);
      const label = el("input", { class: "label mono", value: suggested, title: t("labelHint") });
      const index = el("input", { class: "index", type: "number", min: "1", value: "1" });
      const error = el("span", { class: "field-error" });
      return el("form", { class: "incoming-item", onsubmit: async (e) => {
        e.preventDefault();
        error.textContent = "";
        try {
          await api("/images/import", { method: "POST", body: JSON.stringify({ file: f.name, label: label.value.trim(), index: Number(index.value) }) });
          imgsJson = "";
          await loadImages();
        } catch (err) {
          const key = `reason_${err.reason}`;
          error.textContent = err.reason === "format" ? t("labelHint") : t(key) !== key ? t(key) : err.message;
        }
      } },
        el("div", { class: "file" }, el("b", { class: "mono" }, f.name), el("div", { class: "muted" }, size(f.sizeBytes), " · ", ago(f.modifiedAt))),
        el("label", {}, t("imgLabel"), label),
        el("label", {}, t("imgIndex"), index),
        el("button", { type: "submit" }, t("imgImport")),
        error);
    }));

    renderBootFiles();

    $("img-empty").hidden = imgs.images.length > 0;
    $("img-rows").replaceChildren(...imgs.images.map((i) => {
      const badges = [badge(IMAGE_STATE[i.state] || "idle", t(`is_${i.state}`), i.lastError || "")];
      if (i.role) badges.push(badge(i.role === "current" ? "ok" : "info", t(i.role === "current" ? "imgCurrent" : "imgRollback")));
      if (i.generalized === true) badges.push(badge("ok", t("gen_true")));
      if (i.generalized === false) badges.push(badge("bad", t("gen_false")));
      const editions = i.wimImages.map((w) => el("div", { class: w.index === i.imageIndex ? "picked" : "muted" },
        `${w.index}. ${w.name}`, w.build ? ` · ${w.build}` : "", w.architecture ? ` · ${w.architecture}` : ""));
      const actions = [];
      if (i.state === "ready" && i.role !== "current" && i.generalized !== false) {
        // Вопрос с красной кнопкой: публикация удаляет самую старую версию образа.
        actions.push(el("button", { class: "small", onclick: oneAtATime(async () => {
          if (await confirmDialog(t("imgPublishConfirm", { label: i.label }), { okText: t("imgPublish"), danger: true })) await imageAct(`/images/${encodeURIComponent(i.label)}/publish`, "POST");
        }) }, t("imgPublish")));
      }
      if (!i.publishedAt && !i.role && i.state !== "importing") {
        actions.push(el("button", { class: "danger small", onclick: oneAtATime(async () => {
          if (await confirmDialog(t("imgDeleteConfirm", { label: i.label }), { okText: t("imgDelete"), danger: true })) await imageAct(`/images/${encodeURIComponent(i.label)}`, "DELETE");
        }) }, t("imgDelete")));
      }
      return el("tr", {},
        el("td", { class: "mono" }, el("b", {}, i.label)),
        el("td", {}, el("div", { class: "badges" }, badges), i.lastError ? el("div", { class: "error small" }, i.lastError) : null),
        el("td", { class: "editions" }, editions.length ? editions : dash()),
        el("td", {}, size(i.sizeBytes)),
        el("td", { class: "mono", title: i.sha256 || "" }, i.sha256 ? `${i.sha256.slice(0, 12)}…` : dash()),
        el("td", {}, el("div", { class: "row-actions" }, actions)));
    }));
  }

  const BOOT_FILE = { ok: "ok", missing: "bad", invalid: "bad", unsigned: "warn", badSignature: "bad", wrongSigner: "warn" };

  function renderBootFiles() {
    const report = imgs.bootFiles;
    $("bf-no-tftp").hidden = !report || report.tftpChecked;
    $("bf-rows").replaceChildren(...(report?.files ?? []).map((f) => {
      const kind = f.status === "missing" && !f.required ? "idle" : BOOT_FILE[f.status] || "idle";
      const status = [badge(kind, t(`bf_${f.status}`), f.expected.length && f.status !== "ok" ? t("bfNotSecure") : "")];
      if (!f.required) status.push(el("span", { class: "muted small" }, ` ${t("bfOptional")}`));
      return el("tr", {},
        el("td", { class: "mono" }, f.name),
        el("td", { class: "muted" }, f.location === "tftp" ? "TFTP" : "HTTP"),
        el("td", {}, status),
        el("td", { class: "small" }, f.signedBy.length ? f.signedBy.join(", ") : f.expected.length ? dash() : ""),
        el("td", { class: "mono small" }, orDash(f.component, true)),
        el("td", { class: "mono", title: f.sha256 || "" }, f.sha256 ? `${f.sha256.slice(0, 12)}…` : ""));
    }));
  }

  async function loadImages() {
    try {
      const fresh = await api("/images");
      const json = JSON.stringify(fresh);
      $("img-error").hidden = true;
      if (json !== imgsJson) {
        imgs = fresh;
        imgsJson = json;
        renderImages();
      }
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("img-error").textContent = t("loadFailed", { error: error.message });
      $("img-error").hidden = false;
    }
  }

  async function imageAct(path, method) {
    try {
      await api(path, { method });
    } catch (error) {
      await alertDialog(problemText(error));
    }
    imgsJson = "";
    await loadImages();
  }

  // ---------- Бездиск ----------

  const SEAT_STATE = { ready: "ok", new: "info", failed: "bad" };

  function renderDiskless() {
    if (!dl) return;
    $("dl-disabled").hidden = dl.enabled;
    const chip = (key, value) => el("span", { class: "chip" }, `${t(key)}: `, value ? el("b", {}, value) : dash());
    const diskless = dl.machines.filter((m) => m.bootMode === "diskless").length;
    $("dl-summary").replaceChildren(
      chip("dlCurrent", dl.currentVersion),
      chip("dlRollbackVersion", dl.rollbackVersion),
      el("span", { class: "chip" }, el("b", {}, diskless), " ", t("dlSeatsCount")),
      el("span", { class: "chip mono small", title: t("dlImageZvol") }, dl.imageZvol));

    if (!$("dl-label").value || $("dl-label").dataset.auto !== "0") {
      const d = new Date();
      const base = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
      let label = base;
      for (let i = 2; dl.versions.some((v) => v.version === label); i++) label = `${base}-${i}`;
      $("dl-label").value = label;
      $("dl-label").dataset.auto = "1";
    }
    const masterOn = !!dl.masterMachineId;
    $("dl-publish-button").disabled = !dl.enabled;
    $("dl-rollback").disabled = !dl.rollbackVersion;
    $("dl-rollback-text").textContent = dl.rollbackVersion
      ? t("dlRollbackText", { current: dl.currentVersion ?? "—", previous: dl.rollbackVersion })
      : t("dlNoRollback");

    // Режим мастера: кто правит эталон; с «установкой» — ставится Windows прямо на эталон.
    const box = [];
    if (masterOn) {
      box.push(el("div", { class: "master-row" },
        badge("warn", t(dl.masterInstall ? "dlMasterInstalling" : "dlMasterEditing")), " ",
        el("b", {}, dl.masterMachineName ?? "?")));
      box.push(el("p", { class: "muted small" }, t(dl.masterInstall ? "dlMasterInstallSteps" : "dlMasterEditSteps")));
      const actions = [];
      if (dl.masterInstall) actions.push(el("button", { onclick: () => dlAct("/diskless/master", "PUT", { machineId: dl.masterMachineId, install: false }) }, t("dlMasterInstalled")));
      actions.push(el("button", { class: "ghost", onclick: oneAtATime(async () => {
        if (await confirmDialog(t("dlMasterOffConfirm"), { okText: t("dlMasterOff") })) await dlAct("/diskless/master", "PUT", { machineId: null, install: false });
      }) }, t("dlMasterOff")));
      box.push(el("div", { class: "master-row" }, actions));
    } else if (dl.machines.length === 0) {
      box.push(el("p", { class: "muted small" }, t("dlNoMachines")));
    } else {
      const select = el("select", { "aria-label": t("masterPc") }, dl.machines.map((m) =>
        el("option", { value: m.id }, `№${m.number} ${m.name}${m.online ? ` · ${t("online")}` : ""}`)));
      const install = el("input", { type: "checkbox", id: "dl-master-install" });
      if (!dl.currentVersion) install.checked = true;
      box.push(el("div", { class: "master-row" }, select,
        el("label", { class: "check small", for: "dl-master-install" }, install, " ", t("dlMasterInstallBox")),
        el("button", { onclick: () => dlAct("/diskless/master", "PUT", { machineId: select.value, install: install.checked }) }, t("dlMasterOn"))));
    }
    $("dl-master").replaceChildren(...box);

    $("dl-seats-empty").hidden = dl.seats.length > 0;
    $("dl-seat-rows").replaceChildren(...dl.seats.map((s) => el("tr", {},
      el("td", { class: "mono" }, s.kind === "master" ? t("dlMasterSeat") : s.target),
      el("td", {}, s.machineName ? `№${s.number} ${s.machineName}` : dash()),
      el("td", { class: "mono" }, s.kind === "master" ? t("dlMasterDisk") : orDash(s.baseVersion, true)),
      el("td", {}, badge(SEAT_STATE[s.state] || "idle", t(`dlState_${s.state}`), s.error || "")),
      el("td", { class: "num" }, s.boots),
      el("td", {}, ago(s.lastBootAt)))));

    $("dl-versions-empty").hidden = dl.versions.length > 0;
    $("dl-version-rows").replaceChildren(...dl.versions.map((v) => el("tr", {},
      el("td", { class: "mono" }, v.version),
      el("td", {}, v.version === dl.currentVersion ? badge("ok", t("dlIsCurrent"))
        : v.version === dl.rollbackVersion ? badge("info", t("dlIsRollback")) : badge("idle", t("dlIsOld"))),
      el("td", { class: "wrap" }, orDash(v.comment)),
      el("td", {}, ago(v.createdAt)))));
  }

  async function loadDiskless() {
    try {
      const fresh = await api("/diskless");
      const json = JSON.stringify(fresh);
      $("dl-error").hidden = true;
      if (json !== dlJson) {
        dl = fresh;
        dlJson = json;
        renderDiskless();
      } else {
        refreshAgo();
      }
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("dl-error").textContent = t("loadFailed", { error: error.message });
      $("dl-error").hidden = false;
    }
  }

  async function dlAct(path, method, body) {
    try {
      await api(path, { method, ...(body ? { body: JSON.stringify(body) } : {}) });
    } catch (error) {
      await alertDialog(t(`dlErr_${error.reason}`) !== `dlErr_${error.reason}` ? t(`dlErr_${error.reason}`) : error.message);
    }
    dlJson = "";
    await loadDiskless();
  }

  async function publishDiskless(event) {
    event.preventDefault();
    $("dl-publish-error").hidden = true;
    try {
      await api("/diskless/versions", { method: "POST", body: JSON.stringify({ label: $("dl-label").value.trim(), comment: $("dl-comment").value.trim() }) });
      $("dl-comment").value = "";
      $("dl-label").dataset.auto = "1";
    } catch (error) {
      $("dl-publish-error").textContent = t(`dlErr_${error.reason}`) !== `dlErr_${error.reason}` ? t(`dlErr_${error.reason}`) : error.message;
      $("dl-publish-error").hidden = false;
    }
    dlJson = "";
    await loadDiskless();
  }

  // ---------- Сохранения ----------

  const SAVE_RESULT = { ok: "ok", overLimit: "warn", rejected: "warn", error: "bad" };

  // Ноль байт — «0 KiB», а не «1 KiB», как у size() (там нулевых размеров не бывает): игрок без файлов, пустой диск.
  const savesSize = (bytes) => (bytes === 0 ? "0 KiB" : size(bytes));

  // Текст ошибки API сохранений: поле и причина из ответа — переводом (savesErr_<поле>_<причина>, у пути — код проверки
  // шаблона, как у помощника), без поля — savesErr_<причина>; 404 — «папки уже нет».
  function savesError(error) {
    if (error.status === 404) return t("savesEntryGone");
    const field = error.details?.field;
    const key = field ? `savesErr_${field}_${error.reason}` : `savesErr_${error.reason}`;
    return t(key) !== key ? t(key) : error.message;
  }

  function renderSavesStatus() {
    const s = svStatus;
    $("sv-disabled").hidden = s.enabled;
    $("sv-unavailable").hidden = !s.enabled || s.available;
    $("sv-unavailable").textContent = t("savesUnavailableNotice", { root: s.root });
    const row = (key, value) => [el("dt", {}, t(key)), el("dd", {}, value)];
    $("sv-status").replaceChildren(el("dl", { class: "sv-status" },
      row("savesStatusEnabled", badge(s.enabled ? "ok" : "idle", t(s.enabled ? "savesOn" : "savesOff"))),
      row("savesStatusDisk", badge(s.available ? "ok" : "bad", t(s.available ? "savesDiskOk" : "savesDiskMissing"))),
      row("savesStatusRoot", el("span", { class: "mono" }, s.root)),
      row("savesStatusUsed", s.available ? savesSize(s.usedBytes) : dash())));
  }

  // Сводка: папки (включено/всего, если выключенные есть) и игроки — с поиском это найденные по нему.
  function renderSavesSummary() {
    const chip = (key, value) => el("span", { class: "chip" }, `${t(key)}: `, el("b", {}, value));
    const on = svEntries.filter((e) => e.enabled).length;
    $("sv-summary").replaceChildren(
      chip("savesSumEntries", on === svEntries.length ? on : `${on}/${svEntries.length}`),
      chip(svQuery ? "savesSumFound" : "savesSumPlayers", svPlayers.total));
  }

  // Папки из списка: вкл/выкл — сразу на сервер; название и путь — в окне «Изменить»; удаление — с вопросом.
  function renderSavesEntries() {
    $("sv-entries-empty").hidden = svEntries.length > 0;
    $("sv-entry-rows").replaceChildren(...svEntries.map((e) => {
      const toggle = el("input", { type: "checkbox", "aria-label": t("savesEntryToggle"), title: t("savesEntryToggle") });
      toggle.checked = e.enabled;
      toggle.addEventListener("change", async () => {
        toggle.disabled = true;
        await savesAct(`/saves/entries/${encodeURIComponent(e.id)}`, "PUT", { enabled: toggle.checked });
      });
      return el("tr", {},
        el("td", {}, e.name),
        el("td", { class: "mono" }, e.path),
        el("td", {}, toggle),
        el("td", {}, el("div", { class: "row-actions" },
          el("button", { class: "ghost small", onclick: () => openSaveEntry(e) }, t("savesEntryEdit")),
          el("button", { class: "danger small", onclick: oneAtATime(async () => {
            if (await confirmDialog(t("savesEntryDeleteConfirm", { name: e.name }), { okText: t("savesEntryDelete"), danger: true })) {
              await savesAct(`/saves/entries/${encodeURIComponent(e.id)}`, "DELETE");
            }
          }) }, t("savesEntryDelete")))));
    }));
  }

  // Игроки: страница из 50, поиск по части ID; последний результат — отметка, причина — во всплывающей подсказке.
  function renderSavesPlayers() {
    const p = svPlayers;
    const pages = Math.max(1, Math.ceil(p.total / p.pageSize));
    $("sv-players-empty").hidden = p.items.length > 0;
    $("sv-players-empty").textContent = svQuery ? t("savesPlayersNotFound", { query: svQuery }) : t("savesPlayersEmpty");
    $("sv-pages").hidden = pages < 2;
    $("sv-page-text").textContent = t("savesPage", { page: svPage, pages, total: p.total });
    $("sv-prev").disabled = svPage <= 1;
    $("sv-next").disabled = svPage >= pages;
    $("sv-player-rows").replaceChildren(...p.items.map((r) => {
      let result = dash();
      if (r.lastResult) {
        const hintKey = `savesResultHint_${r.lastResult}`;
        const hint = [t(hintKey) !== hintKey ? t(hintKey) : "", r.lastError ?? ""].filter(Boolean).join(" ");
        const label = t(`savesResult_${r.lastResult}`);
        result = el("div", { class: "badges" }, badge(SAVE_RESULT[r.lastResult] || "idle", label, hint), r.lastResultAt ? ago(r.lastResultAt) : null);
      }
      return el("tr", {},
        el("td", { class: "mono" }, r.playerId),
        el("td", {}, r.savedAt ? savesSize(r.sizeBytes) : dash()),
        el("td", { class: "num" }, r.savedAt ? r.fileCount : dash()),
        el("td", {}, ago(r.savedAt)),
        el("td", {}, orDash(r.machineName)),
        el("td", {}, result),
        el("td", {}, el("button", { class: "danger small", onclick: oneAtATime(async () => {
          if (await confirmDialog(t("savesPlayerDeleteConfirm", { id: r.playerId }), { okText: t("savesPlayerDelete"), danger: true })) {
            await savesAct(`/saves/players/${encodeURIComponent(r.playerId)}`, "DELETE");
          }
        }) }, t("savesPlayerDelete"))));
    }));
  }

  function renderSaves() {
    if (!svStatus || !svEntries || !svPlayers || view !== "saves") return;
    renderSavesStatus();
    renderSavesSummary();
    renderSavesEntries();
    renderSavesPlayers();
  }

  // Состояние, папки и страница игроков — одним заходом; ответ на устаревший запрос (поиск сменился, пока он шёл)
  // не рисуется. Удалили последних игроков страницы — на последнюю непустую.
  async function loadSaves() {
    const seq = ++svSeq;
    try {
      const [status, entries, players] = await Promise.all([
        api("/saves/status"),
        api("/saves/entries"),
        api(`/saves/players?query=${encodeURIComponent(svQuery)}&page=${svPage}`),
      ]);
      if (seq !== svSeq) return;
      if (players.items.length === 0 && players.total > 0 && svPage > 1) {
        svPage = Math.ceil(players.total / players.pageSize);
        await loadSaves();
        return;
      }
      $("sv-error").hidden = true;
      const json = JSON.stringify([status, entries, players, svPage, svQuery]);
      if (json !== svJson) {
        svStatus = status;
        svEntries = entries;
        svPlayers = players;
        svJson = json;
        renderSaves();
      } else {
        refreshAgo();
      }
    } catch (error) {
      if (error.message === "unauthorized" || seq !== svSeq) return;
      $("sv-error").textContent = t("loadFailed", { error: error.message });
      $("sv-error").hidden = false;
    }
  }

  async function savesAct(path, method, body) {
    try {
      await api(path, { method, ...(body ? { body: JSON.stringify(body) } : {}) });
    } catch (error) {
      await alertDialog(savesError(error));
    }
    svJson = "";
    await loadSaves();
  }

  // Форма настроек не перезаписывается, пока в ней что-то набирают (как адресный план на «Сети»).
  function fillSavesSettings(s) {
    const form = $("sv-settings");
    form.elements.limitMb.value = s.limitMb;
    form.elements.excludes.value = s.excludes.join("\n");
    form.querySelectorAll(".field-error").forEach((node) => node.remove());
    form.querySelectorAll(".invalid").forEach((node) => node.classList.remove("invalid"));
    svSettingsDirty = false;
  }

  async function loadSavesSettings(force) {
    if (svSettingsDirty && !force) return;
    try {
      fillSavesSettings(await api("/saves/settings"));
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("sv-error").textContent = t("loadFailed", { error: error.message });
      $("sv-error").hidden = false;
    }
  }

  async function saveSavesSettings(event) {
    event.preventDefault();
    const form = $("sv-settings");
    // Пустое или дробное число — 0: сервер ответит «вне диапазона» с подсказкой 1…2000 (null оставил бы лимит прежним).
    const limit = Number(form.elements.limitMb.value.trim() || "0");
    const body = {
      limitMb: Number.isInteger(limit) ? limit : 0,
      // По одному в строке; пустые строки — не исключения.
      excludes: form.elements.excludes.value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean),
    };
    form.querySelectorAll(".field-error").forEach((node) => node.remove());
    form.querySelectorAll(".invalid").forEach((node) => node.classList.remove("invalid"));
    $("sv-settings-error").hidden = true;
    $("sv-settings-saved").hidden = true;
    $("sv-settings-save").disabled = true;
    try {
      fillSavesSettings(await api("/saves/settings", { method: "PUT", body: JSON.stringify(body) }));
      $("sv-settings-saved").hidden = false;
    } catch (error) {
      const input = form.elements[error.details?.field ?? ""];
      if (input) {
        input.classList.add("invalid");
        input.after(el("span", { class: "field-error" }, savesError(error)));
      } else {
        $("sv-settings-error").textContent = savesError(error);
        $("sv-settings-error").hidden = false;
      }
    } finally {
      $("sv-settings-save").disabled = false;
    }
  }

  async function addSaveEntry(event) {
    event.preventDefault();
    const result = $("sv-entry-add-result");
    $("sv-entry-path").classList.remove("invalid");
    $("sv-entry-name").classList.remove("invalid");
    $("sv-entry-add-button").disabled = true;
    try {
      const created = await api("/saves/entries", { method: "POST", body: JSON.stringify({ name: $("sv-entry-name").value.trim(), path: $("sv-entry-path").value.trim() }) });
      result.className = "small ok-text";
      result.textContent = t("savesEntryAdded", { name: created.name });
      $("sv-entry-name").value = "";
      $("sv-entry-path").value = "";
    } catch (error) {
      result.className = "error small";
      result.textContent = savesError(error);
      const field = error.details?.field;
      if (field === "path" || field === "name") $(`sv-entry-${field}`).classList.add("invalid");
    } finally {
      $("sv-entry-add-button").disabled = false;
    }
    result.hidden = false;
    svJson = "";
    await loadSaves();
  }

  function openSaveEntry(entry) {
    svEditing = entry;
    $("save-entry-name").value = entry.name;
    $("save-entry-path").value = entry.path;
    $("save-entry-name").classList.remove("invalid");
    $("save-entry-path").classList.remove("invalid");
    $("save-entry-error").hidden = true;
    $("save-entry").showModal();
  }

  async function saveSaveEntry(event) {
    event.preventDefault();
    const body = { name: $("save-entry-name").value.trim(), path: $("save-entry-path").value.trim() };
    $("save-entry-name").classList.remove("invalid");
    $("save-entry-path").classList.remove("invalid");
    $("save-entry-save").disabled = true;
    try {
      await api(`/saves/entries/${encodeURIComponent(svEditing.id)}`, { method: "PUT", body: JSON.stringify(body) });
      $("save-entry").close();
    } catch (error) {
      $("save-entry-error").textContent = savesError(error);
      $("save-entry-error").hidden = false;
      const field = error.details?.field;
      if (field === "path" || field === "name") $(`save-entry-${field}`).classList.add("invalid");
    } finally {
      $("save-entry-save").disabled = false;
    }
    svJson = "";
    await loadSaves();
  }

  // Поиск — после паузы в наборе, с первой страницы.
  function searchSavePlayers() {
    clearTimeout(svQueryTimer);
    svQueryTimer = setTimeout(() => {
      const query = $("sv-query").value.trim();
      if (query === svQuery) return;
      svQuery = query;
      svPage = 1;
      svJson = "";
      loadSaves();
    }, 300);
  }

  function savePlayersPage(delta) {
    svPage = Math.max(1, svPage + delta);
    svJson = "";
    loadSaves();
  }

  // ---------- Навигация ----------

  function route() {
    view = { "#network": "network", "#images": "images", "#library": "library", "#diskless": "diskless", "#saves": "saves" }[location.hash] ?? "machines";
    document.querySelectorAll(".tab").forEach((tab) => tab.classList.toggle("active", tab.dataset.view === view));
    if (!token) return;
    $("machines").hidden = view !== "machines";
    $("network").hidden = view !== "network";
    $("images").hidden = view !== "images";
    $("library").hidden = view !== "library";
    $("diskless").hidden = view !== "diskless";
    $("saves").hidden = view !== "saves";
    if (view === "diskless") dlJson = "";
    if (view === "images") imgsJson = "";
    if (view === "library") libJson = "";
    if (view === "network") {
      netJson = "";
      loadSettings(false);
    }
    if (view === "saves") {
      svJson = "";
      loadSavesSettings(false);
    }
    refresh();
  }

  function refresh() {
    if (view === "machines") load();
    if (view === "images") loadImages();
    if (view === "library") loadLibrary();
    if (view === "diskless") loadDiskless();
    if (view === "saves") loadSaves();
    loadNetwork();
  }

  async function load() {
    try {
      const fresh = await api("/machines");
      // Вторая библиотека: буква G: и названия для строк тома и сводки — из списка библиотек (раз за вход в панель).
      if (fresh.libraries && !libs.length) await loadLibraries();
      const json = JSON.stringify(fresh);
      $("load-error").hidden = true;
      if (json !== dataJson) {
        data = fresh;
        dataJson = json;
        render();
      } else {
        refreshAgo();
      }
    } catch (error) {
      if (error.message === "unauthorized") return;
      $("load-error").textContent = t("loadFailed", { error: error.message });
      $("load-error").hidden = false;
    }
  }

  async function act(path) {
    dataJson = "";
    try {
      await api(path, { method: "POST" });
    } catch (error) {
      await alertDialog(error.message);
    }
    await load();
  }

  // Новый ПК по MAC: ПК, которому негде зарегистрироваться самому (новый гибрид без Windows, бездиск). Гибрид (local)
  // потом ставится перезаливкой по сети, бездиск (diskless) сразу грузится с личного диска места; помощник при первой
  // регистрации привязывается к этой записи.
  async function addMachine(event) {
    event.preventDefault();
    const result = $("add-result");
    const number = $("add-number").value.trim();
    const mode = $("add-mode").value;
    $("add-button").disabled = true;
    try {
      const added = await api("/machines", { method: "POST", body: JSON.stringify({
        macAddress: $("add-mac").value.trim(),
        number: number ? Number(number) : null,
        name: $("add-name").value.trim() || null,
        bootMode: mode,
      }) });
      result.className = "small ok-text";
      result.textContent = t(mode === "diskless" ? "dlAdded" : "addedLocal", { name: added.name, number: added.number });
      $("add-mac").value = "";
      $("add-number").value = "";
      $("add-name").value = "";
    } catch (error) {
      result.className = "error small";
      result.textContent = t(`dlErr_${error.reason}`) !== `dlErr_${error.reason}` ? t(`dlErr_${error.reason}`) : error.message;
    } finally {
      $("add-button").disabled = false;
    }
    result.hidden = false;
    dlJson = "";
    dataJson = "";
    await load();
  }

  function openEdit(machine) {
    editing = machine;
    $("edit-number").value = machine.number;
    $("edit-name").value = machine.name;
    fillZoneSelect($("edit-zone"), machine.zone);
    $("edit-maintenance").checked = machine.status === "maintenance";
    $("edit-error").hidden = true;
    $("edit").showModal();
  }

  async function saveEdit(event) {
    event.preventDefault();
    const body = {
      number: Number($("edit-number").value),
      name: $("edit-name").value.trim(),
      zone: $("edit-zone").value,
      maintenance: $("edit-maintenance").checked,
    };
    try {
      await api(`/machines/${editing.id}`, { method: "PATCH", body: JSON.stringify(body) });
      $("edit").close();
      dataJson = "";
      await load();
    } catch (error) {
      const zoneGone = error.details?.field === "zone";
      $("edit-error").textContent = error.reason === "numberTaken" ? t("numberTaken")
        : zoneGone ? t("zoneGoneEdit")
        : error.message;
      $("edit-error").hidden = false;
      if (zoneGone) {
        // Зону удалили, пока окно было открыто: список зон в окне — заново, без неё; выбрана текущая зона ПК.
        dataJson = "";
        await load();
        fillZoneSelect($("edit-zone"), data.machines.find((m) => m.id === editing.id)?.zone ?? editing.zone);
      }
    }
  }

  function showApp() {
    $("login").hidden = true;
    $("logout").hidden = false;
    route();
    clearInterval(timer);
    timer = setInterval(() => {
      if (document.hidden || $("edit").open || $("reimage").open || $("ask").open || $("zone-rename").open || $("save-entry").open) return;
      refresh();
      refreshAgo();
    }, REFRESH_MS);
  }

  function logout() {
    token = null;
    safeSet(sessionStorage, "panel.token", null);
    clearInterval(timer);
    $("machines").hidden = true;
    $("network").hidden = true;
    $("images").hidden = true;
    $("library").hidden = true;
    $("diskless").hidden = true;
    $("saves").hidden = true;
    $("dhcp-alert").hidden = true;
    $("logout").hidden = true;
    $("login").hidden = false;
  }

  async function login(event) {
    event.preventDefault();
    token = $("token").value.trim();
    const response = await fetch(API + "/machines", { headers: { Authorization: `Bearer ${token}` } });
    if (response.ok) {
      safeSet(sessionStorage, "panel.token", token);
      $("login-error").hidden = true;
      $("token").value = "";
      showApp();
    } else {
      $("login-error").textContent = t("loginFailed");
      $("login-error").hidden = false;
    }
  }

  document.addEventListener("DOMContentLoaded", () => {
    applyStaticTexts();
    $("lang").addEventListener("change", (e) => {
      lang = e.target.value;
      safeSet(localStorage, "panel.lang", lang);
      applyStaticTexts();
      dataJson = "";
      render();
      renderNetwork();
      renderCapacity();
      imgsJson = "";
      renderImages();
      libJson = "";
      if (view === "library") loadLibrary();
      dlJson = "";
      renderDiskless();
      svJson = "";
      renderSaves();
    });
    $("login-form").addEventListener("submit", login);
    $("logout").addEventListener("click", logout);
    $("edit-form").addEventListener("submit", saveEdit);
    $("edit-cancel").addEventListener("click", () => $("edit").close());
    $("net-form").addEventListener("submit", saveNetwork);
    $("net-form").addEventListener("input", () => { formDirty = true; $("net-saved").hidden = true; });
    $("net-download").addEventListener("click", downloadConfig);
    $("reimage-form").addEventListener("submit", startReimage);
    $("lib-publish").addEventListener("submit", publishLibrary);
    $("lib-label").addEventListener("input", () => { $("lib-label").dataset.auto = "0"; });
    $("dl-publish").addEventListener("submit", publishDiskless);
    $("add-machine").addEventListener("submit", addMachine);
    $("zone-add").addEventListener("submit", addZone);
    $("zone-assign").addEventListener("submit", assignSeats);
    $("zone-rename-form").addEventListener("submit", saveZoneRename);
    $("zone-rename-cancel").addEventListener("click", () => $("zone-rename").close());
    // Подсказка на «Бездиске» ведёт к общей форме уже с режимом «Бездиск» (по умолчанию там «Гибрид») и сразу к полю MAC.
    // pushState не шлёт hashchange — экран переключается один раз, здесь.
    $("dl-add-go").addEventListener("click", (event) => {
      event.preventDefault();
      $("add-mode").value = "diskless";
      history.pushState(null, "", "#machines");
      route();
      $("add-machine").scrollIntoView({ block: "start" });
      $("add-mac").focus({ preventScroll: true });
    });
    $("dl-label").addEventListener("input", () => { $("dl-label").dataset.auto = "0"; });
    $("dl-rollback").addEventListener("click", oneAtATime(async () => {
      if (await confirmDialog(t("dlRollbackConfirm", { previous: dl?.rollbackVersion ?? "" }), { okText: t("dlRollback") })) await dlAct("/diskless/rollback", "POST");
    }));
    $("reimage-cancel").addEventListener("click", () => $("reimage").close());
    $("ask-cancel").addEventListener("click", () => $("ask").close());
    $("sv-settings").addEventListener("submit", saveSavesSettings);
    $("sv-settings").addEventListener("input", () => { svSettingsDirty = true; $("sv-settings-saved").hidden = true; });
    $("sv-entry-add").addEventListener("submit", addSaveEntry);
    $("save-entry-form").addEventListener("submit", saveSaveEntry);
    $("save-entry-cancel").addEventListener("click", () => $("save-entry").close());
    $("sv-query").addEventListener("input", searchSavePlayers);
    $("sv-prev").addEventListener("click", () => savePlayersPage(-1));
    $("sv-next").addEventListener("click", () => savePlayersPage(1));
    window.addEventListener("hashchange", route);
    if (token) showApp(); else logout();
  });
})();
