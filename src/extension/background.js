/**
 * Boundless Study Browser Guardian service worker.
 * Chrome and Edge only. Rules use declarativeNetRequest; no page content or
 * browsing history is read. The blocked page receives only a configured domain.
 */

const NATIVE_HOST_NAME = "com.boundlessstudy.bridge";
const EXTENSION_ID = "fbeajcbhfcpegpedfjbdmdmlndadhppn";
const HEARTBEAT_ALARM_NAME = "boundless_heartbeat_alarm";
const POLICY_EXPIRY_ALARM_NAME = "boundless_policy_expiry_alarm";
// Chromium alarms currently have a 30 second minimum period.
const HEARTBEAT_INTERVAL_MINUTES = 0.5;
const FAILSAFE_EXPIRY_MS = 30000;
const MAX_POLICY_DOMAINS = 1000;
const RULE_PRIORITY_BLOCK = 1;
const RULE_PRIORITY_ALLOW = 2;

const BROWSER_FAMILY = /Edg\//.test(navigator.userAgent)
  ? "edge"
  : (/Chrome\//.test(navigator.userAgent) ? "chrome" : "unsupported");

let nativePort = null;
let heartbeatTimerId = null;
let nativeConnectionStartedUtc = null;
let lastHeartbeatAckUtc = null;
let activeSessionId = null;
let currentPolicy = null;
let isFailsafeActive = false;
let policyApplyQueue = Promise.resolve();

chrome.runtime.onInstalled.addListener(start);
chrome.runtime.onStartup.addListener(start);

function start() {
  chrome.alarms.create(HEARTBEAT_ALARM_NAME, { periodInMinutes: HEARTBEAT_INTERVAL_MINUTES });
  ensureNativeConnection();
}

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === HEARTBEAT_ALARM_NAME) onHeartbeatTick();
  if (alarm.name === POLICY_EXPIRY_ALARM_NAME) expirePolicyIfNeeded();
});

function ensureNativeConnection() {
  if (nativePort || BROWSER_FAMILY === "unsupported") return;

  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST_NAME);
    nativeConnectionStartedUtc = Date.now();
    heartbeatTimerId = setInterval(onHeartbeatTick, 15000);
    nativePort.onMessage.addListener(handleNativeMessage);
    nativePort.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError ? chrome.runtime.lastError.message : "Disconnected";
      console.warn("Native bridge disconnected:", error);
      nativePort = null;
      if (heartbeatTimerId !== null) clearInterval(heartbeatTimerId);
      heartbeatTimerId = null;
      nativeConnectionStartedUtc = null;
      enterFailsafe("AGENT_DISCONNECTED");
    });
    nativePort.postMessage({
      type: "hello",
      extensionId: EXTENSION_ID,
      version: "0.1.0",
      browserFamily: BROWSER_FAMILY
    });
  } catch (err) {
    console.error("Failed to connect to native bridge host:", err);
    nativePort = null;
    nativeConnectionStartedUtc = null;
    enterFailsafe("AGENT_DISCONNECTED");
  }
}

function sendNativeMessage(msg) {
  if (!nativePort) ensureNativeConnection();
  if (!nativePort) return false;
  try {
    nativePort.postMessage({ ...msg, extensionId: EXTENSION_ID, browserFamily: BROWSER_FAMILY });
    return true;
  } catch (err) {
    console.error("Failed to post message to native bridge:", err);
    nativePort = null;
    if (heartbeatTimerId !== null) clearInterval(heartbeatTimerId);
    heartbeatTimerId = null;
    enterFailsafe("AGENT_DISCONNECTED");
    return false;
  }
}

function handleNativeMessage(msg) {
  if (!msg || typeof msg !== "object") return;

  switch (msg.type) {
    case "helloAck":
      if (!msg.success) {
        enterFailsafe("AGENT_DISCONNECTED");
        return;
      }
      lastHeartbeatAckUtc = Date.now();
      nativeConnectionStartedUtc = null;
      isFailsafeActive = false;
      requestPolicy();
      break;

    case "heartbeatAck":
      if (!msg.success) {
        checkFailsafeCondition();
        return;
      }
      lastHeartbeatAckUtc = Date.now();
      nativeConnectionStartedUtc = null;
      isFailsafeActive = false;
      if ((msg.sessionId || null) !== activeSessionId) requestPolicy();
      break;

    case "policy":
      if (!msg.success) {
        enterFailsafe("POLICY_INVALID");
        return;
      }
      void applyBrowserPolicy(msg);
      break;

    case "policyAppliedAck":
      if (!msg.success) console.warn("Agent rejected browser protection status:", msg.errorMessage);
      break;

    case "error":
      console.error("Agent bridge returned error:", msg.errorMessage);
      break;
  }
}

function requestPolicy() {
  sendNativeMessage({ type: "getPolicy" });
}

function onHeartbeatTick() {
  ensureNativeConnection();
  checkFailsafeCondition();
  sendNativeMessage({ type: "heartbeat", sessionId: activeSessionId });
  requestPolicy();
  void expirePolicyIfNeeded();
}

function checkFailsafeCondition() {
  const anchor = lastHeartbeatAckUtc || nativeConnectionStartedUtc;
  if (anchor && Date.now() - anchor > FAILSAFE_EXPIRY_MS) enterFailsafe("AGENT_DISCONNECTED");
}

function enterFailsafe(errorCode) {
  isFailsafeActive = true;
  activeSessionId = null;
  currentPolicy = null;
  lastHeartbeatAckUtc = null;
  chrome.alarms.clear(POLICY_EXPIRY_ALARM_NAME);
  return clearAllRules().then(() => {
    // Only send status when a live bridge exists; sendNativeMessage would reconnect
    // and race the hello handshake after an explicit disconnect.
    if (nativePort) reportPolicyApplied(false, null, errorCode);
  });
}

/** Accept only the same bare hostnames stored by the Domain value object. */
function normalizeDomain(raw) {
  if (typeof raw !== "string") return "";
  let domain = raw.trim().toLowerCase();
  if (domain.startsWith("www.")) domain = domain.slice(4);
  if (!domain || domain.length > 253 || domain.endsWith(".")
      || domain.includes("://") || /[\/?#@:*\s]/.test(domain)) return "";

  if (/^\d+(\.\d+){3}$/.test(domain)) {
    return domain.split(".").every((part) => Number(part) <= 255) ? domain : "";
  }

  const labels = domain.split(".");
  if (labels.length < 2 || labels.some((label) => label.length < 1 || label.length > 63
      || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(label))) return "";
  return domain;
}

function toUrlFilter(domain) {
  const normalized = normalizeDomain(domain);
  return normalized ? `||${normalized}^` : "";
}

function applyBrowserPolicy(policyMsg) {
  policyApplyQueue = policyApplyQueue
    .then(() => applyBrowserPolicyCore(policyMsg))
    .catch((err) => {
      console.error("Browser policy processing failed:", err);
      enterFailsafe("POLICY_INVALID");
    });
  return policyApplyQueue;
}

async function applyBrowserPolicyCore(policyMsg) {
  if (!policyMsg || !Array.isArray(policyMsg.blockedDomains) || !Array.isArray(policyMsg.allowedDomains)
      || policyMsg.blockedDomains.length + policyMsg.allowedDomains.length > MAX_POLICY_DOMAINS) {
    await enterFailsafe("POLICY_INVALID");
    return;
  }

  const blockedDomains = policyMsg.blockedDomains.map((domain) => {
    const normalized = normalizeDomain(domain);
    return normalized === domain ? normalized : "";
  });
  const allowedDomains = policyMsg.allowedDomains.map((domain) => {
    const normalized = normalizeDomain(domain);
    return normalized === domain ? normalized : "";
  });
  if (blockedDomains.some((domain) => !domain) || allowedDomains.some((domain) => !domain)) {
    await enterFailsafe("POLICY_INVALID");
    return;
  }

  const sessionId = policyMsg.sessionId || null;
  if (!sessionId) {
    const cleared = await clearAllRules();
    activeSessionId = null;
    currentPolicy = null;
    chrome.alarms.clear(POLICY_EXPIRY_ALARM_NAME);
    isFailsafeActive = !cleared;
    if (cleared) {
      lastHeartbeatAckUtc = Date.now();
      reportPolicyApplied(true, null, null);
    } else {
      reportPolicyApplied(false, null, "DNR_UPDATE_FAILED");
    }
    return;
  }
  if (typeof sessionId !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(sessionId)) {
    await enterFailsafe("POLICY_INVALID");
    return;
  }

  const expiresAt = policyMsg.expiresAtUtc ? new Date(policyMsg.expiresAtUtc).getTime() : NaN;
  if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) {
    const cleared = await clearAllRules();
    activeSessionId = null;
    currentPolicy = null;
    isFailsafeActive = !cleared;
    chrome.alarms.clear(POLICY_EXPIRY_ALARM_NAME);
    reportPolicyApplied(cleared, null, cleared ? "POLICY_EXPIRED" : "DNR_UPDATE_FAILED");
    return;
  }

  const nextPolicy = { sessionId, expiresAtUtc: policyMsg.expiresAtUtc, blockedDomains, allowedDomains };
  if (currentPolicy && currentPolicy.sessionId === sessionId
      && currentPolicy.expiresAtUtc === nextPolicy.expiresAtUtc
      && JSON.stringify(currentPolicy.blockedDomains) === JSON.stringify(blockedDomains)
      && JSON.stringify(currentPolicy.allowedDomains) === JSON.stringify(allowedDomains)) {
    lastHeartbeatAckUtc = Date.now();
    isFailsafeActive = false;
    reportPolicyApplied(true, sessionId, null);
    return;
  }

  const addRules = [];
  let ruleId = 1000;
  for (const domain of blockedDomains) {
    addRules.push({
      id: ruleId++,
      priority: RULE_PRIORITY_BLOCK,
      action: {
        type: "redirect",
        redirect: {
          url: `chrome-extension://${EXTENSION_ID}/blocked.html?domain=${encodeURIComponent(domain)}&session=${encodeURIComponent(sessionId)}`
        }
      },
      condition: { urlFilter: `||${domain}^`, resourceTypes: ["main_frame"] }
    });
  }
  for (const domain of allowedDomains) {
    addRules.push({
      id: ruleId++,
      priority: RULE_PRIORITY_ALLOW,
      action: { type: "allow" },
      condition: { urlFilter: `||${domain}^`, resourceTypes: ["main_frame", "sub_frame"] }
    });
  }

  try {
    const existingRules = await chrome.declarativeNetRequest.getDynamicRules();
    if (Date.now() >= expiresAt) {
      const cleared = await clearAllRules();
      activeSessionId = null;
      currentPolicy = null;
      chrome.alarms.clear(POLICY_EXPIRY_ALARM_NAME);
      reportPolicyApplied(cleared, null, cleared ? "POLICY_EXPIRED" : "DNR_UPDATE_FAILED");
      return;
    }
    await chrome.declarativeNetRequest.updateDynamicRules({
      removeRuleIds: existingRules.map((rule) => rule.id),
      addRules
    });
    activeSessionId = sessionId;
    currentPolicy = nextPolicy;
    lastHeartbeatAckUtc = Date.now();
    isFailsafeActive = false;
    chrome.alarms.create(POLICY_EXPIRY_ALARM_NAME, { when: expiresAt });
    reportPolicyApplied(true, sessionId, null);
  } catch (err) {
    console.error("Failed to update declarativeNetRequest rules:", err);
    const cleared = await clearAllRules();
    activeSessionId = null;
    currentPolicy = null;
    isFailsafeActive = true;
    reportPolicyApplied(false, sessionId, cleared ? "DNR_UPDATE_FAILED" : "DNR_UPDATE_FAILED");
  }
}

async function clearAllRules() {
  try {
    const existingRules = await chrome.declarativeNetRequest.getDynamicRules();
    const ids = existingRules.map((rule) => rule.id);
    if (ids.length) {
      await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: ids, addRules: [] });
    }
    return true;
  } catch (err) {
    console.error("Failed to clear declarativeNetRequest rules:", err);
    return false;
  }
}

function reportPolicyApplied(isApplied, sessionId, errorCode) {
  sendNativeMessage({
    type: "policyApplied",
    sessionId,
    isApplied,
    errorCode: errorCode || null
  });
}

function reportViolation(domain) {
  if (!activeSessionId) return;
  const normalized = normalizeDomain(domain);
  if (!normalized) return;
  sendNativeMessage({
    type: "reportViolation",
    sessionId: activeSessionId,
    domain: normalized,
    timestampUtc: new Date().toISOString()
  });
}

chrome.runtime.onMessage.addListener((message) => {
  if (!message || message.type !== "blockedNavigation") return;
  const domain = normalizeDomain(message.domain);
  if (!domain || !currentPolicy || currentPolicy.sessionId !== message.sessionId) return;
  if (currentPolicy.blockedDomains.includes(domain)) reportViolation(domain);
});

async function expirePolicyIfNeeded() {
  if (!currentPolicy) return;
  const expiry = new Date(currentPolicy.expiresAtUtc).getTime();
  if (!Number.isFinite(expiry) || Date.now() >= expiry) {
    const cleared = await clearAllRules();
    activeSessionId = null;
    currentPolicy = null;
    isFailsafeActive = !cleared;
    reportPolicyApplied(cleared, null, cleared ? "POLICY_EXPIRED" : "DNR_UPDATE_FAILED");
  } else {
    chrome.alarms.create(POLICY_EXPIRY_ALARM_NAME, { when: expiry });
  }
}
