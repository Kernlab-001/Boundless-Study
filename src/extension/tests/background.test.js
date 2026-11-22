import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(here, "..", "background.js"), "utf8");

function event() {
  const listeners = [];
  return {
    addListener(listener) { listeners.push(listener); },
    async fire(...args) {
      for (const listener of listeners) await listener(...args);
    }
  };
}

function harness(userAgent = "Chrome/130.0") {
  const sent = [];
  const alarms = new Map();
  let rules = [];
  let failNextUpdate = false;
  const port = { onMessage: event(), onDisconnect: event(), postMessage: (message) => sent.push(message) };
  const api = {
    runtime: {
      onInstalled: event(), onStartup: event(), onMessage: event(),
      connectNative: () => port
    },
    alarms: {
      onAlarm: event(),
      create: (name, info) => alarms.set(name, info),
      clear: (name) => alarms.delete(name)
    },
    declarativeNetRequest: {
      getDynamicRules: async () => rules.map((rule) => structuredClone(rule)),
      updateDynamicRules: async ({ removeRuleIds, addRules }) => {
        if (failNextUpdate) {
          failNextUpdate = false;
          throw new Error("synthetic update failure");
        }
        const remove = new Set(removeRuleIds);
        rules = rules.filter((rule) => !remove.has(rule.id)).concat(structuredClone(addRules));
      }
    }
  };
  const context = {
    chrome: api,
    navigator: { userAgent },
    console: { log() {}, warn() {}, error() {} },
    Date,
    URLSearchParams,
    encodeURIComponent,
    Promise,
    setTimeout,
    clearTimeout,
    setInterval: () => 1,
    clearInterval() {}
  };
  vm.runInNewContext(source, context, { filename: "background.js" });
  return {
    context,
    sent,
    rules: () => rules,
    alarms,
    port,
    failNextRuleUpdate() { failNextUpdate = true; }
  };
}

const activePolicy = {
  success: true,
  sessionId: "11111111-2222-4333-8444-555555555555",
  expiresAtUtc: new Date(Date.now() + 5 * 60_000).toISOString(),
  blockedDomains: ["youtube.com"],
  allowedDomains: ["docs.google.com"]
};

test("Chrome policy update installs block and allow rules and acknowledges actual application", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);

  assert.equal(h.rules().length, 2);
  assert.equal(h.rules()[0].condition.urlFilter, "||youtube.com^");
  assert.match(h.rules()[0].action.redirect.url, /domain=youtube.com/);
  assert.match(h.rules()[0].action.redirect.url, /session=11111111-2222-4333-8444-555555555555/);
  assert.equal(h.rules()[1].priority, 2);
  const ack = h.sent.find((message) => message.type === "policyApplied");
  assert.equal(ack.isApplied, true);
  assert.equal(ack.browserFamily, "chrome");
  assert.equal(ack.sessionId, "11111111-2222-4333-8444-555555555555");
});

test("Edge sends its detected browser family in the native hello", async () => {
  const h = harness("Mozilla/5.0 Edg/130.0");
  await h.context.start();
  assert.equal(h.sent[0].type, "hello");
  assert.equal(h.sent[0].browserFamily, "edge");
});

test("each heartbeat tick requests a fresh policy to reconcile same-session exceptions", async () => {
  const h = harness();
  await h.context.start();
  h.sent.length = 0;
  h.context.onHeartbeatTick();
  assert.deepEqual(h.sent.map((message) => message.type), ["heartbeat", "getPolicy"]);
});

test("domain policy validation rejects URL fragments, ports and malformed host labels", () => {
  const h = harness();
  assert.equal(h.context.normalizeDomain("www.youtube.com"), "youtube.com");
  assert.equal(h.context.normalizeDomain("https://youtube.com/path"), "");
  assert.equal(h.context.normalizeDomain("youtube.com:443"), "");
  assert.equal(h.context.normalizeDomain("bad..example"), "");
});

test("policy with no session clears persisted DNR rules", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.context.applyBrowserPolicy({ success: true, sessionId: null, blockedDomains: [], allowedDomains: [] });
  assert.deepEqual(h.rules(), []);
  const ack = h.sent.filter((message) => message.type === "policyApplied").at(-1);
  assert.equal(ack.isApplied, true);
  assert.equal(ack.sessionId, null);
});

test("same-session allowed-site exception changes replace the applied rules", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.context.applyBrowserPolicy({
    ...activePolicy,
    allowedDomains: ["docs.google.com", "school.example"]
  });
  assert.equal(h.rules().length, 3);
  assert.equal(h.rules()[2].condition.urlFilter, "||school.example^");
  const acks = h.sent.filter((message) => message.type === "policyApplied");
  assert.equal(acks.at(-1).isApplied, true);
});

test("expired policy removes rules immediately", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.context.applyBrowserPolicy({
    ...activePolicy,
    expiresAtUtc: new Date(Date.now() - 1000).toISOString()
  });
  assert.deepEqual(h.rules(), []);
});

test("malformed policy clears rules and reports that requested protection was not applied", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.context.applyBrowserPolicy({ ...activePolicy, blockedDomains: ["https://bad.test/path"] });
  assert.deepEqual(h.rules(), []);
  const ack = h.sent.filter((message) => message.type === "policyApplied").at(-1);
  assert.equal(ack.isApplied, false);
  assert.equal(ack.errorCode, "POLICY_INVALID");
});

test("blocked page reports only a configured domain for the matching session", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.context.chrome.runtime.onMessage.fire({
    type: "blockedNavigation", domain: "youtube.com", sessionId: "11111111-2222-4333-8444-555555555555"
  });
  const violation = h.sent.find((message) => message.type === "reportViolation");
  assert.equal(violation.domain, "youtube.com");
  assert.equal(violation.sessionId, "11111111-2222-4333-8444-555555555555");
  assert.equal("url" in violation, false);

  await h.context.chrome.runtime.onMessage.fire({
    type: "blockedNavigation", domain: "private.example", sessionId: "11111111-2222-4333-8444-555555555555"
  });
  assert.equal(h.sent.filter((message) => message.type === "reportViolation").length, 1);
});

test("DNR update failure does not claim protection and removes partial rule state", async () => {
  const h = harness();
  h.failNextRuleUpdate();
  await h.context.applyBrowserPolicy(activePolicy);
  assert.deepEqual(h.rules(), []);
  const ack = h.sent.find((message) => message.type === "policyApplied");
  assert.equal(ack.isApplied, false);
  assert.equal(ack.errorCode, "DNR_UPDATE_FAILED");
});

test("native host disconnect clears active rules", async () => {
  const h = harness();
  await h.context.applyBrowserPolicy(activePolicy);
  await h.port.onDisconnect.fire();
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(h.rules(), []);
});
