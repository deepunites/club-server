// Экраны «Рабочие станции», «Библиотека игр», «Образы Windows», «Бездиск» и «Сеть». Без сборки и внешних зависимостей: панель работает в клубе без интернета.
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
  let dl = null;
  let dlJson = "";
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
      error.details = body?.error?.details;
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

  // Версия образа на ПК и ход перезаливки: этап, проценты, причина сбоя.
  function windowsCell(m) {
    const r = m.reimage;
    const parts = [m.bootMode === "diskless" ? badge("info", t("bootDiskless"), t("bootDisklessHint")) : m.imageVersion ? el("span", { class: "mono" }, m.imageVersion) : dash()];
    if (m.masterState) {
      parts.push(el("div", {}, badge(m.masterState === "failed" ? "bad" : "warn", t("masterBadge", { letter: "M" }), t(`mm_${m.masterState}`))));
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
    buttons.push(el("button", { class: "ghost small", title: t(toDiskless ? "bootToDisklessHint" : "bootToLocalHint"), onclick: async () => {
      if (!confirm(t(toDiskless ? "bootToDisklessConfirm" : "bootToLocalConfirm", { name: m.name }))) return;
      try {
        await api(`/machines/${m.id}/boot-mode`, { method: "PUT", body: JSON.stringify({ mode: toDiskless ? "diskless" : "local" }) });
      } catch (error) {
        alert(error.message);
      }
      dataJson = "";
      await load();
    } }, t(toDiskless ? "bootToDiskless" : "bootToLocal")));
    if (r && ["requested", "failed"].includes(r.state) && !r.diskTouched) {
      buttons.push(el("button", { class: "danger small", onclick: () => {
        if (confirm(t("reimageConfirmCancel", { name: m.name }))) act(`/machines/${m.id}/reimage/cancel`);
      } }, t("reimageCancel")));
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
    chips.push(el("span", { class: "chip" }, `${t("sumLibrary")}: `, data.currentLibraryVersion ? el("b", {}, data.currentLibraryVersion) : dash()));
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
        windowsCell(m),
        el("td", {}, orDash(m.helperVersion, true)),
        el("td", {}, ago(m.lastSeenAt)),
        reimageActions(m));
    }));
  }

  function render() {
    if (!data) return;
    renderSummary();
    renderPending();
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
        op.status === "failed" ? el("button", { class: "small", onclick: () => libAct(`/library/operations/${op.id}/retry`) }, t("libRetry")) : null),
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
    if (!m) { box.replaceChildren(); return; }
    const parts = [];
    const head = [badge(MASTER_STATE[m.state] || "idle", t(`ms_${m.state}`))];
    if (m.machineName && m.state !== "closed") head.push(el("span", {}, " ", t("masterOnPc", { name: m.machineName, letter: m.driveLetter })));
    if (m.state === "open") {
      head.push(" ", badge(MASTER_MACHINE[m.machineState ?? "none"] || "idle", t(`mm_${m.machineState ?? "none"}`)));
      if (m.openedAt) head.push(el("span", { class: "muted small" }, ` · ${t("masterSince")} `, ago(m.openedAt)));
    }
    parts.push(el("div", { class: "master-row" }, head));
    if (m.state === "closing") parts.push(el("div", { class: "muted small" }, t("masterWaiting")));
    // У открытого тома lastError — оговорка открытия (таргет не виден ПК), а не ошибка.
    if (m.lastError) parts.push(el("div", { class: m.state === "open" ? "notice small" : "error small" }, m.lastError));
    if (m.dirty && m.state === "closed") parts.push(el("p", { class: "notice small" }, t("masterDirty")));

    const actions = [];
    if (m.state === "closed") {
      const candidates = lib.masterCandidates ?? [];
      if (candidates.length === 0) {
        parts.push(el("p", { class: "muted small" }, t("masterNoCandidates")));
      } else {
        const select = el("select", { "aria-label": t("masterPc") }, candidates.map((c) =>
          el("option", { value: c.id }, `${c.name}${c.online ? ` · ${t("online")}` : ""}`)));
        actions.push(select, el("button", { onclick: () => libAct("/library/master/open", { machineId: select.value }) }, t("masterOpen")));
      }
    }
    if (m.state === "open") actions.push(el("button", { onclick: () => libAct("/library/master/close", {}) }, t("masterFinish")));
    if (m.state === "failed") actions.push(el("button", { onclick: () => libAct("/library/master/close", {}) }, t("masterCleanup")));
    if ((m.state === "open" || m.state === "closing") && !m.forceClose) {
      actions.push(el("button", { class: "danger", onclick: () => {
        if (confirm(t("masterForceConfirm"))) libAct("/library/master/close", { force: true });
      } }, t("masterForce")));
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
      chips.push(el("button", { class: "ghost small", onclick: () => {
        if (confirm(t("libRollbackConfirm", { label: lib.rollback.label }))) libAct("/library/rollback");
      } }, t("libRollback")));
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
        el("td", {}, op.status === "failed" ? el("button", { class: "small ghost", onclick: () => libAct(`/library/operations/${op.id}/retry`) }, t("libRetry")) : null))));
    }
  }

  async function loadLibrary() {
    try {
      const [fresh, history] = await Promise.all([api("/library"), api("/library/operations?limit=15")]);
      const json = JSON.stringify([fresh, history]);
      $("lib-error").hidden = true;
      if (json !== libJson) {
        lib = fresh;
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
      alert(problemText(error));
    }
    libJson = "";
    await loadLibrary();
  }

  async function publishLibrary(event) {
    event.preventDefault();
    const label = $("lib-label").value.trim();
    $("lib-publish-error").hidden = true;
    const dirty = lib?.master?.dirty === true;
    if (!confirm(t(dirty ? "libPublishDirtyConfirm" : "libPublishConfirm", { label }))) return;
    $("lib-publish-button").disabled = true;
    try {
      await api("/library/versions", { method: "POST", body: JSON.stringify({ label, allowDirtyMaster: dirty }) });
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
      chips.push(el("button", { class: "ghost small", onclick: () => {
        if (confirm(t("imgRollbackConfirm", { label: imgs.rollback }))) imageAct("/images/rollback", "POST");
      } }, t("imgRollbackBtn")));
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
      if (i.state === "ready" && i.role !== "current") {
        actions.push(el("button", { class: "small", onclick: () => {
          if (confirm(t("imgPublishConfirm", { label: i.label }))) imageAct(`/images/${encodeURIComponent(i.label)}/publish`, "POST");
        } }, t("imgPublish")));
      }
      if (!i.publishedAt && !i.role && i.state !== "importing") {
        actions.push(el("button", { class: "danger small", onclick: () => {
          if (confirm(t("imgDeleteConfirm", { label: i.label }))) imageAct(`/images/${encodeURIComponent(i.label)}`, "DELETE");
        } }, t("imgDelete")));
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
      alert(problemText(error));
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
      actions.push(el("button", { class: "ghost", onclick: () => {
        if (confirm(t("dlMasterOffConfirm"))) dlAct("/diskless/master", "PUT", { machineId: null, install: false });
      } }, t("dlMasterOff")));
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
      alert(t(`dlErr_${error.reason}`) !== `dlErr_${error.reason}` ? t(`dlErr_${error.reason}`) : error.message);
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

  async function addDisklessMachine(event) {
    event.preventDefault();
    const result = $("dl-add-result");
    const number = $("dl-add-number").value.trim();
    try {
      const added = await api("/machines", { method: "POST", body: JSON.stringify({
        macAddress: $("dl-add-mac").value.trim(),
        number: number ? Number(number) : null,
        name: $("dl-add-name").value.trim() || null,
        bootMode: "diskless",
      }) });
      result.className = "small ok-text";
      result.textContent = t("dlAdded", { name: added.name, number: added.number });
      $("dl-add-mac").value = "";
      $("dl-add-number").value = "";
      $("dl-add-name").value = "";
    } catch (error) {
      result.className = "error small";
      result.textContent = t(`dlErr_${error.reason}`) !== `dlErr_${error.reason}` ? t(`dlErr_${error.reason}`) : error.message;
    }
    result.hidden = false;
    dlJson = "";
    dataJson = "";
    await loadDiskless();
  }

  // ---------- Навигация ----------

  function route() {
    view = { "#network": "network", "#images": "images", "#library": "library", "#diskless": "diskless" }[location.hash] ?? "machines";
    document.querySelectorAll(".tab").forEach((tab) => tab.classList.toggle("active", tab.dataset.view === view));
    if (!token) return;
    $("machines").hidden = view !== "machines";
    $("network").hidden = view !== "network";
    $("images").hidden = view !== "images";
    $("library").hidden = view !== "library";
    $("diskless").hidden = view !== "diskless";
    if (view === "diskless") dlJson = "";
    if (view === "images") imgsJson = "";
    if (view === "library") libJson = "";
    if (view === "network") {
      netJson = "";
      loadSettings(false);
    }
    refresh();
  }

  function refresh() {
    if (view === "machines") load();
    if (view === "images") loadImages();
    if (view === "library") loadLibrary();
    if (view === "diskless") loadDiskless();
    loadNetwork();
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
    $("logout").hidden = false;
    route();
    clearInterval(timer);
    timer = setInterval(() => {
      if (document.hidden || $("edit").open || $("reimage").open) return;
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
    $("dl-add").addEventListener("submit", addDisklessMachine);
    $("dl-label").addEventListener("input", () => { $("dl-label").dataset.auto = "0"; });
    $("dl-rollback").addEventListener("click", () => {
      if (confirm(t("dlRollbackConfirm", { previous: dl?.rollbackVersion ?? "" }))) dlAct("/diskless/rollback", "POST");
    });
    $("reimage-cancel").addEventListener("click", () => $("reimage").close());
    window.addEventListener("hashchange", route);
    if (token) showApp(); else logout();
  });
})();
