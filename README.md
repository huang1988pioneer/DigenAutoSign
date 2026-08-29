# Digen Auto Sign

Playwright multi-profile daily login reward helper for Digen.

## Desktop UI (Avalonia)

`DigenAutoSign.Desktop` is an Avalonia control panel modelled after [Musicful Flow](https://github.com/huang1988pioneer/AutoSignMusicful) for this repository's Node/Playwright scripts and GitHub Actions workflow.

Three main views:

1. **簽到總覽** — trigger `digen-daily-reward.yml` and refresh the latest GitHub Actions run status (requires [GitHub CLI](https://cli.github.com/) `gh auth login`)
2. **帳號設定** — edit local aliases for slots `1`–`33` (synced to `accounts.json` and mapped to `DIGEN_TOKEN1`–`DIGEN_TOKEN33`)
3. **更新登入狀態** — open a browser profile, complete Digen login manually, export `digen_token`, and copy it for the matching GitHub Secret

Prerequisites: .NET 8 SDK, Node.js, `npm install`, and (for Actions dashboard) GitHub CLI.

```bat
dotnet run --project DigenAutoSign.Desktop\DigenAutoSign.Desktop.csproj
```

The application uses the local `profiles` folder for browser sessions. Tokens are copied to the clipboard only; they are not written into `accounts.json` or the UI log.

## What Was Found

Digen's frontend includes a `LoginReward` component. When a user is logged in, that component calls:

```text
POST /v1/credit/reward?action=Login
```

The check-in script now watches for that real reward request first. If the request is not observed, it falls back to simple visible text detection.

## Install

```bat
cmd /c npm install
```

## Accounts

Edit `accounts.json` and add one entry per local profile:

```json
{
  "name": "goldshoot0720",
  "enabled": true
}
```

The name is only a local profile name. Do not put passwords in the config file.

## Login

```bat
node scripts/login.js goldshoot0720 --browser=chrome
```

Log in to Digen in the opened browser. After the account is active, return to the terminal and press Enter.

### Browser fallback order

1. **chrome** (default) — profile `profiles/<name>`
2. **edge** (primary fallback when Google blocks Chrome) — system Microsoft Edge, profile `profiles/<name>-edge`
3. **firefox** (last resort) — Playwright Firefox, profile `profiles/<name>-firefox`

If Google blocks Chrome, switch to Edge:

```bat
node scripts/login.js goldshoot0720 --browser=edge
```

If Edge is also blocked, use Firefox (first time only: install the browser build):

```bat
cmd /c npx playwright install firefox
node scripts/login.js goldshoot0720 --browser=firefox
```

Desktop UI: **更新登入狀態** → browser dropdown includes `chrome`, `edge`, and `firefox`.

Login / export / check-in / api-reward must use the **same** `--browser=` value that created the profile. Chrome, Edge, and Firefox sessions are **not** interchangeable (separate profile folders).

Notes:
- `--browser=edge` uses installed Microsoft Edge (`msedge` channel / system path).
- `--browser=firefox` uses Playwright-managed Firefox (not the system Mozilla install).

## Check In

```bat
cmd /c npm run checkin
```

To watch the browser:

```bat
cmd /c npm run checkin:headed
```

If the profile was created with Edge:

```bat
node scripts/checkin.js --headed --browser=edge
```

If the profile was created with Firefox:

```bat
node scripts/checkin.js --headed --browser=firefox
```

Results are written to `logs/checkin-YYYY-MM-DD.jsonl`.

## Direct Reward API

This calls the same endpoint the frontend uses, without clicking UI:

```bat
node scripts/api-reward.js goldshoot0720 --headed
```

The frontend API host is:

```text
https://api.digen.ai
```

For a normal Chrome Default profile, first close all Chrome windows, then start Chrome with remote debugging:

```bat
"C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222 --profile-directory="Default" https://digen.ai/zh-TW/explore
```

After Digen is logged in in that Chrome window, run:

```bat
node scripts/api-reward.js --cdp=http://127.0.0.1:9222
```

Results are written to `logs/api-reward-YYYY-MM-DD.jsonl`.

## GitHub Actions

GitHub Actions cannot use the local browser profile. For Actions, save each Digen cookie value named `digen_token` as a repository secret. For `goldshoot0720`, use:

```text
DIGEN_TOKEN1
```

For `abuhg17`, use:

```text
DIGEN_TOKEN2
```

For `fengtuprinfo`, use:

```text
DIGEN_TOKEN3
```

For `feng33feng35feng3`, use:

```text
DIGEN_TOKEN4
```

For `chbondg2`, use:

```text
DIGEN_TOKEN5
```

For `huang1988pioneer`, use:

```text
DIGEN_TOKEN6
```

For `chbondg_outloook`, use:

```text
DIGEN_TOKEN7
```

For `gaokaolevel3iptopscorer_outlook`, use:

```text
DIGEN_TOKEN8
```

For `huang1988pioneer_outloook`, use:

```text
DIGEN_TOKEN9
```

For `fengtuta_tuta`, use:

```text
DIGEN_TOKEN10
```

For `fengfence_fence`, use:

```text
DIGEN_TOKEN11
```

For `samafengtu`, use:

```text
DIGEN_TOKEN12
```

For `fengtusama`, use:

```text
DIGEN_TOKEN13
```

For `fengwithting0831`, use:

```text
DIGEN_TOKEN14
```

For `fengwithfeng1127`, use:

```text
DIGEN_TOKEN15
```

For `fengwithtu1127`, use:

```text
DIGEN_TOKEN16
```

For `akaonda333`, use:

```text
DIGEN_TOKEN17
```

For `fbussinesseng`, use:

```text
DIGEN_TOKEN18
```

For `engdictatorf`, use:

```text
DIGEN_TOKEN19
```

For `flottojackpoteng`, use:

```text
DIGEN_TOKEN20
```

For `tushenbyfengbro`, use:

```text
DIGEN_TOKEN21
```

For the remaining separate Playwright/token jobs, use:

```text
DIGEN_TOKEN22
DIGEN_TOKEN23
DIGEN_TOKEN24
DIGEN_TOKEN25
DIGEN_TOKEN26
DIGEN_TOKEN27
DIGEN_TOKEN28
DIGEN_TOKEN29
DIGEN_TOKEN30
DIGEN_TOKEN31
DIGEN_TOKEN32
DIGEN_TOKEN33
```

The workflow at `.github/workflows/digen-daily-reward.yml` runs five times daily (Asia/Taipei):

| Taipei | UTC cron |
|--------|----------|
| 05:05 (window 05:00–06:00) | `5 21 * * *` |
| 08:00 | `0 0 * * *` |
| 11:00 | `0 3 * * *` |
| 13:05 (window 13:00–14:00) | `5 5 * * *` |
| 21:05 (window 21:00–22:00) | `5 13 * * *` |

It creates one GitHub Actions job per configured token, such as `checkin-token-1 - goldshoot0720`.

All 33 account jobs are allowed to run in parallel. Before claiming, each job waits a cumulative stagger so account *N* starts a random **5–15 seconds** after account *N−1* (shared seed per workflow run). Unset token secrets are skipped. During each run, the workflow also checks configured token values for duplicates and writes a warning if two `DIGEN_TOKEN` secrets have the same value.

The workflow at `.github/workflows/check-token-secret-duplicates.yml` is a dedicated duplicate check for `DIGEN_TOKEN1` through `DIGEN_TOKEN33`. It runs daily at `20:35 UTC`, can be started manually from the Actions tab, and fails if two configured token secrets have the same value.

The workflow can also be started manually from the Actions tab.

Each matrix job writes a structured `checkin-result.json` artifact (`checkin-result-N`). After all token jobs finish, the `daily-summary` job:

1. Downloads every account result
2. Restores previous consecutive-day state (GitHub Actions cache)
3. Updates each account’s **連續簽到天數** (Asia/Taipei calendar days; `checked_in` / `already_done` count as success; same-day re-runs do not double-count; a full missed day resets the streak)
4. Builds one combined markdown/JSON report (includes consecutive success days, last successful/failed action times, and `Longest` columns)
5. Writes the table into the workflow **Job Summary** (open the `daily-summary` job)
6. Uploads `checkin-daily-summary` (`*.md` / `*.json` / `checkin-streaks.json`) and `checkin-streaks` artifacts
7. Fails the summary job if any account status is `failed`

Per-account reward logs under `logs/` are still uploaded as `digen-reward-logs-tokenN-*` artifacts.

Locally you can rebuild a summary from a folder of result JSON (or raw `api-reward-*.jsonl` artifacts):

```bat
cmd /c npm run summary -- path\to\collected-results
```

To test token mode locally:

```bat
cmd /c "set DIGEN_TOKEN1=your_token_value&& npm run api-reward -- token --token-name=DIGEN_TOKEN1"
```

```bat
cmd /c "set DIGEN_TOKEN2=your_token_value&& npm run api-reward -- token --token-name=DIGEN_TOKEN2"
```

```bat
cmd /c "set DIGEN_TOKEN3=your_token_value&& npm run api-reward -- token --token-name=DIGEN_TOKEN3"
```

```bat
cmd /c "set DIGEN_TOKEN4=your_token_value&& npm run api-reward -- token --token-name=DIGEN_TOKEN4"
```

```bat
cmd /c "set DIGEN_TOKEN5=your_token_value&& npm run api-reward -- token --token-name=DIGEN_TOKEN5"
```

Use the same command shape for `DIGEN_TOKEN6` through `DIGEN_TOKEN33`.

## Scheduler

In Windows Task Scheduler, run this command daily with this folder as the working directory:

```bat
cmd /c npm run checkin
```

## Notes

- If Digen asks for CAPTCHA, phone verification, or a fresh login, handle that manually.
- If the frontend changes, update `checkin.rewardEndpoint` in `accounts.json`.
- Make sure your usage follows Digen's terms.
