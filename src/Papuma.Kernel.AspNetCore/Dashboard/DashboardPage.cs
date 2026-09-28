// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

namespace Papuma.Kernel.AspNetCore.Dashboard;

/// <summary>
/// The self-contained dashboard page: no framework, no CDN, no build step — one HTML
/// string with vanilla JS that polls <c>{path}/data</c> every 2 seconds and computes
/// rates client-side from the cumulative counters.
/// </summary>
internal static class DashboardPage
{
    public static string Html(string dataPath) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Papuma.Kernel Dashboard</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 24px; background: #0f1115; color: #d7dae0;
         font: 14px/1.5 system-ui, "Segoe UI", sans-serif; }
  h1 { font-size: 18px; margin: 0 0 4px; }
  h1 small { color: #6b7280; font-weight: normal; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .08em;
       color: #8b95a5; margin: 28px 0 10px; }
  .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: 12px; }
  .card { background: #171a21; border: 1px solid #232733; border-radius: 10px; padding: 14px 16px; }
  .card .v { font-size: 26px; font-weight: 600; color: #e8eaf0; }
  .card .l { color: #8b95a5; font-size: 12px; margin-top: 2px; }
  table { width: 100%; border-collapse: collapse; background: #171a21;
          border: 1px solid #232733; border-radius: 10px; overflow: hidden; }
  th, td { text-align: left; padding: 8px 14px; border-bottom: 1px solid #232733; }
  th { color: #8b95a5; font-size: 12px; text-transform: uppercase; letter-spacing: .05em; }
  tr:last-child td { border-bottom: none; }
  td.num { text-align: right; font-variant-numeric: tabular-nums; }
  .bar { display: inline-block; height: 8px; background: #3b82f6; border-radius: 4px;
         vertical-align: middle; margin-right: 8px; min-width: 2px; }
  .ok { color: #34d399; } .warn { color: #fbbf24; } .bad { color: #f87171; }
  .empty { color: #6b7280; font-style: italic; }
  #status { float: right; color: #6b7280; font-size: 12px; }
</style>
</head>
<body>
<div id="status">…</div>
<h1>Papuma.Kernel <small>embedded dashboard — first-look metrics, not a Prometheus replacement</small></h1>

<h2>Throughput (per second, computed between polls)</h2>
<div class="cards" id="cards"></div>

<h2>Change feed — lag per handler</h2>
<table><thead><tr><th>Handler</th><th>Kind</th><th title="Highest seq delivered to the handler">Delivered up to</th><th title="Highest seq in the feed">Head</th><th style="width:45%">Lag</th></tr></thead>
<tbody id="changeLag"></tbody></table>

<h2>Event feed — lag per handler</h2>
<table><thead><tr><th>Handler</th><th>Kind</th><th title="Highest seq delivered to the handler">Delivered up to</th><th title="Highest seq in the feed">Head</th><th style="width:45%">Lag</th></tr></thead>
<tbody id="eventLag"></tbody></table>

<h2>Failures (retry pending / poison)</h2>
<table><thead><tr><th>Feed</th><th>Handler</th><th>Seq</th><th>Attempts</th><th>Next retry</th><th>Last error</th></tr></thead>
<tbody id="failures"></tbody></table>

<h2>Handler durations (avg since start)</h2>
<table><thead><tr><th>Series</th><th class="num">Invocations</th><th class="num">Avg ms</th></tr></thead>
<tbody id="histograms"></tbody></table>

<script>
const DATA = "{{dataPath}}";
const RATE_CARDS = [
  ["papuma.session.writes", "writes/s"],
  ["papuma.session.commits", "commits/s"],
  ["papuma.session.events", "events appended/s"],
  ["papuma.session.conflicts", "conflicts/s"],
  ["papuma.feed.processed", "deliveries/s"],
  ["papuma.feed.failures", "handler failures/s"],
];
let prev = null, prevAt = 0;

function sumByPrefix(counters, prefix) {
  return Object.entries(counters)
    .filter(([k]) => k === prefix || k.startsWith(prefix + "|"))
    .reduce((acc, [, v]) => acc + v, 0);
}

function lagRows(rows, tbody) {
  const el = document.getElementById(tbody);
  if (!rows.length) { el.innerHTML = '<tr><td colspan="5" class="empty">no handlers registered</td></tr>'; return; }
  const max = Math.max(1, ...rows.map(r => r.lag));
  el.innerHTML = rows.map(r => {
    const cls = r.lag === 0 ? "ok" : r.lag < 1000 ? "warn" : "bad";
    const width = Math.max(2, Math.round(160 * r.lag / max));
    const kind = r.kind === "projection" ? `projection v${r.projectionVersion}${r.paused ? " — paused (newer deploy)" : ""}` : "effect";
    return `<tr><td>${r.handler}</td><td>${kind}</td><td class="num">${r.checkpoint.toLocaleString()}</td>` +
           `<td class="num">${r.latestSeq.toLocaleString()}</td>` +
           `<td><span class="bar" style="width:${r.lag === 0 ? 2 : width}px"></span>` +
           `<span class="${cls}">${r.lag.toLocaleString()}</span></td></tr>`;
  }).join("");
}

async function refresh() {
  try {
    const res = await fetch(DATA);
    const d = await res.json();
    const now = Date.now();

    // Rate cards: delta of cumulative counters between polls.
    const cards = document.getElementById("cards");
    cards.innerHTML = RATE_CARDS.map(([prefix, label]) => {
      const total = sumByPrefix(d.counters, prefix);
      let rate = "–";
      if (prev) {
        const delta = total - sumByPrefix(prev.counters, prefix);
        rate = (delta / ((now - prevAt) / 1000)).toFixed(1);
      }
      return `<div class="card"><div class="v">${rate}</div><div class="l">${label} <span style="float:right">Σ ${total.toLocaleString()}</span></div></div>`;
    }).join("");
    prev = d; prevAt = now;

    lagRows(d.changeFeed.lag, "changeLag");
    lagRows(d.eventFeed.lag, "eventLag");

    const failures = [
      ...d.changeFeed.failures.map(f => ({ feed: "change", ...f })),
      ...d.eventFeed.failures.map(f => ({ feed: "event", ...f })),
    ];
    document.getElementById("failures").innerHTML = failures.length
      ? failures.map(f => `<tr><td>${f.feed}</td><td>${f.handler}</td><td class="num">${f.seq}</td>` +
          `<td class="num ${f.attempts >= 5 ? "bad" : "warn"}">${f.attempts}</td>` +
          `<td>${new Date(f.nextRetryAt).toLocaleTimeString()}</td><td>${f.lastError}</td></tr>`).join("")
      : '<tr><td colspan="6" class="empty ok">none — all handlers healthy</td></tr>';

    document.getElementById("histograms").innerHTML = Object.entries(d.histograms)
      .map(([k, h]) => `<tr><td>${k}</td><td class="num">${h.count.toLocaleString()}</td><td class="num">${h.avgMs}</td></tr>`)
      .join("") || '<tr><td colspan="3" class="empty">no measurements yet</td></tr>';

    document.getElementById("status").textContent =
      "updated " + new Date(d.timestamp).toLocaleTimeString() + " · polling every 2 s";
  } catch (e) {
    document.getElementById("status").textContent = "connection lost — retrying…";
  }
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
