/**
 * Consecutive check-in day tracking (Asia/Taipei calendar days).
 *
 * A day counts as success when status is checked_in or already_done.
 * Same-day re-runs do not increment again. A full missed calendar day resets streak to 0.
 */

import fs from "node:fs";
import path from "node:path";

export const STREAK_TIMEZONE = "Asia/Taipei";
export const SUCCESS_STATUSES = new Set(["checked_in", "already_done"]);

export function taipeiDateString(date = new Date()) {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: STREAK_TIMEZONE,
    year: "numeric",
    month: "2-digit",
    day: "2-digit"
  }).format(date);
}

/** Previous calendar day in Asia/Taipei relative to a YYYY-MM-DD string. */
export function previousTaipeiDate(dateStr) {
  const [y, m, d] = dateStr.split("-").map(Number);
  // Noon UTC avoids DST edge cases; Taipei has no DST but keep safe.
  const utc = new Date(Date.UTC(y, m - 1, d, 12, 0, 0));
  utc.setUTCDate(utc.getUTCDate() - 1);
  return taipeiDateString(utc);
}

export function emptyStreakState() {
  return {
    timezone: STREAK_TIMEZONE,
    updatedAt: null,
    asOfDate: null,
    accounts: {}
  };
}

export function loadStreakState(filePath) {
  if (!filePath || !fs.existsSync(filePath)) {
    return emptyStreakState();
  }

  try {
    const parsed = JSON.parse(fs.readFileSync(filePath, "utf8"));
    if (!parsed || typeof parsed !== "object") return emptyStreakState();
    return {
      timezone: parsed.timezone || STREAK_TIMEZONE,
      updatedAt: parsed.updatedAt || null,
      asOfDate: parsed.asOfDate || null,
      accounts:
        parsed.accounts && typeof parsed.accounts === "object" ? parsed.accounts : {}
    };
  } catch {
    return emptyStreakState();
  }
}

export function saveStreakState(filePath, state) {
  const dir = path.dirname(filePath);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(state, null, 2)}\n`, "utf8");
}

function accountKey(account) {
  if (account == null || account === "") return null;
  return String(account);
}

function normalizePrev(entry) {
  if (!entry || typeof entry !== "object") {
    return {
      name: null,
      streak: 0,
      consecutiveSuccessDays: 0,
      longestStreak: 0,
      lastSuccessDate: null,
      lastSuccessAt: null,
      lastFailureAt: null,
      lastStatus: null,
      totalSuccessDays: 0
    };
  }

  const streak = Number(entry.streak ?? entry.consecutiveSuccessDays) || 0;
  return {
    name: entry.name ?? null,
    streak,
    consecutiveSuccessDays: streak,
    longestStreak: Number(entry.longestStreak) || 0,
    lastSuccessDate: entry.lastSuccessDate || null,
    lastSuccessAt: entry.lastSuccessAt || entry.lastSuccessfulActionAt || null,
    lastFailureAt: entry.lastFailureAt || entry.lastFailedActionAt || null,
    lastStatus: entry.lastStatus || null,
    totalSuccessDays: Number(entry.totalSuccessDays) || 0
  };
}

/**
 * Update one account's streak for a run on `today` (Taipei YYYY-MM-DD).
 * @returns {{ streak, consecutiveSuccessDays, longestStreak, lastSuccessDate, lastSuccessAt, lastFailureAt, lastStatus, totalSuccessDays, name }}
 */
export function updateAccountStreak(prevInput, { name, status, today, actionAt = null }) {
  const prev = normalizePrev(prevInput);
  const yesterday = previousTaipeiDate(today);
  const isSuccess = SUCCESS_STATUSES.has(status);
  const displayName = name || prev.name || null;
  const timestamp = actionAt || null;

  if (isSuccess) {
    let streak;
    let totalSuccessDays = prev.totalSuccessDays;

    if (prev.lastSuccessDate === today) {
      streak = Math.max(1, prev.streak);
    } else if (prev.lastSuccessDate === yesterday) {
      streak = Math.max(1, prev.streak) + 1;
      totalSuccessDays += 1;
    } else {
      streak = 1;
      totalSuccessDays += 1;
    }

    const longestStreak = Math.max(prev.longestStreak, streak);
    return {
      name: displayName,
      streak,
      consecutiveSuccessDays: streak,
      longestStreak,
      lastSuccessDate: today,
      lastSuccessAt: timestamp || prev.lastSuccessAt,
      lastFailureAt: prev.lastFailureAt,
      lastStatus: status,
      totalSuccessDays
    };
  }

  // Failed / skipped / unknown this run.
  const lastFailureAt = status === "failed"
    ? timestamp || prev.lastFailureAt
    : prev.lastFailureAt;

  if (prev.lastSuccessDate === today) {
    // Already succeeded earlier today; keep streak.
    return {
      ...prev,
      name: displayName,
      lastFailureAt,
      lastStatus: status
    };
  }

  if (prev.lastSuccessDate === yesterday) {
    // Day not closed yet — streak still valid until a full day is missed.
    return {
      ...prev,
      name: displayName,
      lastFailureAt,
      lastStatus: status
    };
  }

  // Missed at least one full calendar day (or never succeeded).
  return {
    name: displayName,
    streak: 0,
    consecutiveSuccessDays: 0,
    longestStreak: prev.longestStreak,
    lastSuccessDate: prev.lastSuccessDate,
    lastSuccessAt: prev.lastSuccessAt,
    lastFailureAt,
    lastStatus: status,
    totalSuccessDays: prev.totalSuccessDays
  };
}

/**
 * Apply today's result rows onto previous streak state.
 * @param {object} prevState
 * @param {Array<{account, name, status}>} rows
 * @param {string} [today] Taipei YYYY-MM-DD
 */
export function applyRowsToStreakState(prevState, rows, today = taipeiDateString()) {
  const state = {
    timezone: STREAK_TIMEZONE,
    updatedAt: new Date().toISOString(),
    asOfDate: today,
    accounts: { ...(prevState?.accounts || {}) }
  };

  for (const row of rows) {
    const key = accountKey(row.account);
    if (!key) continue;

    const prev = state.accounts[key];
    state.accounts[key] = updateAccountStreak(prev, {
      name: row.name,
      status: row.status,
      today,
      actionAt: row.finishedAt || row.actionAt || null
    });
  }

  return state;
}

export function attachStreaksToRows(rows, streakState) {
  return rows.map((row) => {
    const key = accountKey(row.account);
    const entry = key ? streakState.accounts[key] : null;
    return {
      ...row,
      streak: entry?.streak ?? 0,
      consecutiveSuccessDays: entry?.consecutiveSuccessDays ?? entry?.streak ?? 0,
      longestStreak: entry?.longestStreak ?? 0,
      lastSuccessDate: entry?.lastSuccessDate ?? null,
      lastSuccessAt: entry?.lastSuccessAt ?? null,
      lastFailureAt: entry?.lastFailureAt ?? null,
      totalSuccessDays: entry?.totalSuccessDays ?? 0
    };
  });
}

export function streakStats(streakState) {
  const entries = Object.values(streakState.accounts || {});
  const active = entries.filter((e) => (e.streak || 0) > 0);
  const maxStreak = entries.reduce((m, e) => Math.max(m, Number(e.streak) || 0), 0);
  const sum = active.reduce((s, e) => s + (Number(e.streak) || 0), 0);
  return {
    accountsWithStreak: active.length,
    maxStreak,
    avgStreak: active.length ? Math.round((sum / active.length) * 10) / 10 : 0
  };
}
