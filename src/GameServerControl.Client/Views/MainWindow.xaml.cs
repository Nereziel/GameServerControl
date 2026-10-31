using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Client.ViewModels;

namespace GameServerControl.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Loaded += (_, _) => WireLocalUpdaterButton();
    }

    private void WireLocalUpdaterButton()
    {
        // The update banner is driven by the Agent's release checker. Re-purpose its
        // release-notes button only for a local-agent installation; on remote/federated
        // setups the existing command remains a harmless link to GitHub Releases.
        var settings = AppSettings.Load();
        var first = settings.Agents.FirstOrDefault();
        if (first is null || !Uri.TryCreate(first.Url, UriKind.Absolute, out var uri)) return;
        if (!(uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))) return;

        foreach (var btn in FindVisualChildren<Button>(this))
        {
            if (!string.Equals(btn.Content?.ToString(), "View release notes", StringComparison.Ordinal)) continue;
            btn.Command = null;
            btn.Content = "Update Agent + Client";
            btn.ToolTip = "Install the latest GitHub release. The client closes while files are replaced.";
            btn.Click += UpdateNow_Click;
            break;
        }
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Update GameServerControl Agent and Client to the latest published release?\n\n" +
            "The client will close, the Agent service will restart, and your config, API token and registered servers will be preserved.",
            "Update GameServerControl",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        const string installerUrl = "https://raw.githubusercontent.com/Nereziel/GameServerControl/main/deploy/windows/install.ps1";
        var command = $"& {{ $ProgressPreference='SilentlyContinue'; Invoke-WebRequest -Uri '{installerUrl}' -UseBasicParsing | Invoke-Expression }}";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled the UAC prompt.
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start updater:\n\n" + ex.Message,
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private async void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ServerViewModel vm)
        {
            var desired = cb.IsChecked == true;
            cb.IsChecked = vm.IsOn;
            await vm.ToggleAsync(desired);
        }
    }

    private async void AutostartClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ServerViewModel vm)
        {
            var desired = cb.IsChecked == true;
            cb.IsChecked = vm.IsAutostartEnabled;
            await vm.SetAutostartAsync(desired);
        }
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string code && !string.IsNullOrEmpty(code))
        {
            try
            {
                Clipboard.SetText(code);
                btn.Content = "✓";
                _ = System.Threading.Tasks.Task.Delay(1200).ContinueWith(_ =>
                    Dispatcher.Invoke(() => btn.Content = "📋"));
            }
            catch { /* clipboard can be flaky */ }
        }
    }
}
