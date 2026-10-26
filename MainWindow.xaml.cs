using System.Collections.ObjectModel;
using System.IO;
using FsoTuner.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;

namespace FsoTuner;

public sealed partial class MainWindow : Window
{
    private bool _loading;

    public ObservableCollection<GameOverride> Games { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetInitialSizeAndCenter();

        RootGrid.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        UpdateTitleBarColors();
        RefreshAll();
    }

    private void RefreshAll()
    {
        var snapshot = FsoService.Read();

        _loading = true;

        StatusText.Text = snapshot.StatusTitle;
        StatusDetail.Text = snapshot.StatusDetail;
        StatusDot.Fill = new SolidColorBrush(snapshot.Kind switch
        {
            FsoStateKind.Default or FsoStateKind.OnAll => Color.FromArgb(255, 0x6C, 0xCB, 0x5F),
            FsoStateKind.OffStrong or FsoStateKind.OffPartial => Color.FromArgb(255, 0x60, 0xCD, 0xFF),
            _ => Color.FromArgb(255, 0xFF, 0xB9, 0x00)
        });

        RegistryValuesText.Text = snapshot.RegistryText;

        FseBehaviorBox.Value = snapshot.FseBehavior ?? 0;
        FseBehaviorModeBox.Value = snapshot.FseBehaviorMode ?? 0;
        HonorUserBox.Value = snapshot.HonorUser ?? 0;
        DxgiHonorBox.Value = snapshot.DxgiHonor ?? 0;
        EfseFlagsBox.Value = snapshot.EfseFlags ?? 0;

        DefaultRadio.IsChecked = snapshot.Kind == FsoStateKind.Default;
        OnRadio.IsChecked = snapshot.Kind == FsoStateKind.OnAll;
        OffRadio.IsChecked = snapshot.Kind is FsoStateKind.OffStrong or FsoStateKind.OffPartial;
        CustomRadio.IsChecked = snapshot.Kind == FsoStateKind.Custom;
        AdvancedPanel.Visibility = snapshot.Kind == FsoStateKind.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;

        _loading = false;

        LoadGames();

        PerGameHint.IsOpen = snapshot.Kind == FsoStateKind.OffStrong && Games.Count > 0;
    }

    private void LoadGames()
    {
        Games.Clear();

        foreach (var game in FsoService.ReadGameOverrides())
        {
            Games.Add(game);
        }

        var hasGames = Games.Count > 0;
        EmptyGamesPanel.Visibility = hasGames ? Visibility.Collapsed : Visibility.Visible;
        GamesItems.Visibility = hasGames ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        StatusInfoBar.IsOpen = false;
        AdvancedPanel.Visibility = CustomRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        StatusInfoBar.IsOpen = false;
        RefreshAll();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DefaultRadio.IsChecked == true)
            {
                FsoService.WriteDefault();
                ShowInfo(
                    "Windows default restored",
                    "The global FSO overrides were removed. Restart the game to pick up the change.",
                    InfoBarSeverity.Success);
            }
            else if (OnRadio.IsChecked == true)
            {
                FsoService.WriteOnAll();
                ShowInfo(
                    "FSO-on preference applied",
                    "The all-games FSO profile was written. Restart the game; some games may still choose exclusive or borderless mode.",
                    InfoBarSeverity.Success);
            }
            else if (OffRadio.IsChecked == true)
            {
                FsoService.WriteOff();
                ShowInfo(
                    "FSO disabled",
                    "Strong global preset applied. Restart the game; sign out and back in if it doesn't take effect.",
                    InfoBarSeverity.Success);
            }
            else if (CustomRadio.IsChecked == true)
            {
                FsoService.WriteCustom(
                    ToInt(FseBehaviorBox.Value),
                    ToInt(FseBehaviorModeBox.Value),
                    ToInt(HonorUserBox.Value),
                    ToInt(DxgiHonorBox.Value),
                    ToInt(EfseFlagsBox.Value));

                ShowInfo(
                    "Custom values applied",
                    "Restart the game; sign out and back in if it doesn't take effect.",
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo("Pick a mode", "Choose Windows default, FSO off, or Custom before applying.", InfoBarSeverity.Informational);
                return;
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            ShowInfo("Couldn't apply changes", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Restore Windows default?",
                Content = "This removes the five FSO overrides from HKCU. Games will use Windows' normal fullscreen behavior.",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            FsoService.WriteDefault();
            ShowInfo(
                "Windows default restored",
                "The global FSO overrides were removed. Restart the game to pick up the change.",
                InfoBarSeverity.Success);
            RefreshAll();
        }
        catch (Exception ex)
        {
            ShowInfo("Couldn't restore defaults", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void AddGameButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.ViewMode = PickerViewMode.List;

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            if (Games.Any(game => string.Equals(game.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            {
                ShowInfo(
                    "Already added",
                    "That executable already has a per-game FSO override.",
                    InfoBarSeverity.Informational);
                return;
            }

            FsoService.AddGameOverride(file.Path);
            RefreshAll();
            ShowInfo(
                "Per-game override added",
                $"{file.Name} will launch with FSO disabled.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("Couldn't add the game", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        try
        {
            FsoService.RemoveGameOverride(path);
            RefreshAll();
            ShowInfo(
                "Override removed",
                $"{Path.GetFileName(path)} now follows the global FSO setting.",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo("Couldn't remove the override", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void SetInitialSizeAndCenter()
    {
        const int preferredWidth = 1400;
        const int preferredHeight = 900;
        const int margin = 40;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
        {
            AppWindow.Resize(new SizeInt32(preferredWidth, preferredHeight));
            return;
        }

        var work = area.WorkArea;
        var width = Math.Min(preferredWidth, Math.Max(800, work.Width - margin * 2));
        var height = Math.Min(preferredHeight, Math.Max(600, work.Height - margin * 2));

        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2));
    }

    private void UpdateTitleBarColors()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar is null)
        {
            return;
        }

        var dark = RootGrid.ActualTheme == ElementTheme.Dark;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(0x80, 255, 255, 255)
            : Color.FromArgb(0x80, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x18, 255, 255, 255)
            : Color.FromArgb(0x10, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x28, 255, 255, 255)
            : Color.FromArgb(0x18, 0, 0, 0);
        titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    private static int ToInt(double value)
    {
        return double.IsNaN(value) ? 0 : (int)Math.Round(value);
    }
}
