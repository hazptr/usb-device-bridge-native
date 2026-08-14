using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using UsbDeviceBridge.App.Services;
using Usbdevicebridge.V1;
using Button = System.Windows.Controls.Button;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing settings panel filter/search logic for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private sealed record SettingsSectionInfo(
        string Key,
        string Label,
        FrameworkElement Section,
        IReadOnlyList<FrameworkElement> Rows);

    private readonly record struct ConfiguredClientEntry(
        AttachTargetType Type,
        string Name,
        string Source,
        bool IsUserAdded);

    private readonly record struct CommandRunResult(int ExitCode, string StdOut, string StdErr);

    private IReadOnlyList<FrameworkElement> _searchRows = [];
    private IReadOnlyList<SettingsSectionInfo> _settingsSections = [];
    private HashSet<FrameworkElement> _hiddenSettingRows = [];
    private readonly Dictionary<string, Button> _settingsFilterButtons = [];
    private string? _activeSettingsFilterKey;

    private async void OpenSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.SearchBox.Focus();
        await PopulateSettingsClientsPanelAsync();
        await RefreshVersionInfoAsync();
    }

    private async Task PopulateSettingsClientsPanelAsync()
    {
        var clientListHost = SettingsOverlay.ClientListHost;
        clientListHost.Children.Clear();

        var entries = new List<ConfiguredClientEntry>();

        var detectedWsl = await GetDetectedWslClientsAsync();
        entries.AddRange(detectedWsl.Select(distro => new ConfiguredClientEntry(
            AttachTargetType.Wsl,
            distro,
            "auto-detected",
            IsUserAdded: false)));

        var configSshHosts = GetDetectedSshConfigClients();
        var userAddedSshHosts = (_settings.AdditionalSshClients ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var host in configSshHosts)
        {
            var isUserAdded = userAddedSshHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
            entries.Add(new ConfiguredClientEntry(
                AttachTargetType.Ssh,
                host,
                isUserAdded ? "ssh config + user" : "ssh config",
                IsUserAdded: isUserAdded));
        }

        foreach (var host in userAddedSshHosts)
        {
            if (configSshHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                continue;

            entries.Add(new ConfiguredClientEntry(
                AttachTargetType.Ssh,
                host,
                "user",
                IsUserAdded: true));
        }

        foreach (var entry in entries
                     .OrderBy(e => e.Type == AttachTargetType.Wsl ? 0 : 1)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8),
                MinHeight = 40,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var typeLabel = entry.Type == AttachTargetType.Wsl ? "WSL" : "SSH";
            var text = new TextBlock
            {
                Text = $"{typeLabel} | {entry.Name} ({entry.Source})",
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetColumn(text, 0);

            var setupButton = new Button
            {
                Content = "Setup",
                Tag = entry,
                Style = (Style)FindResource("GhostBtn"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            setupButton.Click += SettingsSetupClient_OnClick;
            Grid.SetColumn(setupButton, 1);

            row.Children.Add(text);
            row.Children.Add(setupButton);

            if (entry.IsUserAdded)
            {
                var removeButton = new Button
                {
                    Content = "Remove",
                    Tag = entry.Name,
                    Style = (Style)FindResource("GhostBtn"),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeButton.Click += SettingsRemoveClient_OnClick;
                Grid.SetColumn(removeButton, 2);
                row.Children.Add(removeButton);
            }

            clientListHost.Children.Add(row);
        }

        if (clientListHost.Children.Count == 0)
        {
            clientListHost.Children.Add(new TextBlock
            {
                Text = "No clients detected yet. Add an SSH client above.",
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private async Task<IReadOnlyList<string>> GetDetectedWslClientsAsync()
    {
        try
        {
            return (await _wslUserSpaceInterop.QueryDistrosAsync())
                .Select(d => d.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private IReadOnlyList<string> GetDetectedSshConfigClients()
    {
        try
        {
            return _sshConfigParser.GetHostAliases()
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async void SettingsAddClient_OnClick(object sender, RoutedEventArgs e)
    {
        var host = (SettingsOverlay.AddClientHostText.Text ?? string.Empty).Trim();
        if (host.Length == 0)
            return;

        if (!SshConfigParser.IsValidAdHocHost(host))
        {
            ShowThemedNoticeDialog(
                "Invalid SSH client",
                "Enter a valid SSH host alias or host string (for example: dev-box or user@dev-box).",
                "OK");
            return;
        }

        var clients = (_settings.AdditionalSshClients ?? [])
            .Append(host)
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _settings.AdditionalSshClients = clients;
        _settingsService.Save(_settings);
        SettingsOverlay.AddClientHostText.Text = string.Empty;

        _vm.RefreshCommand.Execute(null);
        await PopulateSettingsClientsPanelAsync();
    }

    private async void SettingsRemoveClient_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string host)
            return;

        _settings.AdditionalSshClients = (_settings.AdditionalSshClients ?? [])
            .Where(c => !string.Equals(c, host, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _settingsService.Save(_settings);

        _vm.RefreshCommand.Execute(null);
        await PopulateSettingsClientsPanelAsync();
    }

    private async void SettingsSetupClient_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ConfiguredClientEntry entry)
            return;

        button.IsEnabled = false;
        try
        {
            if (entry.Type == AttachTargetType.Wsl)
            {
                await SetupWslClientPackagesAsync(entry.Name);
                return;
            }

            await SetupSshClientPackagesAsync(entry.Name);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task SetupWslClientPackagesAsync(string distro)
    {
        var lines = new List<string>();
        Task Capture(string line, bool isError)
        {
            lines.Add((isError ? "ERR: " : string.Empty) + line);
            return Task.CompletedTask;
        }

        var updateCode = await _wslUserSpaceInterop.RunCommandInDistroStreamingAsync(
            distro,
            "apt-get update",
            Capture,
            CancellationToken.None,
            user: "root");

        var installCode = await _wslUserSpaceInterop.RunCommandInDistroStreamingAsync(
            distro,
            "apt-get install -y usbip usbutils linux-tools-generic hwdata",
            Capture,
            CancellationToken.None,
            user: "root");

        var vhciCode = await _wslUserSpaceInterop.RunCommandInDistroStreamingAsync(
            distro,
            "modprobe vhci_hcd",
            Capture,
            CancellationToken.None,
            user: "root");

        var success = updateCode == 0 && installCode == 0 && vhciCode == 0;
        var tail = string.Join(Environment.NewLine, lines.TakeLast(24));

        ShowThemedNoticeDialog(
            success ? "WSL setup complete" : "WSL setup failed",
            success
                ? $"Configured '{distro}' successfully.{Environment.NewLine}{Environment.NewLine}{tail}"
                : $"Package setup failed for '{distro}'.{Environment.NewLine}{Environment.NewLine}{tail}",
            "OK");
    }

    private async Task SetupSshClientPackagesAsync(string sshHost)
    {
        var commandNoPassword = BuildSshSetupCommand(requirePassword: false);
        var firstTry = await RunSshSetupCommandAsync(sshHost, commandNoPassword, null, CancellationToken.None);

        if (firstTry.ExitCode == 0)
        {
            ShowThemedNoticeDialog(
                "SSH setup complete",
                $"Configured '{sshHost}' successfully.{Environment.NewLine}{Environment.NewLine}{BuildOutputSummary(firstTry)}",
                "OK");
            return;
        }

        var firstOutput = $"{firstTry.StdErr}\n{firstTry.StdOut}";
        if (RequiresSudoPassword(firstOutput))
        {
            var password = PromptForSshSudoPassword(sshHost);
            if (password is null)
                return;

            var commandWithPassword = BuildSshSetupCommand(requirePassword: true);
            var secondTry = await RunSshSetupCommandAsync(sshHost, commandWithPassword, password, CancellationToken.None);
            if (secondTry.ExitCode == 0)
            {
                ShowThemedNoticeDialog(
                    "SSH setup complete",
                    $"Configured '{sshHost}' successfully.{Environment.NewLine}{Environment.NewLine}{BuildOutputSummary(secondTry)}",
                    "OK");
                return;
            }

            ShowThemedNoticeDialog(
                "SSH setup failed",
                $"Package setup failed for '{sshHost}'.{Environment.NewLine}{Environment.NewLine}{BuildOutputSummary(secondTry)}",
                "OK");
            return;
        }

        ShowThemedNoticeDialog(
            "SSH setup failed",
            $"Package setup failed for '{sshHost}'.{Environment.NewLine}{Environment.NewLine}{BuildOutputSummary(firstTry)}",
            "OK");
    }

    private static string BuildSshSetupCommand(bool requirePassword)
    {
        // Resolve usbip binary path and write a sudoers.d rule so the user can run
        // usbip attach without a password (required for non-interactive attach in the app).
        var setupScript =
            // 1. Install packages
            "if command -v apt-get >/dev/null 2>&1; then apt-get update && apt-get install -y usbip usbutils linux-tools-generic hwdata; "
            + "elif command -v dnf >/dev/null 2>&1; then dnf install -y usbip usbutils hwdata; "
            + "elif command -v pacman >/dev/null 2>&1; then pacman -Sy --noconfirm usbip usbutils hwdata; "
            + "else echo 'No supported package manager found (apt, dnf, pacman).' >&2; exit 1; fi; "
            // 2. Load the vhci_hcd kernel module now, and persist it across reboots.
            //    Without the modules-load.d entry the module is gone after a reboot and
            //    every subsequent attach fails with "usbip: error: open vhci_driver".
            + "modprobe vhci_hcd || echo 'Warning: Failed to load vhci_hcd kernel module (may not be available in this environment)'; "
            + "mkdir -p /etc/modules-load.d && echo vhci_hcd > /etc/modules-load.d/vhci-hcd.conf; "
            // 3. Write a sudoers rule so non-interactive sudo works for the three commands
            //    the attach path needs: usbip itself, reloading vhci_hcd if it is missing,
            //    and settling udev between attaches. modprobe/udevadm are argument-scoped.
            //    The udevadm arguments must match RemoteUsbIpCommands exactly, because
            //    sudoers argument matching is exact rather than prefix-based.
            + "USBIP_BIN=$(command -v usbip 2>/dev/null || echo /usr/sbin/usbip); "
            + "MODPROBE_BIN=$(command -v modprobe 2>/dev/null || echo /usr/sbin/modprobe); "
            + "UDEVADM_BIN=$(command -v udevadm 2>/dev/null || echo /usr/bin/udevadm); "
            + "RULE_USER=${SUDO_USER:-$USER}; "
            + "if [ -z \"$RULE_USER\" ]; then echo 'Could not determine which user to grant passwordless sudo to.' >&2; exit 1; fi; "
            // Validate in a temp file and only then install. Writing straight to
            // /etc/sudoers.d left an invalid rule in place when visudo rejected it, and
            // every later sudo on that host then reported a parse error.
            + "TMP_RULE=$(mktemp); "
            + $"echo \"${{RULE_USER}} ALL=(ALL) NOPASSWD: ${{USBIP_BIN}}, ${{MODPROBE_BIN}} vhci_hcd, ${{UDEVADM_BIN}} {RemoteUsbIpCommands.UdevSettleArgs}\" > \"$TMP_RULE\"; "
            + "if visudo -c -f \"$TMP_RULE\" >/dev/null 2>&1; then "
            + "install -m 0440 -o root -g root \"$TMP_RULE\" /etc/sudoers.d/usbip-attach && rm -f \"$TMP_RULE\" && echo 'sudoers rule written for usbip'; "
            + "else "
            + "echo 'Generated sudoers rule was rejected by visudo; nothing was installed:' >&2; cat \"$TMP_RULE\" >&2; rm -f \"$TMP_RULE\"; exit 1; "
            + "fi";

        // Single-quote the script for the remote login shell. With double quotes that shell
        // expanded ${RULE_USER}/${USBIP_BIN} before the inner sh assigned them, so the
        // sudoers line was written with an empty user and empty paths.
        var escaped = setupScript.Replace("'", "'\\''");
        var sudoPrefix = requirePassword ? "sudo -S -p ''" : "sudo -n";
        return $"{sudoPrefix} sh -lc '{escaped}'";
    }

    private static bool RequiresSudoPassword(string output)
        => output.Contains("password is required", StringComparison.OrdinalIgnoreCase)
           || output.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
           || output.Contains("sudo:", StringComparison.OrdinalIgnoreCase)
              && output.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string BuildOutputSummary(CommandRunResult result)
    {
        var all = $"{result.StdErr}\n{result.StdOut}".Trim();
        if (all.Length == 0)
            return result.ExitCode == 0 ? "No output." : "No output was returned.";

        var lines = all.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(24);
        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<CommandRunResult> RunSshSetupCommandAsync(
        string sshHost,
        string remoteCommand,
        string? sudoPassword,
        CancellationToken ct)
    {
        // When a sudo password is provided, pipe it via printf on the REMOTE side
        // rather than writing to SSH's stdin from Windows. Writing to SSH stdin has
        // Windows-side buffering/timing issues that cause sudo to receive EOF before
        // reading the password. A local printf pipe on the remote is always reliable.
        string actualCommand;
        if (!string.IsNullOrEmpty(sudoPassword))
        {
            // Single-quote-escape the password for safe shell embedding.
            var shellEscaped = sudoPassword.Replace("'", "'\\''");
            actualCommand = $"printf '%s\\n' '{shellEscaped}' | {remoteCommand}";
        }
        else
        {
            actualCommand = remoteCommand;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        // See UsbIpdClient.RunSshCommandAsync: a host configured with RemoteCommand cannot
        // also be given a command line, and RequestTTY=yes would break the piped sudo -S
        // password read below by turning stdin into a pty.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RemoteCommand=none");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RequestTTY=no");
        psi.ArgumentList.Add(sshHost);
        psi.ArgumentList.Add(actualCommand);

        using var process = Process.Start(psi);
        if (process is null)
            return new CommandRunResult(-1, string.Empty, "Failed to start ssh process.");

        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new CommandRunResult(
            process.ExitCode,
            (await stdoutTask).Trim(),
            (await stderrTask).Trim());
    }

    private string? PromptForSshSudoPassword(string sshHost)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = $"Sudo Password - {sshHost}",
            Width = 420,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var description = new TextBlock
        {
            Text = "Enter the sudo password for the SSH client to install packages.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(description, 0);

        var passwordBox = new PasswordBox
        {
            Height = 34,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(passwordBox, 1);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("GhostBtn"),
        };
        var ok = new Button
        {
            Content = "Run setup",
            MinWidth = 90,
            Style = (Style)FindResource("AccentBtn"),
            IsDefault = true,
        };

        string? password = null;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        ok.Click += (_, _) =>
        {
            password = passwordBox.Password;
            dialog.DialogResult = true;
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);

        root.Children.Add(description);
        root.Children.Add(passwordBox);
        root.Children.Add(buttons);
        dialog.Content = root;

        var accepted = dialog.ShowDialog() == true;
        return accepted ? password : null;
    }

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void InitializeSettingsMetadata()
    {
        var discovered = new List<SettingsSectionInfo>();
        foreach (var section in SettingsOverlay.SectionsRoot.Children.OfType<FrameworkElement>())
        {
            if (!section.Name.StartsWith("SettingsSection", StringComparison.OrdinalIgnoreCase))
                continue;

            if (section is not Border border || border.Child is not System.Windows.Controls.Panel panel)
                continue;

            var key = section.Name["SettingsSection".Length..];
            var label = panel.Children
                .OfType<TextBlock>()
                .FirstOrDefault()?.Text ?? key;

            var rows = panel.Children
                .OfType<FrameworkElement>()
                .Where(e => e.Name.StartsWith("SettingRow", StringComparison.OrdinalIgnoreCase))
                .ToList();

            discovered.Add(new SettingsSectionInfo(key, label, section, rows));
        }

        _settingsSections = discovered;
        _searchRows = discovered.SelectMany(s => s.Rows).Distinct().ToList();
        _hiddenSettingRows = _searchRows.Where(r => r.Visibility == Visibility.Collapsed).ToHashSet();
    }

    private void BuildSettingsFilterButtons()
    {
        SettingsOverlay.SectionFiltersHost.Children.Clear();
        _settingsFilterButtons.Clear();

        CreateSettingsFilterButton("All", null);
        foreach (var section in _settingsSections)
            CreateSettingsFilterButton(section.Label, section.Key);

        UpdateSettingsFilterButtonVisuals();
    }

    private void CreateSettingsFilterButton(string label, string? key)
    {
        var button = new Button
        {
            Content = label,
            Tag = key,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("SettingsFilterButton"),
        };
        button.Click += SettingsSectionFilter_OnClick;
        SettingsOverlay.SectionFiltersHost.Children.Add(button);
        _settingsFilterButtons[key ?? string.Empty] = button;
    }

    private void SettingsSectionFilter_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        _activeSettingsFilterKey = button.Tag as string;
        UpdateSettingsFilterButtonVisuals();
        ApplySettingsSearch();
    }

    private void UpdateSettingsFilterButtonVisuals()
    {
        var selectedKey = _activeSettingsFilterKey ?? string.Empty;

        foreach (var (key, button) in _settingsFilterButtons)
        {
            var selected = string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase);
            button.Background = selected
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("TextPrimary");
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }
    }

    private void SettingsSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Search intentionally disables section filter mode.
        if (!string.IsNullOrWhiteSpace(SettingsOverlay.SearchBox.Text) && _activeSettingsFilterKey is not null)
        {
            _activeSettingsFilterKey = null;
            UpdateSettingsFilterButtonVisuals();
        }

        ApplySettingsSearch();
    }

    private void ApplySettingsSearch()
    {
        var query = (SettingsOverlay.SearchBox.Text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            foreach (var row in _searchRows)
                row.Visibility = _hiddenSettingRows.Contains(row) ? Visibility.Collapsed : Visibility.Visible;

            foreach (var section in _settingsSections)
            {
                var visibleByFilter = _activeSettingsFilterKey is null
                    || string.Equals(section.Key, _activeSettingsFilterKey, StringComparison.OrdinalIgnoreCase);
                section.Section.Visibility = visibleByFilter ? Visibility.Visible : Visibility.Collapsed;
            }

            return;
        }

        foreach (var row in _searchRows)
        {
            if (_hiddenSettingRows.Contains(row)) { row.Visibility = Visibility.Collapsed; continue; }
            var tag = row.Tag?.ToString() ?? string.Empty;
            row.Visibility = tag.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var section in _settingsSections)
        {
            var hasVisibleRows = section.Rows.Count == 0 || section.Rows.Any(r => r.Visibility == Visibility.Visible);
            section.Section.Visibility = hasVisibleRows ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
