/*
 * Copyright (c) 2025-2026 Aegis AO Soft LLC and Alexander Orlov.
 * 34 Middletown Ave, Atlantic Highlands, NJ 07716
 *
 * THIS SOFTWARE IS THE CONFIDENTIAL AND PROPRIETARY INFORMATION OF
 * Aegis AO Soft LLC and Alexander Orlov.
 *
 * This code may be used, reproduced, modified, or distributed ONLY with the
 * prior written permission of Aegis AO Soft LLC / Alexander Orlov.
 *
 * Author: Alexander Orlov
 * Aegis AO Soft LLC
 */

const $ = (id) => document.getElementById(id);

const el = {
  path: $("path"), archive: $("archive"), languages: $("languages"), platform: $("platform"),
  context: $("context"), keep: $("keep"), apikey: $("apikey"),
  scan: $("scan"), translate: $("translate"), apply: $("apply"),
  setup: $("setup"), progress: $("progress"), results: $("results"),
  status: $("status-line"), log: $("log"), download: $("download"),
  summary: $("summary"), head: $("strings-head"), body: $("strings-body"),
};

let localMode = true;
let hasEnvironmentKey = false;
let currentRun = null;

init();

async function init() {
  const meta = await (await fetch("/api/meta")).json();
  localMode = meta.localMode;

  $("source-local").hidden = !localMode;
  $("source-upload").hidden = localMode;

  for (const p of meta.platforms) {
    const option = document.createElement("option");
    option.value = p.name;
    option.textContent = `${p.displayName} → ${p.defaultFormat}`;
    el.platform.append(option);
  }

  hasEnvironmentKey = meta.hasEnvironmentKey;

  if (hasEnvironmentKey) {
    el.apikey.placeholder = "using ANTHROPIC_API_KEY from your environment";
    el.apikey.closest(".field").querySelector("small").textContent =
      "Picked up from your environment. Type a key here to use a different one.";
  } else {
    // The key is the one field worth remembering between runs, and only ever in this browser.
    el.apikey.value = localStorage.getItem("anthropicKey") ?? "";
    el.apikey.addEventListener("change", () => localStorage.setItem("anthropicKey", el.apikey.value));
  }

  el.scan.addEventListener("click", () => start({ scanOnly: true }));
  el.translate.addEventListener("click", () => start({}));
  el.apply.addEventListener("click", () => {
    if (confirm("This rewrites the files in your project. Originals are backed up to .aegis-localizer/backup. Continue?"))
      start({ apply: true });
  });

  el.download.addEventListener("click", () => {
    if (currentRun) window.location = `/api/runs/${currentRun}/download`;
  });
}

function list(value) {
  return value.split(/[,;]/).map((s) => s.trim()).filter(Boolean);
}

function busy(on) {
  for (const b of [el.scan, el.translate, el.apply]) b.disabled = on;
}

async function start(options) {
  const languages = list(el.languages.value);

  if (!options.scanOnly && languages.length === 0) {
    alert("Add at least one target language, for example: ru, es");
    return;
  }
  if (!options.scanOnly && !el.apikey.value.trim() && !hasEnvironmentKey) {
    alert("An Anthropic API key is required to translate.");
    return;
  }

  busy(true);
  el.progress.hidden = false;
  el.results.hidden = true;
  el.log.textContent = "";
  el.download.hidden = true;
  el.status.textContent = "Starting…";

  try {
    let uploadId = null;

    if (!localMode) {
      const file = el.archive.files[0];
      if (!file) throw new Error("Choose a .zip of your project first.");

      el.status.textContent = "Uploading…";
      const form = new FormData();
      form.append("archive", file);

      const uploaded = await post("/api/uploads", form);
      uploadId = uploaded.uploadId;
    }

    const body = {
      path: localMode ? el.path.value.trim() : null,
      uploadId,
      languages,
      platform: el.platform.value,
      context: el.context.value.trim() || null,
      doNotTranslate: list(el.keep.value),
      scanOnly: !!options.scanOnly,
      apply: !!options.apply,
      apiKey: el.apikey.value.trim(),
    };

    const started = await post("/api/runs", JSON.stringify(body), "application/json");
    currentRun = started.runId;
    el.status.textContent = "Running…";
    follow(started.runId);
  } catch (err) {
    el.status.innerHTML = `<span class="error">${escapeHtml(err.message)}</span>`;
    busy(false);
  }
}

async function post(url, body, contentType) {
  const response = await fetch(url, {
    method: "POST",
    body,
    headers: contentType ? { "content-type": contentType } : undefined,
  });

  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error ?? `Request failed (${response.status})`);
  return payload;
}

function follow(runId) {
  const stream = new EventSource(`/api/runs/${runId}/stream`);

  stream.onmessage = (message) => {
    const e = JSON.parse(message.data);
    const text = e.kind === "progress" ? `  ${e.message} ${e.completed}/${e.total}` : e.message;

    const line = document.createElement("span");
    if (e.kind === "warn" || e.kind === "detail") line.className = e.kind;
    line.textContent = text + "\n";

    el.log.append(line);
    el.log.scrollTop = el.log.scrollHeight;
  };

  stream.addEventListener("done", (message) => {
    stream.close();
    busy(false);
    render(JSON.parse(message.data));
  });

  stream.onerror = () => {
    // EventSource reconnects on its own; the cursor-based replay makes that safe, so only a
    // closed stream is worth reporting.
    if (stream.readyState === EventSource.CLOSED) {
      el.status.innerHTML = '<span class="error">Lost the connection to the run.</span>';
      busy(false);
    }
  };
}

function render(session) {
  if (session.status === "Failed") {
    el.status.innerHTML = `<span class="error">${escapeHtml(session.error ?? "The run failed.")}</span>`;
    return;
  }

  const r = session.result;
  el.status.textContent = r.rewrite ? "Done — sources rewritten" : "Done";
  el.download.hidden = false;
  el.results.hidden = false;

  const stats = [
    ["Files scanned", r.filesScanned],
    ["Candidates", r.candidates],
    [r.candidateList.length ? "Listed" : "Localized", r.candidateList.length || r.strings.length],
    ["Rejected as non-UI", r.rejected],
  ];

  if (r.rewrite) stats.push(["Replacements", r.rewrite.replacements], ["Left in place", r.rewrite.notRewritable]);
  stats.push(["Tokens", `${r.tokens.input.toLocaleString()} / ${r.tokens.output.toLocaleString()}`]);

  el.summary.replaceChildren(...stats.map(([label, value]) => {
    const cell = document.createElement("div");
    cell.innerHTML = `<b></b><span></span>`;
    cell.querySelector("b").textContent = value;
    cell.querySelector("span").textContent = label;
    return cell;
  }));

  r.candidateList.length ? renderCandidates(r.candidateList) : renderStrings(r.strings);
}

function renderCandidates(candidates) {
  setHead(["Where", "Kind", "String"]);

  el.body.replaceChildren(...candidates.map((c) => {
    const row = document.createElement("tr");
    row.append(
      cell(`${c.file}:${c.line}`, "where"),
      cell(c.kind, "where"),
      cell(c.text));
    return row;
  }));
}

function renderStrings(strings) {
  const languages = strings.length ? Object.keys(strings[0].translations) : [];
  setHead(["Where", "Key", "Source", ...languages]);

  el.body.replaceChildren(...strings.map((s) => {
    const row = document.createElement("tr");
    row.append(cell(`${s.file}:${s.line}`, "where"), cell(s.key, "key"));

    const source = cell(s.source);
    if (s.blocked) {
      const why = document.createElement("div");
      why.className = "blocked";
      why.textContent = `not rewritten: ${s.blocked}`;
      source.append(why);
    }
    row.append(source, ...languages.map((l) => cell(s.translations[l] ?? "")));

    return row;
  }));
}

function setHead(labels) {
  el.head.replaceChildren(...labels.map((label) => {
    const th = document.createElement("th");
    th.textContent = label;
    return th;
  }));
}

function cell(text, className) {
  const td = document.createElement("td");
  if (className) td.className = className;
  td.textContent = text;
  return td;
}

function escapeHtml(text) {
  const div = document.createElement("div");
  div.textContent = text;
  return div.innerHTML;
}
