// Экран «Рабочие станции». Без сборки и внешних зависимостей: панель работает в клубе без интернета.
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
      error.reason = body?.error?.details?.reason;
      throw error;
    }
    return response.status === 204 ? null : response.json();
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
    if (volume.state === "mounted" && volume.readOnlyVerified !== true) badges.push(badge("bad", t("volNotVerified")));
    return el("div", { class: "badges" }, badges);
  }

  function zoneName(id) {
    return data?.zones.find((z) => z.id === id)?.name ?? id;
  }

  function renderSummary() {
    const chip = (value, key) => el("span", { class: "chip" }, el("b", {}, value), " ", t(key));
    const chips = [chip(data.online, "sumOnline"), chip(data.offline, "sumOffline")];
    if (data.pending) chips.push(chip(data.pending, "sumPending"));
    if (data.maintenance) chips.push(chip(data.maintenance, "sumMaintenance"));
    chips.push(el("span", { class: "chip" }, `${t("sumLibrary")}: `, data.currentLibraryVersion ? el("b", {}, data.currentLibraryVersion) : dash()));
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
        el("button", { class: "danger", onclick: () => {
          if (confirm(t("rejectConfirm", { name: m.hostname }))) act(`/machines/${m.id}/reject`);
        } }, t("reject"))))));
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
        el("td", {}, volumeCell(m.volume)),
        el("td", {}, orDash(m.helperVersion, true)),
        el("td", {}, ago(m.lastSeenAt)),
        el("td", {}, el("button", { class: "ghost", onclick: () => openEdit(m) }, t("edit"))));
    }));
  }

  function render() {
    if (!data) return;
    renderSummary();
    renderPending();
    renderRows();
  }

  async function load() {
    try {
      const fresh = await api("/machines");
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
      alert(error.message);
    }
    await load();
  }

  function openEdit(machine) {
    editing = machine;
    $("edit-number").value = machine.number;
    $("edit-name").value = machine.name;
    $("edit-zone").replaceChildren(...data.zones.map((z) => el("option", { value: z.id }, z.name)));
    $("edit-zone").value = machine.zone;
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
      $("edit-error").textContent = error.reason === "numberTaken" ? t("numberTaken") : error.message;
      $("edit-error").hidden = false;
    }
  }

  function showApp() {
    $("login").hidden = true;
    $("machines").hidden = false;
    $("logout").hidden = false;
    load();
    clearInterval(timer);
    timer = setInterval(() => { if (!document.hidden && !$("edit").open) load(); }, REFRESH_MS);
  }

  function logout() {
    token = null;
    safeSet(sessionStorage, "panel.token", null);
    clearInterval(timer);
    $("machines").hidden = true;
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
    });
    $("login-form").addEventListener("submit", login);
    $("logout").addEventListener("click", logout);
    $("edit-form").addEventListener("submit", saveEdit);
    $("edit-cancel").addEventListener("click", () => $("edit").close());
    if (token) showApp(); else logout();
  });
})();
