using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DigenAutoSign.Desktop;

public partial class MainWindow : Window
{
    private const int AccountCount = 33;
    private readonly string _workspace;
    private readonly GitHubActionsService _github = new();
    private readonly Dictionary<int, TextBox> _aliasInputs = [];
    private readonly Dictionary<int, string> _aliases;
    private string? _exportedToken;

    public MainWindow()
    {
        InitializeComponent();
        _workspace = FindWorkspace() ?? Environment.CurrentDirectory;
        _aliases = LoadAliases();
        // Fallback order when Google blocks a browser: chrome → edge → firefox.
        BrowserComboBox.ItemsSource = new[] { "chrome", "edge", "firefox" };
        BrowserComboBox.SelectedIndex = 0;
        AccountComboBox.ItemsSource = Enumerable.Range(1, AccountCount)
            .Select(i => FormatAccountLabel(i))
            .ToArray();
        BuildAliasList();
        ConfiguredMetric.Text = $"{_aliases.Count} 個";
        UpdateAccountDisplay();
    }

    private int AccountNumber => Math.Max(1, AccountComboBox.SelectedIndex + 1);
    private string ProfileName
    {
        get
        {
            if (_aliases.TryGetValue(AccountNumber, out var alias) && !string.IsNullOrWhiteSpace(alias))
                return alias.Trim();
            return $"account{AccountNumber}";
        }
    }
    private string SecretName => $"DIGEN_TOKEN{AccountNumber}";
    private string BrowserName => BrowserComboBox.SelectedItem as string ?? "chrome";
    /// <summary>
    /// chrome → profiles/name; edge → profiles/name-edge; firefox → profiles/name-firefox.
    /// Each browser keeps its own session so fallback logins do not clash.
    /// </summary>
    private string ProfileFolderName
    {
        get
        {
            if (string.Equals(BrowserName, "edge", StringComparison.OrdinalIgnoreCase))
                return $"{ProfileName}-edge";
            if (string.Equals(BrowserName, "firefox", StringComparison.OrdinalIgnoreCase))
                return $"{ProfileName}-firefox";
            return ProfileName;
        }
    }
    private static string AliasFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DigenAutoSign",
        "account-aliases.json");

    private void DashboardNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(DashboardView);
    private void AccountsNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(AccountsView);
    private void LoginNavButton_OnClick(object? sender, RoutedEventArgs e) => ShowView(LoginView);
    private void AccountComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _exportedToken = null;
        if (CopyTokenButton is not null) CopyTokenButton.IsEnabled = false;
        UpdateAccountDisplay();
    }

    private void BrowserComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Profile path depends on selected browser (edge/firefox use separate folders).
        if (ProfileStatus is not null)
            UpdateAccountDisplay();
    }

    private void ShowView(Control view)
    {
        DashboardView.IsVisible = view == DashboardView;
        AccountsView.IsVisible = view == AccountsView;
        LoginView.IsVisible = view == LoginView;
        view.BringIntoView();
    }

    private void UpdateAccountDisplay()
    {
        if (AccountComboBox is null || SecretNameText is null) return;
        if (AccountComboBox.SelectedIndex < 0) return;

        var label = _aliases.GetValueOrDefault(AccountNumber);
        SecretNameText.Text = string.IsNullOrWhiteSpace(label)
            ? SecretName
            : $"{SecretName}  ·  {label}";

        var profileDir = Path.Combine(_workspace, "profiles", ProfileFolderName);
        if (ProfileStatus is not null)
        {
            ProfileStatus.Text = Directory.Exists(profileDir)
                ? $"本機 profile：profiles/{ProfileFolderName}"
                : $"尚未建立 profile：profiles/{ProfileFolderName}（請先登入）";
        }
    }

    private string FormatAccountLabel(int number)
    {
        var alias = _aliases.GetValueOrDefault(number);
        return string.IsNullOrWhiteSpace(alias)
            ? $"帳號 {number:00}"
            : $"帳號 {number:00} · {alias}";
    }

    private void RefreshAccountComboLabels()
    {
        var selected = AccountComboBox.SelectedIndex;
        AccountComboBox.ItemsSource = Enumerable.Range(1, AccountCount)
            .Select(FormatAccountLabel)
            .ToArray();
        AccountComboBox.SelectedIndex = selected >= 0 ? selected : 0;
    }

    private async void StartLoginButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StartLoginButton.IsEnabled = false;
        CopyTokenButton.IsEnabled = false;
        _exportedToken = null;
        try
        {
            EnsureAccountInConfig(ProfileName);
            LoginStatus.Text = "正在確認 Node.js 相依套件…";
            await RunProcessAsync("npm", ["install"]);

            if (string.Equals(BrowserName, "firefox", StringComparison.OrdinalIgnoreCase))
            {
                LoginStatus.Text = "正在確認 Playwright Firefox（首次可能需下載）…";
                await RunProcessAsync("npx", ["playwright", "install", "firefox"]);
            }

            LoginStatus.Text = "瀏覽器已開啟。請完成 Digen 登入後關閉瀏覽器視窗；工具會在關閉後繼續匯出 Token。";
            await RunProcessAsync("node", [
                "scripts/login.js",
                ProfileName,
                $"--browser={BrowserName}",
                "--wait-for-close"
            ]);

            LoginStatus.Text = "正在從本機 profile 讀取 Digen Token…";
            var output = await RunProcessCaptureAsync("node", [
                "scripts/export-token.js",
                ProfileName,
                $"--browser={BrowserName}"
            ]);
            using var document = JsonDocument.Parse(ExtractJsonObject(output));
            var token = document.RootElement.GetProperty("token").GetString();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("找不到 Digen Token。請確認已成功登入。");

            _exportedToken = token;
            CopyTokenButton.IsEnabled = true;
            LoginStatus.Text = $"完成。已讀取 Token（{token.Length} 字元）；請複製後貼到 GitHub Secret {SecretName}。";
            UpdateAccountDisplay();
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"登入狀態更新失敗：{ex.Message}";
        }
        finally
        {
            StartLoginButton.IsEnabled = true;
        }
    }

    private async void CopyTokenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_exportedToken))
            {
                // Attempt live export if user already logged in earlier.
                LoginStatus.Text = "正在從本機 profile 讀取 Digen Token…";
                EnsureAccountInConfig(ProfileName);
                var output = await RunProcessCaptureAsync("node", [
                    "scripts/export-token.js",
                    ProfileName,
                    $"--browser={BrowserName}"
                ]);
                using var document = JsonDocument.Parse(ExtractJsonObject(output));
                _exportedToken = document.RootElement.GetProperty("token").GetString();
            }

            if (string.IsNullOrWhiteSpace(_exportedToken))
            {
                LoginStatus.Text = "目前帳號尚未有可複製的 Token。請先完成登入。";
                return;
            }

            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(_exportedToken);

            LoginStatus.Text = $"已複製 Token（{_exportedToken.Length} 字元）；請貼到 {SecretName}。";
            CopyTokenButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"無法複製 Token：{ex.Message}";
        }
    }

    private async void CopySecretButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(SecretName);
        LoginStatus.Text = $"已複製 {SecretName}。";
    }

    private async void TriggerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithDashboardBusy(async () =>
        {
            DashboardStatus.Text = "正在觸發 Digen Daily Reward…";
            var repository = await _github.GetRepositoryAsync(_workspace);
            await _github.TriggerAsync(repository);
            DashboardStatus.Text = "已送出簽到工作；稍後重新整理即可查看結果。";
        });
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await WithDashboardBusy(async () =>
        {
            DashboardStatus.Text = "正在讀取 GitHub Actions…";
            var repository = await _github.GetRepositoryAsync(_workspace);
            var run = await _github.GetLatestAsync(repository);
            if (run is null)
            {
                RunMetric.Text = "尚無執行紀錄";
                LastSuccessActionMetric.Text = "—";
                LastFailureActionMetric.Text = "—";
                MonthlyStreakMetric.Text = "—";
                RunTimeMetric.Text = "—";
                DashboardStatus.Text = "尚未找到 Digen Daily Reward 執行紀錄。";
                return;
            }

            RunMetric.Text = string.IsNullOrWhiteSpace(run.Conclusion) ? run.Status : run.Conclusion;
            RunTimeMetric.Text = TimeZoneInfo.ConvertTime(run.UpdatedAt, GetTaipeiZone()).ToString("MM/dd HH:mm");

            var accounts = await _github.GetAccountStatusesAsync(repository, run.DatabaseId);
            var success = accounts.Count(a => string.Equals(a.Status, "success", StringComparison.OrdinalIgnoreCase));
            var failure = accounts.Count(a => string.Equals(a.Status, "failure", StringComparison.OrdinalIgnoreCase));
            var withStreak = accounts.Where(a => (a.ConsecutiveSuccessDays ?? a.Streak ?? 0) > 0).ToArray();
            var maxStreak = accounts.Select(a => a.ConsecutiveSuccessDays ?? a.Streak ?? 0).DefaultIfEmpty(0).Max();
            LastSuccessActionMetric.Text = FormatActionTime(LatestActionTime(accounts.Select(a => a.LastSuccessAt)));
            LastFailureActionMetric.Text = FormatActionTime(LatestActionTime(accounts.Select(a => a.LastFailureAt)));
            MonthlyStreakMetric.Text = withStreak.Length > 0
                ? $"最長 {maxStreak} 天 · {withStreak.Length} 帳號有連續"
                : $"成功 {success} · 失敗 {failure}";
            RenderAccountStatuses(accounts);
            DashboardStatus.Text = $"最近執行：{run.Url}";
        });
    }

    private async Task WithDashboardBusy(Func<Task> action)
    {
        TriggerButton.IsEnabled = RefreshButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { DashboardStatus.Text = $"GitHub Actions 操作失敗：{ex.Message}"; }
        finally { TriggerButton.IsEnabled = RefreshButton.IsEnabled = true; }
    }

    private void RenderAccountStatuses(IEnumerable<AccountRunStatus> accounts)
    {
        MonthlyAccountsPanel.Children.Clear();
        foreach (var account in accounts)
        {
            var localAlias = _aliases.GetValueOrDefault(account.Number);
            var displayAlias = !string.IsNullOrWhiteSpace(localAlias)
                ? localAlias
                : account.Alias;

            var row = new StackPanel
            {
                Spacing = 3,
                Margin = new Avalonia.Thickness(0, 0, 0, 7)
            };
            var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("68,*,90,110") };
            summary.Children.Add(new TextBlock
            {
                Text = $"#{account.Number:00}",
                FontWeight = FontWeight.SemiBold
            });

            var alias = new TextBlock
            {
                Text = displayAlias,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(alias, 1);
            summary.Children.Add(alias);

            var consecutiveSuccessDays = account.ConsecutiveSuccessDays ?? account.Streak;
            var streakText = consecutiveSuccessDays is > 0
                ? $"{consecutiveSuccessDays} 天"
                : consecutiveSuccessDays is 0
                    ? "0 天"
                    : "—";
            var streakBlock = new TextBlock
            {
                Text = streakText,
                Foreground = consecutiveSuccessDays is > 0 ? Brushes.SeaGreen : Brushes.Gray,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(streakBlock, 2);
            summary.Children.Add(streakBlock);

            var isSuccess = string.Equals(account.Status, "success", StringComparison.OrdinalIgnoreCase);
            var isFailure = string.Equals(account.Status, "failure", StringComparison.OrdinalIgnoreCase);
            var state = new TextBlock
            {
                Text = account.Status,
                Foreground = isSuccess
                    ? Brushes.SeaGreen
                    : isFailure
                        ? Brushes.IndianRed
                        : Brushes.Gray,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };
            Grid.SetColumn(state, 3);
            summary.Children.Add(state);

            row.Children.Add(summary);
            row.Children.Add(new TextBlock
            {
                Text = $"連續成功 {streakText}  ·  上次成功 {FormatActionTime(account.LastSuccessAt)}  ·  上次失敗 {FormatActionTime(account.LastFailureAt)}",
                Classes = { "muted" },
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(68, 0, 0, 0)
            });
            MonthlyAccountsPanel.Children.Add(row);
        }
    }

    private void BuildAliasList()
    {
        for (var i = 1; i <= AccountCount; i++)
        {
            var box = new TextBox
            {
                Width = 350,
                Text = _aliases.GetValueOrDefault(i),
                Watermark = "帳號名稱（本機 profile / 顯示用）"
            };
            _aliasInputs[i] = box;
            var row = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 12
            };
            row.Children.Add(new TextBlock
            {
                Text = $"帳號 {i:00}",
                Width = 72,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            row.Children.Add(box);
            row.Children.Add(new TextBlock
            {
                Text = $"DIGEN_TOKEN{i}",
                FontFamily = new FontFamily("Consolas,Cascadia Mono,monospace"),
                FontSize = 12,
                Foreground = Brush.Parse("#5F736F"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            AliasPanel.Children.Add(row);
        }
    }

    private async void SaveAliasesButton_OnClick(object? sender, RoutedEventArgs e)
    {
        foreach (var (number, input) in _aliasInputs)
        {
            if (string.IsNullOrWhiteSpace(input.Text))
                _aliases.Remove(number);
            else
                _aliases[number] = input.Text.Trim();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(AliasFile)!);
        await File.WriteAllTextAsync(AliasFile, JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true }));
        WriteAccountsJson();
        ConfiguredMetric.Text = $"{_aliases.Count} 個";
        RefreshAccountComboLabels();
        UpdateAccountDisplay();
        LoginStatus.Text = "已儲存帳號別名，並同步 accounts.json。";
        DashboardStatus.Text = "已儲存帳號別名，並同步 accounts.json。";
    }

    private void EnsureAccountInConfig(string accountName)
    {
        var path = Path.Combine(_workspace, "accounts.json");
        AccountConfigFile config;
        if (File.Exists(path))
        {
            try
            {
                config = JsonSerializer.Deserialize<AccountConfigFile>(File.ReadAllText(path), CamelCase())
                    ?? new AccountConfigFile();
            }
            catch (JsonException)
            {
                config = new AccountConfigFile();
            }
        }
        else
        {
            config = new AccountConfigFile();
        }

        config.SiteUrl ??= "https://digen.ai/zh-TW/explore";
        config.Accounts ??= [];
        if (!config.Accounts.Any(a => string.Equals(a.Name, accountName, StringComparison.OrdinalIgnoreCase)))
        {
            config.Accounts.Add(new AccountConfigItem { Name = accountName, Enabled = true });
            File.WriteAllText(path, JsonSerializer.Serialize(config, CamelCaseIndented()));
        }
    }

    private void WriteAccountsJson()
    {
        var path = Path.Combine(_workspace, "accounts.json");
        AccountConfigFile existing = new();
        if (File.Exists(path))
        {
            try
            {
                existing = JsonSerializer.Deserialize<AccountConfigFile>(File.ReadAllText(path), CamelCase())
                    ?? new AccountConfigFile();
            }
            catch (JsonException)
            {
                existing = new AccountConfigFile();
            }
        }

        var accounts = _aliases
            .OrderBy(kv => kv.Key)
            .Select(kv => new AccountConfigItem { Name = kv.Value, Enabled = true })
            .ToList();

        // Keep any extra local-only accounts that are not in the numbered alias map.
        if (existing.Accounts is { Count: > 0 })
        {
            foreach (var account in existing.Accounts)
            {
                if (string.IsNullOrWhiteSpace(account.Name)) continue;
                if (accounts.Any(a => string.Equals(a.Name, account.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (_aliases.Values.Any(v => string.Equals(v, account.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                accounts.Add(account);
            }
        }

        var config = new AccountConfigFile
        {
            SiteUrl = existing.SiteUrl ?? "https://digen.ai/zh-TW/explore",
            Accounts = accounts,
            Checkin = existing.Checkin
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config, CamelCaseIndented()));
    }

    private async Task RunProcessAsync(string command, IEnumerable<string> args) =>
        _ = await RunProcessCaptureAsync(command, args);

    private async Task<string> RunProcessCaptureAsync(string command, IEnumerable<string> args)
    {
        var executable = NodeCommandPath(command);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = _workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        if (!process.Start())
            throw new InvalidOperationException($"無法啟動 {command}。");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
            throw new InvalidOperationException(message.Truncate(900));
        }
        return output;
    }

    private static Dictionary<int, string> LoadAliases()
    {
        var aliases = new Dictionary<int, string>
        {
            [1] = "goldshoot0720",
            [2] = "abuhg17",
            [3] = "fengtuprinfo",
            [4] = "feng33feng35feng3",
            [5] = "chbondg2",
            [6] = "huang1988pioneer",
            [7] = "chbondg_outloook",
            [8] = "gaokaolevel3iptopscorer_outlook",
            [9] = "huang1988pioneer_outloook",
            [10] = "fengtuta_tuta",
            [11] = "fengfence_fence",
            [12] = "samafengtu",
            [13] = "fengtusama",
            [14] = "fengwithting0831",
            [15] = "fengwithfeng1127",
            [16] = "fengwithtu1127",
            [17] = "akaonda333",
            [18] = "fbussinesseng",
            [19] = "engdictatorf",
            [20] = "flottojackpoteng",
            [21] = "tushenbyfengbro"
        };

        try
        {
            // Prefer AppData overrides.
            if (File.Exists(AliasFile))
            {
                var saved = JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(AliasFile)) ?? [];
                foreach (var (number, name) in saved)
                    aliases[number] = name;
            }

            // Merge any existing accounts.json names in list order into empty slots after known defaults.
            var accountsPath = Path.Combine(FindWorkspace() ?? Environment.CurrentDirectory, "accounts.json");
            if (File.Exists(accountsPath))
            {
                var config = JsonSerializer.Deserialize<AccountConfigFile>(File.ReadAllText(accountsPath), CamelCase());
                if (config?.Accounts is { Count: > 0 })
                {
                    for (var i = 0; i < config.Accounts.Count && i < AccountCount; i++)
                    {
                        var name = config.Accounts[i].Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var slot = i + 1;
                        // Only fill if the slot still has a placeholder-like name and file-order account differs.
                        if (!aliases.ContainsKey(slot) || aliases[slot].StartsWith("account", StringComparison.OrdinalIgnoreCase))
                            aliases[slot] = name.Trim();
                    }
                }
            }

            return aliases;
        }
        catch (JsonException)
        {
            return aliases;
        }
    }

    private static string? FindWorkspace()
    {
        string? workspace = null;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json")) &&
                    File.Exists(Path.Combine(dir.FullName, "scripts", "login.js")) &&
                    File.Exists(Path.Combine(dir.FullName, "scripts", "export-token.js")))
                {
                    workspace = dir.FullName;
                }
            }
        }
        return workspace;
    }

    private static DateTimeOffset? LatestActionTime(IEnumerable<string?> timestamps)
    {
        DateTimeOffset? latest = null;
        foreach (var timestamp in timestamps)
        {
            var parsed = ParseActionTime(timestamp);
            if (parsed is not { } value || (latest is { } current && value <= current))
                continue;
            latest = value;
        }
        return latest;
    }

    private static string FormatActionTime(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
            return "—";

        var parsed = ParseActionTime(timestamp);
        return parsed is { } value
            ? FormatActionTime(value)
            : timestamp.Truncate(24);
    }

    private static string FormatActionTime(DateTimeOffset? timestamp) =>
        timestamp is { } value
            ? TimeZoneInfo.ConvertTime(value, GetTaipeiZone()).ToString("MM/dd HH:mm")
            : "—";

    private static DateTimeOffset? ParseActionTime(string? timestamp) =>
        DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static TimeZoneInfo GetTaipeiZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time"); }
        catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"); }
    }

    private static string NodeCommandPath(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;
        if (command == "node")
        {
            var nodeExecutable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs",
                "node.exe");
            return File.Exists(nodeExecutable) ? nodeExecutable : "node";
        }

        var systemCommand = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs",
            $"{command}.cmd");
        return File.Exists(systemCommand) ? systemCommand : $"{command}.cmd";
    }

    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new InvalidOperationException("腳本輸出中找不到 JSON。");
        return output[start..(end + 1)];
    }

    private static JsonSerializerOptions CamelCase() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static JsonSerializerOptions CamelCaseIndented() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class AccountConfigFile
    {
        public string? SiteUrl { get; set; }
        public List<AccountConfigItem>? Accounts { get; set; }
        public object? Checkin { get; set; }
    }

    private sealed class AccountConfigItem
    {
        public string? Name { get; set; }
        public bool Enabled { get; set; } = true;
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
