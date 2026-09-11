const connectedElement = document.querySelector("#connectedHosts");
const registeredElement = document.querySelector("#registeredHosts");
const modelsElement = document.querySelector("#availableModels");
const networkTitleElement = document.querySelector("#networkTitle");
const networkSignalElement = document.querySelector("#networkSignal");
const i18n = window.NawahI18n;
let latestPayload = null;

function render(payload) {
  latestPayload = payload;
  const availableModels = (payload.models || []).filter((item) => Number(item.available) > 0);
  const connected = Number(payload.stats?.connected ?? 0);
  connectedElement.textContent = i18n.number(connected);
  registeredElement.textContent = i18n.number(payload.stats?.registered ?? connected);
  modelsElement.textContent = i18n.number(availableModels.length);
  networkTitleElement.textContent = i18n.t(connected ? "networkAvailable" : "waitingVolunteer");
  networkSignalElement.classList.toggle("offline", connected === 0);
}

async function refreshNetwork() {
  try {
    const response = await fetch("/api/hosts", { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("unavailable");
    render(await response.json());
  } catch (_) {
    [connectedElement, registeredElement, modelsElement].forEach((element) => { element.textContent = "—"; });
    networkTitleElement.textContent = i18n.t("networkError");
    networkSignalElement.classList.add("offline");
  }
}

window.addEventListener("nawah:language-change", () => { if (latestPayload) render(latestPayload); });
refreshNetwork();
setInterval(refreshNetwork, 15000);
