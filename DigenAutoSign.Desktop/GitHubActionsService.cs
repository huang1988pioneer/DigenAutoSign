using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigenAutoSign.Desktop;

internal sealed class GitHubActionsService
{
    private const string Workflow = "digen-daily-reward.yml";
    private const string FallbackRepository = "huang1988pioneer/AutoSignDigen";

    public async Task TriggerAsync(string repository)
    {
        await RunGhAsync(["workflow", "run", Workflow, "--repo", repository, "--ref", "main"]);
    }

    public async Task<RunInfo?> GetLatestAsync(string repository)
    {
        var output = await RunGhAsync([
            "run", "list",
            "--workflow", Workflow,
            "--repo", repository,
            "--limit", "1",
            "--json", "databaseId,status,conclusion,createdAt,updatedAt,url"
        ]);
        return JsonSerializer.Deserialize<List<RunInfo>>(output, JsonOptions())?.FirstOrDefault();
    }

    public async Task<AccountRunStatus[]> GetAccountStatusesAsync(string repository, long runId)
    {
        try
        {
            var output = await RunGhAsync([
                "run", "view", runId.ToString(),
                "--repo", repository,
                "--json", "jobs"
            ]);
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("jobs", out var jobsElement))
                return EmptyStatuses();

            var byNumber = new Dictionary<int, AccountRunStatus>();
            foreach (var job in jobsElement.EnumerateArray())
            {
                var name = job.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                var match = Regex.Match(name, @"checkin-token-(?<number>\d+)\s*-\s*(?<alias>.+)", RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                var number = int.Parse(match.Groups["number"].Value);
                var alias = match.Groups["alias"].Value.Trim();
                var conclusion = job.TryGetProperty("conclusion", out var conclusionEl) ? conclusionEl.GetString() : null;
                var status = job.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                var display = string.IsNullOrWhiteSpace(conclusion) ? (status ?? "unknown") : conclusion;
                var configured = !string.Equals(display, "skipped", StringComparison.OrdinalIgnoreCase);

                // Matrix jobs for missing secrets often complete with success after writing skipped result.
                // Prefer job name label; mark unconfigured when conclusion is skipped or name is default accountN with skipped artifact semantics.
                byNumber[number] = new AccountRunStatus(number, alias, display, configured, null, null, null, null, null);
            }

            var streaks = await TryLoadStreaksAsync(repository, runId);
            return Enumerable.Range(1, 33)
                .Select(number =>
                {
                    var baseStatus = byNumber.GetValueOrDefault(number)
                        ?? new AccountRunStatus(number, $"account{number}", "未出現在此 run", false, null, null, null, null, null);
                    if (streaks.TryGetValue(number, out var streakInfo))
                    {
                        return baseStatus with
                        {
                            Streak = streakInfo.Streak,
                            LongestStreak = streakInfo.LongestStreak,
                            ConsecutiveSuccessDays = streakInfo.ConsecutiveSuccessDays,
                            LastSuccessAt = streakInfo.LastSuccessAt,
                            LastFailureAt = streakInfo.LastFailureAt,
                            Alias = string.IsNullOrWhiteSpace(streakInfo.Name) ? baseStatus.Alias : streakInfo.Name
                        };
                    }
                    return baseStatus;
                })
                .ToArray();
        }
        catch
        {
            return EmptyStatuses();
        }
    }

    private async Task<Dictionary<int, StreakInfo>> TryLoadStreaksAsync(string repository, long runId)
    {
        var result = new Dictionary<int, StreakInfo>();
        var tempRoot = Path.Combine(Path.GetTempPath(), "DigenAutoSign", $"run-{runId}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            // Prefer full daily summary (includes per-row streak); fall back to streak-only artifact.
            foreach (var artifactName in new[] { "checkin-daily-summary", "checkin-streaks" })
            {
                try
                {
                    await RunGhAsync([
                        "run", "download", runId.ToString(),
                        "--repo", repository,
                        "--name", artifactName,
                        "--dir", tempRoot
                    ]);
                }
                catch
                {
                    continue;
                }

                var summaryPath = Directory.EnumerateFiles(tempRoot, "checkin-daily-summary.json", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (summaryPath is not null && TryParseSummaryStreaks(summaryPath, result))
                    return result;

                var streakPath = Directory.EnumerateFiles(tempRoot, "checkin-streaks.json", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (streakPath is not null && TryParseStreakState(streakPath, result))
                    return result;
            }
        }
        catch
        {
            // Streaks are optional UI enrichment.
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        return result;
    }

    private static bool TryParseSummaryStreaks(string path, Dictionary<int, StreakInfo> into)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("rows", out var rows))
                return false;

            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty("account", out var accountEl) || accountEl.ValueKind != JsonValueKind.Number)
                    continue;
                var number = accountEl.GetInt32();
                var name = row.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var streak = row.TryGetProperty("streak", out var streakEl) && streakEl.ValueKind == JsonValueKind.Number
                    ? streakEl.GetInt32()
                    : 0;
                var longest = row.TryGetProperty("longestStreak", out var longEl) && longEl.ValueKind == JsonValueKind.Number
                    ? longEl.GetInt32()
                    : 0;
                var consecutive = row.TryGetProperty("consecutiveSuccessDays", out var consecutiveEl) && consecutiveEl.ValueKind == JsonValueKind.Number
                    ? consecutiveEl.GetInt32()
                    : streak;
                var lastSuccessAt = row.TryGetProperty("lastSuccessAt", out var successAtEl) && successAtEl.ValueKind == JsonValueKind.String
                    ? successAtEl.GetString()
                    : null;
                var lastFailureAt = row.TryGetProperty("lastFailureAt", out var failureAtEl) && failureAtEl.ValueKind == JsonValueKind.String
                    ? failureAtEl.GetString()
                    : null;
                into[number] = new StreakInfo(name, streak, longest, consecutive, lastSuccessAt, lastFailureAt);
            }

            return into.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseStreakState(string path, Dictionary<int, StreakInfo> into)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("accounts", out var accounts))
                return false;

            foreach (var prop in accounts.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var number))
                    continue;
                var entry = prop.Value;
                var name = entry.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var streak = entry.TryGetProperty("streak", out var streakEl) && streakEl.ValueKind == JsonValueKind.Number
                    ? streakEl.GetInt32()
                    : 0;
                var longest = entry.TryGetProperty("longestStreak", out var longEl) && longEl.ValueKind == JsonValueKind.Number
                    ? longEl.GetInt32()
                    : 0;
                var consecutive = entry.TryGetProperty("consecutiveSuccessDays", out var consecutiveEl) && consecutiveEl.ValueKind == JsonValueKind.Number
                    ? consecutiveEl.GetInt32()
                    : streak;
                var lastSuccessAt = entry.TryGetProperty("lastSuccessAt", out var successAtEl) && successAtEl.ValueKind == JsonValueKind.String
                    ? successAtEl.GetString()
                    : null;
                var lastFailureAt = entry.TryGetProperty("lastFailureAt", out var failureAtEl) && failureAtEl.ValueKind == JsonValueKind.String
                    ? failureAtEl.GetString()
                    : null;
                into[number] = new StreakInfo(name, streak, longest, consecutive, lastSuccessAt, lastFailureAt);
            }

            return into.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetRepositoryAsync(string? preferredWorkspace = null)
    {
        try
        {
            var args = new List<string> { "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner" };
            if (!string.IsNullOrWhiteSpace(preferredWorkspace))
            {
                // When cwd is the repo, gh can resolve it without --repo.
            }

            var output = await RunGhAsync(args, preferredWorkspace);
            var name = output.Trim();
            return string.IsNullOrWhiteSpace(name) ? FallbackRepository : name;
        }
        catch
        {
            return FallbackRepository;
        }
    }

    private static AccountRunStatus[] EmptyStatuses() =>
        Enumerable.Range(1, 33)
            .Select(number => new AccountRunStatus(number, $"account{number}", "尚未讀取", false, null, null, null, null, null))
            .ToArray();

    private static async Task<string> RunGhAsync(IEnumerable<string> arguments, string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveGhPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("無法啟動 GitHub CLI (gh)。請先安裝並執行 gh auth login。");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode == 0) return output;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
    }

    private static string ResolveGhPath()
    {
        if (!OperatingSystem.IsWindows()) return "gh";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "gh";
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };
}

internal sealed record RunInfo(
    long DatabaseId,
    string Status,
    string? Conclusion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Url);

internal sealed record AccountRunStatus(
    int Number,
    string Alias,
    string Status,
    bool IsConfigured,
    int? Streak,
    int? LongestStreak,
    int? ConsecutiveSuccessDays,
    string? LastSuccessAt,
    string? LastFailureAt);

internal sealed record StreakInfo(
    string? Name,
    int Streak,
    int LongestStreak,
    int ConsecutiveSuccessDays,
    string? LastSuccessAt,
    string? LastFailureAt);
