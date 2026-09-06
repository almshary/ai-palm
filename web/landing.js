const connectedElement = document.querySelector("#connectedHosts");
const registeredElement = document.querySelector("#registeredHosts");
const modelsElement = document.querySelector("#availableModels");
const modelChipsElement = document.querySelector("#modelChips");
const networkTitleElement = document.querySelector("#networkTitle");
const networkSignalElement = document.querySelector("#networkSignal");
const connectedDevicesElement = document.querySelector("#connectedDevices");
const offlineDevicesElement = document.querySelector("#offlineDevices");
const connectedDevicesCount = document.querySelector("#connectedDevicesCount");
const offlineDevicesCount = document.querySelector("#offlineDevicesCount");
const i18n = window.NawahI18n;
let latestPayload = null;

function escapeText(value) {
  const span = document.createElement("span");
  span.textContent = value ?? "";
  return span.innerHTML;
}

function cleanGpuName(name, vendor) {
  const value = String(name || i18n.t("unknownGpu")).trim();
  const knownVendor = String(vendor || "").trim();
  if (!knownVendor) return value;
  return value.replace(new RegExp(`^${knownVendor.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s+`, "i"), "");
}

function vendorLogo(vendor) {
  const normalized = String(vendor || "").toLowerCase();
  if (normalized === "amd") return '<span class="gpu-brand amd" role="img" aria-label="AMD"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 4h7v3H7v4H4V4Zm9 0h7v7h-3V7h-4V4Zm7 9v7h-7v-3h4v-4h3ZM11 20H4v-7h3v4h4v3Z"></path></svg></span>';
  if (normalized === "nvidia") return '<span class="gpu-brand nvidia" role="img" aria-label="NVIDIA"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12s3.8-5 9-5c4.6 0 7.4 3.6 9 5-1.6 1.4-4.4 5-9 5-5.2 0-9-5-9-5Z"></path><circle cx="12" cy="12" r="3"></circle><circle cx="12" cy="12" r="1"></circle></svg></span>';
  if (normalized === "intel") return '<span class="gpu-brand intel" role="img" aria-label="Intel"><svg viewBox="0 0 24 24" aria-hidden="true"><ellipse cx="12" cy="12" rx="9" ry="6.5"></ellipse><path d="M10 10.5v5M10 8.5h.01M13 10.5v5m0-3.2c.8-1.4 2.8-1 2.8.7v2.5"></path></svg></span>';
  return '<span class="gpu-brand unknown" role="img" aria-label="GPU"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="6" width="18" height="12" rx="2"></rect><path d="M7 10h6v4H7m10-4v4M6 18v2m12-2v2"></path></svg></span>';
}

function renderDevices(devices, online) {
  if (!devices.length) return `<p class="device-empty">${escapeText(i18n.t(online ? "noConnectedDevices" : "noOfflineDevices"))}</p>`;
  return devices.map((device) => {
    const gpu = cleanGpuName(device.gpu, device.gpu_vendor);
    const vram = Number(device.vram_gb) > 0 ? Math.round(Number(device.vram_gb)) : "—";
    const model = device.model || i18n.t("unknownModel");
    const busy = online && Boolean(device.busy);
    const state = busy ? "busy" : (online ? "connected" : "offline");
    const stateLabel = busy ? "busyStatus" : (online ? "connectedStatus" : "offlineStatus");
    const modelControl = online && !busy
      ? `<a class="device-model-link" href="/chat/?model=${encodeURIComponent(model)}&host=${encodeURIComponent(device.id)}" aria-label="${escapeText(i18n.t("useThisDevice"))}"><bdi>${escapeText(model)}</bdi><span aria-hidden="true">↗</span></a>`
      : `<strong class="${busy ? "device-model-busy" : ""}" ${busy ? 'aria-disabled="true"' : ""}><bdi>${escapeText(model)}</bdi>${busy ? '<span aria-hidden="true">●</span>' : ""}</strong>`;
    return `<article class="device-card ${busy ? "busy" : ""}">
      ${vendorLogo(device.gpu_vendor)}
      <div class="device-card-main">
        <div class="device-card-top"><h4 class="device-card-name">${escapeText(device.name)}</h4><span class="device-card-state"><i class="device-card-status ${state}"></i>${escapeText(i18n.t(stateLabel))}</span></div>
        <div class="device-card-gpu"><strong>${escapeText(gpu)}</strong><span class="device-vram"><b>${escapeText(vram)}</b> GB VRAM</span></div>
        <div class="device-card-model"><span>${escapeText(i18n.t(online ? "modelLabel" : "lastModelLabel"))}</span>${modelControl}</div>
      </div>
    </article>`;
  }).join("");
}

function render(payload) {
  latestPayload = payload;
  const availableModels = (payload.models || []).filter((item) => Number(item.available) > 0);
  const connected = Number(payload.stats?.connected ?? 0);
  const offline = Number(payload.stats?.offline ?? 0);
  connectedElement.textContent = i18n.number(connected);
  registeredElement.textContent = i18n.number(payload.stats?.registered ?? connected);
  modelsElement.textContent = i18n.number(availableModels.length);
  connectedDevicesCount.textContent = i18n.number(connected);
  offlineDevicesCount.textContent = i18n.number(offline);
  networkTitleElement.textContent = i18n.t(connected ? "networkAvailable" : "waitingVolunteer");
  networkSignalElement.classList.toggle("offline", connected === 0);
  modelChipsElement.innerHTML = availableModels.length
    ? availableModels.slice(0, 6).map((item) => `<a class="model-chip" href="/chat/?model=${encodeURIComponent(item.model)}"><bdi>${escapeText(item.model)}</bdi><span>${i18n.number(item.available)}</span></a>`).join("")
    : `<span class="model-chip muted">${escapeText(i18n.t("noModels"))}</span>`;
  connectedDevicesElement.innerHTML = renderDevices(payload.hosts || [], true);
  offlineDevicesElement.innerHTML = renderDevices(payload.offline_hosts || [], false);
}

async function refreshNetwork() {
  try {
    const response = await fetch("/api/hosts", { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("unavailable");
    render(await response.json());
  } catch (_) {
    [connectedElement, registeredElement, modelsElement, connectedDevicesCount, offlineDevicesCount].forEach((element) => { element.textContent = "—"; });
    networkTitleElement.textContent = i18n.t("networkError");
    networkSignalElement.classList.add("offline");
    modelChipsElement.innerHTML = `<span class="model-chip muted">${escapeText(i18n.t("retrying"))}</span>`;
    connectedDevicesElement.innerHTML = `<p class="device-empty">${escapeText(i18n.t("networkError"))}</p>`;
    offlineDevicesElement.innerHTML = `<p class="device-empty">${escapeText(i18n.t("networkError"))}</p>`;
  }
}

window.addEventListener("nawah:language-change", () => { if (latestPayload) render(latestPayload); });
refreshNetwork();
setInterval(refreshNetwork, 10000);
