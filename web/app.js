const conversation = document.querySelector("#conversation");
const emptyState = document.querySelector("#chatEmpty");
const form = document.querySelector("#promptForm");
const promptElement = document.querySelector("#prompt");
const submitButton = document.querySelector("#submitButton");
const formMessage = document.querySelector("#formMessage");
const charCount = document.querySelector("#charCount");
const modelSelect = document.querySelector("#modelSelect");
const modelAvailability = document.querySelector("#modelAvailability");
const modelStatusDot = document.querySelector("#modelStatusDot");
const chatScroller = document.querySelector(".chat-main");
const i18n = window.NawahI18n;

const query = new URLSearchParams(location.search);
let selectedModel = query.get("model") || "";
const selectedHostId = query.get("host") || "";
let modelAvailabilityCount = 0;
let activeJobId = null;
let eventSource = null;
let eventsHealthy = false;
let refreshingJob = false;
const jobs = new Map();
const panels = new Map();

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    const validation = Array.isArray(payload.detail) ? payload.detail[0]?.msg : payload.detail;
    throw new Error(payload.error || validation || i18n.t("unexpectedError"));
  }
  return payload;
}

function escapeText(value) {
  const span = document.createElement("span");
  span.textContent = value ?? "";
  return span.innerHTML;
}

const languageKeywords = {
  javascript: "as async await break case catch class const continue debugger default delete do else export extends false finally for from function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield",
  typescript: "abstract any as asserts async await bigint boolean break case catch class const constructor continue declare default delete do else enum export extends false finally for from function get if implements import in infer instanceof interface is keyof let module namespace never new null number object of private protected public readonly require return set static string super switch symbol this throw true try type typeof undefined unique unknown var void while with yield",
  python: "and as assert async await break class continue def del elif else except false finally for from global if import in is lambda none nonlocal not or pass raise return true try while with yield",
  php: "abstract and array as break callable case catch class clone const continue declare default die do echo else elseif empty extends final finally fn for foreach function global goto if implements include include_once instanceof interface isset list match namespace new null or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield",
  css: "important inherit initial revert unset auto none block inline flex grid absolute relative fixed sticky hidden visible",
  sql: "add all alter and any as asc backup between by case check column constraint create database default delete desc distinct drop exec exists foreign from full group having in index inner insert into is join key left like limit not null on or order outer primary procedure right select set table top truncate union unique update values view where",
  csharp: "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while",
  java: "abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new null package private protected public return short static strictfp super switch synchronized this throw throws transient true try void volatile while",
  json: "true false null",
  bash: "case do done elif else esac fi for function if in select then time until while",
};

const languageAliases = { js: "javascript", jsx: "javascript", ts: "typescript", tsx: "typescript", py: "python", cs: "csharp", "c#": "csharp", sh: "bash", shell: "bash", html: "markup", xml: "markup" };

function highlightCode(code, language) {
  const normalized = languageAliases[String(language || "").toLowerCase()] || String(language || "").toLowerCase();
  const keywords = (languageKeywords[normalized] || "").split(" ").filter(Boolean).join("|");
  const hashComment = ["python", "bash", "php"].includes(normalized) ? "|#[^\\n]*" : "";
  const markupTag = normalized === "markup" ? "|<\\/?[A-Za-z][^>]*>" : "";
  const pattern = new RegExp(
    `(?<comment>\\/\\*[\\s\\S]*?\\*\\/|\\/\\/[^\\n]*|<!--[\\s\\S]*?-->${hashComment})` +
    `|(?<string>\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|\`(?:\\\\.|[^\`\\\\])*\`)` +
    `|(?<tag>${markupTag ? markupTag.slice(1) : "(?!)"})` +
    `|(?<number>\\b(?:0x[\\da-f]+|\\d+(?:\\.\\d+)?)\\b)` +
    `|(?<keyword>${keywords ? `\\b(?:${keywords})\\b` : "(?!)"})` +
    `|(?<function>\\b[A-Za-z_$][\\w$]*(?=\\s*\\())` +
    `|(?<operator>[{}\\[\\]();,.<>:+\\-*\\/=!&|?%]+)`, "gi"
  );
  let output = "";
  let cursor = 0;
  for (const match of String(code || "").matchAll(pattern)) {
    output += escapeText(code.slice(cursor, match.index));
    const tokenType = Object.keys(match.groups).find((key) => match.groups[key] !== undefined) || "plain";
    output += `<span class="token-${tokenType}">${escapeText(match[0])}</span>`;
    cursor = match.index + match[0].length;
  }
  return output + escapeText(code.slice(cursor));
}

function renderAssistantContent(value) {
  return String(value || "").split("```").map((segment, index) => {
    if (index % 2 === 0) {
      const safe = escapeText(segment)
        .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
        .replace(/`([^`]+)`/g, '<code class="inline-code">$1</code>')
        .replace(/\n/g, "<br>");
      return safe ? `<div class="answer-text" dir="auto">${safe}</div>` : "";
    }
    const newline = segment.indexOf("\n");
    const language = newline >= 0 ? segment.slice(0, newline).trim() : "code";
    const code = newline >= 0 ? segment.slice(newline + 1) : segment;
    return `<div class="code-block" dir="ltr"><div class="code-header"><span class="code-label">${escapeText(language || "code")}</span><button type="button" data-code-copy>${escapeText(i18n.t("copy"))}</button></div><pre><code>${highlightCode(code, language)}</code></pre></div>`;
  }).join("");
}

function renderUsage(usage) {
  if (!usage) return "";
  const number = (value) => value === null || value === undefined ? "—" : i18n.number(value);
  const speed = usage.tokens_per_second === null || usage.tokens_per_second === undefined ? "—" : `${number(usage.tokens_per_second)} ${i18n.t("tokenPerSec")}`;
  return `<div class="usage-bar" aria-label="${escapeText(i18n.t("tokenInfo"))}">
    <span class="usage-metric">${escapeText(i18n.t("inputTokens"))} <strong>${number(usage.prompt_tokens)}</strong></span>
    <span class="usage-metric">${escapeText(i18n.t("outputTokens"))} <strong>${number(usage.completion_tokens)}</strong></span>
    <span class="usage-metric">${escapeText(i18n.t("totalTokens"))} <strong>${number(usage.total_tokens)}</strong></span>
    <span class="usage-metric">${escapeText(i18n.t("speed"))} <strong>${speed}</strong></span>
    <span class="usage-source">${escapeText(i18n.t(usage.estimated ? "estimated" : "engineUsage"))}</span>
  </div>`;
}

function renderActions() {
  return `<div class="message-actions">
    <button class="message-action" type="button" data-action="copy"><svg viewBox="0 0 24 24"><rect x="8" y="8" width="11" height="11" rx="2"></rect><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"></path></svg>${escapeText(i18n.t("copy"))}</button>
    <span class="action-feedback" aria-live="polite"></span>
  </div>`;
}

function renderReasoning(value) {
  if (!value) return "";
  return `<details class="reasoning-panel" open><summary>${escapeText(i18n.t("reasoningLive"))}</summary><div dir="auto">${escapeText(value).replace(/\n/g, "<br>")}</div></details>`;
}

function extractCompatibilityReasoning(job) {
  let thinking = String(job.thinking || "");
  const result = String(job.result || "").replace(/\[\[AI_PALM_REASONING_BASE64:([A-Za-z0-9+/=]+)\]\]/g, (_, encoded) => {
    try {
      const bytes = Uint8Array.from(atob(encoded), (character) => character.charCodeAt(0));
      thinking += new TextDecoder().decode(bytes);
    } catch { /* Leave an invalid compatibility marker out of the visible answer. */ }
    return "";
  });
  return { ...job, result, thinking };
}

function addTurn(job) {
  emptyState?.remove();
  conversation.insertAdjacentHTML("beforeend", `<article class="message user"><span class="message-avatar">${escapeText(i18n.t("you"))}</span><div class="message-body"><p class="message-label">${escapeText(i18n.t("yourMessage"))}</p><p class="user-prompt">${escapeText(job.prompt)}</p></div></article>`);
  conversation.insertAdjacentHTML("beforeend", `<article class="message assistant" data-job-id="${escapeText(job.id)}"><span class="message-avatar"><img src="/assets/ai-palm-mark.png" alt="" /></span><div class="message-body"><p class="message-label"><bdi>${escapeText(job.model || selectedModel)}</bdi> ${escapeText(i18n.t("viaNawah"))}</p><div class="assistant-panel"><div class="waiting-row"><span class="typing-dots"><i></i><i></i><i></i></span><span>${escapeText(i18n.t("waitingDevice"))}</span></div></div></div></article>`);
  panels.set(job.id, conversation.querySelector(`[data-job-id="${CSS.escape(job.id)}"] .assistant-panel`));
  scrollToLatest(true);
}

function scrollToLatest(force = false) {
  const nearBottom = chatScroller.scrollHeight - chatScroller.scrollTop - chatScroller.clientHeight < 170;
  if (force || nearBottom) chatScroller.scrollTo({ top: chatScroller.scrollHeight, behavior: force ? "smooth" : "auto" });
}

function renderJob(job) {
  job = extractCompatibilityReasoning(job);
  jobs.set(job.id, job);
  const panel = panels.get(job.id);
  if (!panel) return;
  if (job.status === "completed") {
    panel.innerHTML = `${renderReasoning(job.thinking)}<div class="assistant-answer">${renderAssistantContent(job.result)}</div>${renderActions()}${renderUsage(job.usage)}`;
    finishJob();
  } else if (job.status === "failed") {
    panel.innerHTML = `<p class="assistant-answer error-answer">${escapeText(i18n.t("requestFailed"))}: ${escapeText(job.error || i18n.t("unknownError"))}</p>${renderUsage(job.usage)}`;
    finishJob();
  } else if (job.status === "running" && job.result) {
    panel.innerHTML = `${renderReasoning(job.thinking)}<div class="assistant-answer">${renderAssistantContent(job.result)}<span class="stream-caret" aria-hidden="true"></span></div>${renderActions()}${renderUsage(job.usage)}`;
  } else if (job.status === "running" && job.thinking) {
    panel.innerHTML = `${renderReasoning(job.thinking)}<div class="waiting-row"><span class="typing-dots"><i></i><i></i><i></i></span><span>${escapeText(i18n.t("modelThinking"))}</span></div>`;
  } else {
    panel.innerHTML = `<div class="waiting-row"><span class="typing-dots"><i></i><i></i><i></i></span><span>${escapeText(job.note || i18n.t(job.status === "running" ? "modelThinking" : "waitingDevice"))}</span></div>`;
  }
  scrollToLatest();
}

function finishJob() {
  activeJobId = null;
  closeEvents();
  refreshModels();
}

function closeEvents() {
  eventSource?.close();
  eventSource = null;
  eventsHealthy = false;
}

function watchJob(jobId) {
  closeEvents();
  if (!window.EventSource) return;
  const source = new EventSource(`/api/jobs/${encodeURIComponent(jobId)}/events`);
  eventSource = source;
  source.onopen = () => { if (eventSource === source) eventsHealthy = true; };
  source.onmessage = (event) => {
    if (eventSource !== source) return;
    try { const payload = JSON.parse(event.data); if (payload?.job) renderJob(payload.job); }
    catch (_) { closeEvents(); }
  };
  source.onerror = () => { if (eventSource === source) closeEvents(); };
}

async function refreshJob() {
  if (!activeJobId || refreshingJob || eventsHealthy) return;
  refreshingJob = true;
  try { renderJob((await fetchJson(`/api/jobs/${activeJobId}`)).job); }
  catch (error) { setFormMessage(error.message, true); }
  finally { refreshingJob = false; }
}

function setFormMessage(message, error = false) {
  formMessage.textContent = message;
  formMessage.classList.toggle("error", error);
}

function updateComposerState() {
  submitButton.disabled = Boolean(activeJobId) || !selectedModel || modelAvailabilityCount < 1 || !promptElement.value.trim();
}

async function refreshModels() {
  try {
    const payload = await fetchJson("/api/hosts");
    const selectedHost = selectedHostId ? (payload.hosts || []).find((item) => item.id === selectedHostId) : null;
    const models = selectedHost
      ? [{ model: selectedHost.model, connected: 1, available: selectedHost.busy ? 0 : 1 }]
      : (payload.models || []).filter((item) => Number(item.connected) > 0);
    if (selectedHost) selectedModel = selectedHost.model;
    if (!models.some((item) => item.model === selectedModel)) selectedModel = models.find((item) => Number(item.available) > 0)?.model || models[0]?.model || "";
    modelSelect.innerHTML = models.length
      ? models.map((item) => `<option value="${escapeText(item.model)}" ${item.model === selectedModel ? "selected" : ""}>${escapeText(item.model)}</option>`).join("")
      : `<option value="">${escapeText(i18n.t("noConnectedModels"))}</option>`;
    modelSelect.disabled = Boolean(selectedHostId) || !models.length || Boolean(activeJobId);
    const selected = models.find((item) => item.model === selectedModel);
    modelAvailabilityCount = Number(selected?.available || 0);
    modelStatusDot.classList.toggle("online", modelAvailabilityCount > 0);
    modelAvailability.textContent = selectedHost
      ? `${selectedHost.name} · ${i18n.t(modelAvailabilityCount > 0 ? "selectedDeviceReady" : "busyNow")}`
      : (selected ? (modelAvailabilityCount > 0 ? `${i18n.number(modelAvailabilityCount)} ${i18n.t("deviceAvailable")}` : i18n.t("busyNow")) : i18n.t("noConnected"));
    setFormMessage(i18n.t(activeJobId ? "answerArriving" : (selected ? (modelAvailabilityCount ? "readySend" : "busyAuto") : "noAvailable")));
  } catch (_) {
    modelSelect.innerHTML = `<option value="">${escapeText(i18n.t("serverUnavailable"))}</option>`;
    modelSelect.disabled = true;
    modelAvailabilityCount = 0;
    modelStatusDot.classList.remove("online");
    modelAvailability.textContent = i18n.t("networkDisconnected");
    setFormMessage(i18n.t("serverUnavailable"), true);
  }
  updateComposerState();
}

async function submitPrompt(value) {
  const prompt = String(value || "").trim();
  if (!prompt || prompt.length > 2000) throw new Error(i18n.t("invalidPrompt"));
  if (!selectedModel || modelAvailabilityCount < 1) throw new Error(i18n.t("unavailableNow"));
  submitButton.disabled = true;
  setFormMessage(i18n.t("sending"));
  const payload = await fetchJson("/api/jobs", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind: "chat", prompt, model: selectedModel, ...(selectedHostId ? { target_host_id: selectedHostId } : {}) }) });
  activeJobId = payload.job.id;
  addTurn(payload.job);
  renderJob(payload.job);
  watchJob(payload.job.id);
  modelSelect.disabled = true;
  setFormMessage(i18n.t("waitingDevice"));
  return payload.job;
}

async function writeToClipboard(text) {
  if (navigator.clipboard?.writeText) return navigator.clipboard.writeText(text);
  const textarea = document.createElement("textarea");
  textarea.value = text; textarea.style.position = "fixed"; textarea.style.opacity = "0";
  document.body.appendChild(textarea); textarea.select(); const copied = document.execCommand("copy"); textarea.remove();
  if (!copied) throw new Error(i18n.t("actionFailed"));
}

function resizeComposer() {
  promptElement.style.height = "auto";
  promptElement.style.height = `${Math.min(promptElement.scrollHeight, 150)}px`;
}

promptElement.addEventListener("input", () => {
  charCount.textContent = `${promptElement.value.length} / 2000`;
  resizeComposer();
  updateComposerState();
});

promptElement.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); if (!submitButton.disabled) form.requestSubmit(); }
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const value = promptElement.value;
  try {
    await submitPrompt(value);
    promptElement.value = "";
    charCount.textContent = "0 / 2000";
    resizeComposer();
  } catch (error) {
    await refreshModels();
    setFormMessage(error.message, true);
  }
});

modelSelect.addEventListener("change", () => { selectedModel = modelSelect.value; refreshModels(); });

document.querySelectorAll("[data-prompt-ar]").forEach((button) => button.addEventListener("click", () => {
  promptElement.value = i18n.language === "en" ? button.dataset.promptEn : button.dataset.promptAr;
  promptElement.dispatchEvent(new Event("input"));
  promptElement.focus();
}));

conversation.addEventListener("click", async (event) => {
  const codeButton = event.target.closest("[data-code-copy]");
  if (codeButton) {
    const code = codeButton.closest(".code-block")?.querySelector("code")?.textContent || "";
    if (!code) return;
    try {
      await writeToClipboard(code);
      const original = codeButton.textContent;
      codeButton.textContent = i18n.t("copied");
      setTimeout(() => { codeButton.textContent = original; }, 1600);
    } catch (_) { codeButton.textContent = i18n.t("actionFailed"); }
    return;
  }
  const button = event.target.closest("[data-action]");
  const message = button?.closest("[data-job-id]");
  const job = message ? jobs.get(message.dataset.jobId) : null;
  if (!button || !job?.result) return;
  const feedback = message.querySelector(".action-feedback");
  try {
    await writeToClipboard(job.result);
    feedback.textContent = i18n.t("copied");
    setTimeout(() => { if (feedback) feedback.textContent = ""; }, 2200);
  } catch (error) { if (error?.name !== "AbortError" && feedback) feedback.textContent = i18n.t("actionFailed"); }
});

function registerAgentTools() {
  const context = document.modelContext;
  if (!context?.registerTool) return;
  try {
    Promise.resolve(context.registerTool({
      name: "submit_chat_job", title: i18n.t("toolTitle"), description: i18n.t("toolDescription"),
      inputSchema: { type: "object", properties: { prompt: { type: "string", minLength: 1, maxLength: 2000 } }, required: ["prompt"], additionalProperties: false },
      annotations: { readOnlyHint: false, untrustedContentHint: true },
      async execute(input) { const job = await submitPrompt(input?.prompt); return { jobId: job.id, status: job.status }; },
    })).catch(() => {});
  } catch (_) {}
}

registerAgentTools();
refreshModels();
resizeComposer();
window.addEventListener("nawah:language-change", () => {
  conversation.querySelectorAll(".message.user").forEach((message) => {
    const avatar = message.querySelector(".message-avatar");
    const label = message.querySelector(".message-label");
    if (avatar) avatar.textContent = i18n.t("you");
    if (label) label.textContent = i18n.t("yourMessage");
  });
  conversation.querySelectorAll(".message.assistant[data-job-id]").forEach((message) => {
    const job = jobs.get(message.dataset.jobId);
    const label = message.querySelector(".message-label");
    if (label && job) label.innerHTML = `<bdi>${escapeText(job.model || selectedModel)}</bdi> ${escapeText(i18n.t("viaNawah"))}`;
  });
  jobs.forEach((job) => renderJob(job));
  refreshModels();
});
setInterval(refreshModels, 5000);
setInterval(refreshJob, 1200);
