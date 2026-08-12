import fs from "node:fs";
import path from "node:path";
import {
  applyRowsToStreakState,
  attachStreaksToRows,
  loadStreakState,
  saveStreakState,
  streakStats,
  taipeiDateString
} from "./checkin-streaks.js";

function walkFiles(rootDir, predicate) {
  if (!fs.existsSync(rootDir)) return [];
  const files = [];
  const visit = (current) => {
    const stat = fs.statSync(current);
    if (stat.isFile()) {
      if (predicate(current)) files.push(current);
      return;
    }
    if (stat.isDirectory()) {
      for (const entry of fs.readdirSync(current)) visit(path.join(current, entry));
    }
  };
  visit(rootDir);
  return files;
}

function accountFromPath(filePath) {
  for (const part of filePath.split(/[/\\]/)) {
    const match = part.match(/^checkin-result-(\d+)$/i) || part.match(/^digen-reward-logs-token(\d+)-(.+)$/i);
    if (match) return { account: Number(match[1]), name: match[2] || null };
  }
  return { account: null, name: null };
}

function normalizeLog(record, fallback) {
  const credits = Number(record?.rewardBody?.data?.credits);
  const errMsg = record?.rewardBody?.errMsg ?? "";
  const rawStatus = record?.status ?? "unknown";
  let status = "failed";
  let message = errMsg || rawStatus;

  if (rawStatus === "reward-request-ok" || rawStatus === "reward-request-received") {
    if (errMsg === "have rewarded" || (errMsg === "success" && credits === 0)) {
      status = "already_done";
      message = errMsg === "have rewarded" ? "claimed earlier" : "success with 0 credits";
    } else if (record?.rewardBody?.errCode === 0) {
      status = credits > 0 ? "checked_in" : "already_done";
      message = credits > 0 ? "new today" : (errMsg || "claimed earlier");
    } else {
      message = errMsg || `unexpected reward body (errCode=${record?.rewardBody?.errCode})`;
    }
  } else if (rawStatus === "not-authenticated") {
    message = "not authenticated (token expired or invalid)";
  } else if (rawStatus === "error") {
    message = record?.error || message;
  }

  return {
    account: fallback.account,
    name: fallback.name || record?.account || "unknown",
    status,
    message,
    creditsDelta: Number.isFinite(credits) ? credits : null,
    profileStatus: record?.profileStatus ?? null,
    rewardStatus: record?.rewardStatus ?? null,
    rawStatus,
    finishedAt: record?.finishedAt ?? null,
    source: fallback.source
  };
}

function loadRows(rootDir) {
  const rows = [];
  const resultFiles = walkFiles(rootDir, (file) => path.basename(file) === "checkin-result.json");
  for (const file of resultFiles) {
    try {
      const parsed = JSON.parse(fs.readFileSync(file, "utf8"));
      const fromPath = accountFromPath(file);
      for (const item of (Array.isArray(parsed) ? parsed : [parsed])) {
        if (!item || typeof item !== "object") continue;
        rows.push({
          account: item.account ?? fromPath.account,
          name: item.name || fromPath.name || (item.account ? `DIGEN_TOKEN${item.account}` : "unknown"),
          status: item.status || "unknown",
          message: item.message || "",
          creditsDelta: item.creditsDelta ?? null,
          profileStatus: item.profileStatus ?? null,
          rewardStatus: item.rewardStatus ?? null,
          rawStatus: item.rawStatus ?? null,
          finishedAt: item.finishedAt ?? null,
          source: file
        });
      }
    } catch (error) {
      console.warn(`Skip invalid result JSON: ${file} (${error.message})`);
    }
  }

  if (rows.length === 0) {
    const logs = walkFiles(rootDir, (file) => path.basename(file).startsWith("api-reward-") && file.endsWith(".jsonl"));
    for (const file of logs) {
      try {
        const last = fs.readFileSync(file, "utf8").trim().split(/\r?\n/).filter(Boolean).at(-1);
        if (!last) continue;
        const fromPath = accountFromPath(file);
        rows.push(normalizeLog(JSON.parse(last), { ...fromPath, source: file }));
      } catch (error) {
        console.warn(`Skip invalid reward log: ${file} (${error.message})`);
      }
    }
  }

  const unique = new Map();
  for (const row of rows) unique.set(row.account == null ? `name:${row.name}` : `account:${row.account}`, row);
  return [...unique.values()].sort((a, b) => (a.account ?? Number.MAX_SAFE_INTEGER) - (b.account ?? Number.MAX_SAFE_INTEGER));
}

function escapeCell(value) {
  return String(value ?? "").replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
}

function compactMessage(value, maxLength = 160) {
  const message = String(value ?? "").replace(/\s+/g, " ").trim();
  return message.length > maxLength ? `${message.slice(0, maxLength - 1)}…` : message;
}

function shortLabel(row) {
  return row.name || (row.account == null ? "unknown" : `DIGEN_TOKEN${row.account}`);
}

function statusBadge(status) {
  return ({ checked_in: "✅ checked_in", already_done: "☑️ already_done", skipped: "⏭️ skipped", failed: "❌ failed" })[status] || escapeCell(status);
}

function fmtCredits(value) {
  if (value === null || value === undefined || value === "") return "—";
  const number = Number(value);
  if (!Number.isFinite(number)) return "—";
  return number > 0 ? `+${number}` : String(number);
}

function fmtStreak(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? `${number}d` : "0";
}

function noteForRow(row) {
  if (row.status === "checked_in" && Number(row.creditsDelta) > 0) return "new today";
  if (row.status === "already_done") return "claimed earlier";
  return compactMessage(row.message || row.rawStatus || row.status);
}

function buildMarkdown(rows, meta) {
  const counts = {
    total: rows.length,
    checked_in: rows.filter((row) => row.status === "checked_in").length,
    already_done: rows.filter((row) => row.status === "already_done").length,
    failed: rows.filter((row) => row.status === "failed").length,
    skipped: rows.filter((row) => row.status === "skipped").length
  };
  counts.ok = counts.checked_in + counts.already_done;
  const configured = counts.ok + counts.failed;
  const gained = rows.reduce((sum, row) => sum + Math.max(0, Number(row.creditsDelta) || 0), 0);
  const activeRows = rows.filter((row) => row.status !== "skipped");
  const failedRows = activeRows.filter((row) => row.status === "failed");
  const skippedRows = rows.filter((row) => row.status === "skipped");
  const maxRows = activeRows.filter((row) => meta.streakStats.maxStreak > 0 && Number(row.streak) === meta.streakStats.maxStreak);
  const headline = counts.failed > 0
    ? `❌ ${counts.failed} configured account(s) need attention`
    : configured > 0
      ? `✅ ${configured}/${configured} configured account(s) completed without errors`
      : "⏭️ No configured accounts";

  const lines = [
    "## Digen daily login reward",
    "",
    `**${headline}**`,
    "",
    "| Metric | Count |",
    "| --- | ---: |",
    `| Configured (ran) | ${configured} |`,
    `| New check-in | ${counts.checked_in} |`,
    `| Already done | ${counts.already_done} |`,
    `| OK total | ${counts.ok} |`,
    `| Failed | ${counts.failed} |`,
    `| Skipped (no secret) | ${counts.skipped} |`,
    `| Credits gained this run | +${gained} |`,
    `| Accounts with streak | ${meta.streakStats.accountsWithStreak} |`,
    `| Max continuous days | ${meta.streakStats.maxStreak} |`,
    `| Avg continuous days | ${meta.streakStats.avgStreak} |`,
    "",
    `- Generated at: \`${meta.generatedAt}\``,
    `- Streak date (Asia/Taipei): \`${meta.asOfDate}\``,
    meta.runUrl ? `- Workflow run: ${meta.runUrl}` : null,
    maxRows.length ? `- Longest streak: **${meta.streakStats.maxStreak} day(s)** — ${maxRows.map(shortLabel).join(", ")}` : null,
    ""
  ].filter((line) => line !== null);

  if (failedRows.length) {
    lines.push("### ❌ Needs attention", "", "| # | Account | Error |", "| ---: | --- | --- |");
    for (const row of failedRows) lines.push(`| ${row.account ?? "?"} | ${escapeCell(shortLabel(row))} | ${escapeCell(compactMessage(row.message || "failed"))} |`);
    lines.push("");
  }
  if (activeRows.length) {
    lines.push("### Account results", "", "| # | Account | Status | Reward | Streak | Best | Note |", "| ---: | --- | --- | ---: | ---: | ---: | --- |");
    for (const row of activeRows) lines.push(`| ${row.account ?? "?"} | ${escapeCell(shortLabel(row))} | ${statusBadge(row.status)} | ${fmtCredits(row.creditsDelta)} | ${fmtStreak(row.streak)} | ${fmtStreak(row.longestStreak)} | ${escapeCell(noteForRow(row))} |`);
    lines.push("");
  }
  if (skippedRows.length) {
    lines.push("### Skipped", "", `No secret / token: **#${skippedRows.map((row) => row.account ?? shortLabel(row)).join(", ")}**`, "");
  }
  if (configured === 0) lines.push("### Next step", "", "Add GitHub Secrets `DIGEN_TOKEN1` through `DIGEN_TOKEN33` before the next run.", "");
  lines.push("---", "", "<sub>Status: `checked_in` = claimed this run · `already_done` = already claimed today · `failed` = token or API issue · `skipped` = secret not configured</sub>", "");
  return { markdown: `${lines.join("\n")}\n`, counts, gained };
}

function main() {
  const inputDir = process.argv[2] || path.join(process.cwd(), "collected");
  const outDir = process.env.DIGEN_SUMMARY_DIR || path.join(process.cwd(), "artifacts");
  const streakStatePath = process.env.DIGEN_STREAK_STATE || path.join(process.cwd(), "streak-state", "checkin-streaks.json");
  const rows = loadRows(inputDir);
  if (!rows.length) {
    const message = `No check-in result JSON or api-reward logs found under ${inputDir}`;
    console.error(message);
    if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## Digen daily login reward\n\n❌ ${message}\n`, "utf8");
    process.exitCode = 1;
    return;
  }

  const asOfDate = process.env.DIGEN_STREAK_DATE || taipeiDateString();
  const streakState = applyRowsToStreakState(loadStreakState(streakStatePath), rows, asOfDate);
  const enrichedRows = attachStreaksToRows(rows, streakState);
  const serverUrl = process.env.GITHUB_SERVER_URL || "https://github.com";
  const runUrl = process.env.GITHUB_REPOSITORY && process.env.GITHUB_RUN_ID
    ? `${serverUrl}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}` : null;
  const generatedAt = new Date().toISOString();
  const { markdown, counts, gained } = buildMarkdown(enrichedRows, { generatedAt, asOfDate, runUrl, streakStats: streakStats(streakState) });

  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, "checkin-daily-summary.md"), markdown, "utf8");
  fs.writeFileSync(path.join(outDir, "checkin-daily-summary.json"), `${JSON.stringify({ generatedAt, asOfDate, timezone: "Asia/Taipei", runUrl, counts, gained, rows: enrichedRows }, null, 2)}\n`, "utf8");
  fs.writeFileSync(path.join(outDir, "checkin-streaks.json"), `${JSON.stringify(streakState, null, 2)}\n`, "utf8");
  saveStreakState(streakStatePath, streakState);

  console.log("----- GITHUB SUMMARY (markdown) -----");
  console.log(markdown);
  console.log("----- END GITHUB SUMMARY -----");
  if (process.env.GITHUB_STEP_SUMMARY) fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, markdown, "utf8");
  if (counts.failed > 0 && process.env.DIGEN_FAIL_ON_FAILED !== "0") {
    console.error(`Daily summary detected problems: ${counts.failed} account(s) failed`);
    process.exitCode = 1;
  }
}

main();
