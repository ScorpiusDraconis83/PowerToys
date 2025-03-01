// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Common.UI;
using interop;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Helpers;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Telemetry.Events;
using Microsoft.PowerToys.Settings.UI.Views;
using Microsoft.PowerToys.Telemetry;
using Microsoft.UI.Xaml;
using Windows.UI.Popups;
using WinRT.Interop;
using WinUIEx;

namespace Microsoft.PowerToys.Settings.UI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private enum Arguments
        {
            PTPipeName = 1,
            SettingsPipeName,
            PTPid,
            Theme, // used in the old settings
            ElevatedStatus,
            IsUserAdmin,
            ShowOobeWindow,
            ShowScoobeWindow,
            ShowFlyout,
            ContainsSettingsWindow,
            ContainsFlyoutPosition,
        }

        // Quantity of arguments
        private const int RequiredArgumentsQty = 12;

        // Create an instance of the  IPC wrapper.
        private static TwoWayPipeMessageIPCManaged ipcmanager;

        public static bool IsElevated { get; set; }

        public static bool IsUserAnAdmin { get; set; }

        public static int PowerToysPID { get; set; }

        public bool ShowOobe { get; set; }

        public bool ShowFlyout { get; set; }

        public bool ShowScoobe { get; set; }

        public Type StartupPage { get; set; } = typeof(Views.DashboardPage);

        public static Action<string> IPCMessageReceivedCallback { get; set; }

        private static bool loggedImmersiveDarkException;

        /// <summary>
        /// Initializes a new instance of the <see cref="App"/> class.
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            Logger.InitializeLogger("\\Settings\\Logs");

            this.InitializeComponent();
        }

        public static void OpenSettingsWindow(Type type = null, bool ensurePageIsSelected = false)
        {
            if (settingsWindow == null)
            {
                settingsWindow = new MainWindow(IsDarkTheme());
            }

            settingsWindow.Activate();

            if (type != null)
            {
                settingsWindow.NavigateToSection(type);
            }

            if (ensurePageIsSelected)
            {
                settingsWindow.EnsurePageIsSelected();
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var cmdArgs = Environment.GetCommandLineArgs();

            if (cmdArgs != null && cmdArgs.Length >= RequiredArgumentsQty)
            {
                // Skip the first argument which is prepended when launched by explorer
                if (cmdArgs[0].EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase) && cmdArgs[1].EndsWith(".exe", StringComparison.InvariantCultureIgnoreCase) && (cmdArgs.Length >= RequiredArgumentsQty + 1))
                {
                    cmdArgs = cmdArgs.Skip(1).ToArray();
                }

                _ = int.TryParse(cmdArgs[(int)Arguments.PTPid], out int powerToysPID);
                PowerToysPID = powerToysPID;

                IsElevated = cmdArgs[(int)Arguments.ElevatedStatus] == "true";
                IsUserAnAdmin = cmdArgs[(int)Arguments.IsUserAdmin] == "true";
                ShowOobe = cmdArgs[(int)Arguments.ShowOobeWindow] == "true";
                ShowScoobe = cmdArgs[(int)Arguments.ShowScoobeWindow] == "true";
                ShowFlyout = cmdArgs[(int)Arguments.ShowFlyout] == "true";
                bool containsSettingsWindow = cmdArgs[(int)Arguments.ContainsSettingsWindow] == "true";
                bool containsFlyoutPosition = cmdArgs[(int)Arguments.ContainsFlyoutPosition] == "true";

                // To keep track of variable arguments
                int currentArgumentIndex = RequiredArgumentsQty;

                if (containsSettingsWindow)
                {
                    // Open specific window
                    StartupPage = GetPage(cmdArgs[currentArgumentIndex]);

                    currentArgumentIndex++;
                }

                int flyout_x = 0;
                int flyout_y = 0;
                if (containsFlyoutPosition)
                {
                    // get the flyout position arguments
                    _ = int.TryParse(cmdArgs[currentArgumentIndex++], out flyout_x);
                    _ = int.TryParse(cmdArgs[currentArgumentIndex++], out flyout_y);
                }

                RunnerHelper.WaitForPowerToysRunner(PowerToysPID, () =>
                {
                    Environment.Exit(0);
                });

                ipcmanager = new TwoWayPipeMessageIPCManaged(cmdArgs[(int)Arguments.SettingsPipeName], cmdArgs[(int)Arguments.PTPipeName], (string message) =>
                {
                    if (IPCMessageReceivedCallback != null && message.Length > 0)
                    {
                        IPCMessageReceivedCallback(message);
                    }
                });
                ipcmanager.Start();

                if (!ShowOobe && !ShowScoobe && !ShowFlyout)
                {
                    settingsWindow = new MainWindow();
                    settingsWindow.Activate();
                    settingsWindow.ExtendsContentIntoTitleBar = true;
                    settingsWindow.NavigateToSection(StartupPage);

                    // https://github.com/microsoft/microsoft-ui-xaml/issues/7595 - Activate doesn't bring window to the foreground
                    // Need to call SetForegroundWindow to actually gain focus.
                    WindowHelpers.BringToForeground(settingsWindow.GetWindowHandle());
                }
                else
                {
                    // Create the Settings window hidden so that it's fully initialized and
                    // it will be ready to receive the notification if the user opens
                    // the Settings from the tray icon.
                    settingsWindow = new MainWindow(true);

                    if (ShowOobe)
                    {
                        PowerToysTelemetry.Log.WriteEvent(new OobeStartedEvent());
                        OobeWindow oobeWindow = new OobeWindow(OOBE.Enums.PowerToysModules.Overview);
                        oobeWindow.Activate();
                        oobeWindow.ExtendsContentIntoTitleBar = true;
                        SetOobeWindow(oobeWindow);
                    }
                    else if (ShowScoobe)
                    {
                        PowerToysTelemetry.Log.WriteEvent(new ScoobeStartedEvent());
                        OobeWindow scoobeWindow = new OobeWindow(OOBE.Enums.PowerToysModules.WhatsNew);
                        scoobeWindow.Activate();
                        scoobeWindow.ExtendsContentIntoTitleBar = true;
                        SetOobeWindow(scoobeWindow);
                    }
                    else if (ShowFlyout)
                    {
                        POINT? p = null;
                        if (containsFlyoutPosition)
                        {
                            p = new POINT(flyout_x, flyout_y);
                        }

                        ShellPage.OpenFlyoutCallback(p);
                    }
                }

                if (SelectedTheme() == ElementTheme.Default)
                {
                    try
                    {
                        themeListener = new ThemeListener();
                        themeListener.ThemeChanged += (_) => HandleThemeChange();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"HandleThemeChange exception. Please install .NET 4.", ex);
                    }
                }
            }
            else
            {
#if DEBUG
                // For debugging purposes
                // Window is also needed to show MessageDialog
                settingsWindow = new MainWindow();
                settingsWindow.ExtendsContentIntoTitleBar = true;
                settingsWindow.Activate();
                settingsWindow.NavigateToSection(StartupPage);
                ShowMessageDialog("The application is running in Debug mode.", "DEBUG");
#else
                /* If we try to run Settings as a standalone app, it will start PowerToys.exe if not running and open Settings again through it in the Dashboard page. */
                SettingsDeepLink.OpenSettings(SettingsDeepLink.SettingsWindow.Dashboard, true);
                Exit();
#endif
            }
        }

#if !DEBUG
        private async void ShowMessageDialogAndExit(string content, string title = null)
#else
        private async void ShowMessageDialog(string content, string title = null)
#endif
        {
            await ShowDialogAsync(content, title);
#if !DEBUG
            this.Exit();
#endif
        }

        public static Task<IUICommand> ShowDialogAsync(string content, string title = null)
        {
            var dialog = new MessageDialog(content, title ?? string.Empty);
            var handle = NativeMethods.GetActiveWindow();
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException();
            }

            InitializeWithWindow.Initialize(dialog, handle);
            return dialog.ShowAsync().AsTask<IUICommand>();
        }

        public static TwoWayPipeMessageIPCManaged GetTwoWayIPCManager()
        {
            return ipcmanager;
        }

        public static ElementTheme SelectedTheme()
        {
            switch (SettingsRepository<GeneralSettings>.GetInstance(settingsUtils).SettingsConfig.Theme.ToUpper(CultureInfo.InvariantCulture))
            {
                case "DARK": return ElementTheme.Dark;
                case "LIGHT": return ElementTheme.Light;
                default: return ElementTheme.Default;
            }
        }

        public static bool IsDarkTheme()
        {
            var selectedTheme = SelectedTheme();
            return selectedTheme == ElementTheme.Dark || (selectedTheme == ElementTheme.Default && ThemeHelpers.GetAppTheme() == AppTheme.Dark);
        }

        public static void HandleThemeChange()
        {
            try
            {
                bool isDark = IsDarkTheme();

                if (settingsWindow != null)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(settingsWindow);
                    ThemeHelpers.SetImmersiveDarkMode(hWnd, isDark);
                }

                if (oobeWindow != null)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(oobeWindow);
                    ThemeHelpers.SetImmersiveDarkMode(hWnd, isDark);
                }

                if (SelectedTheme() == ElementTheme.Default)
                {
                    themeListener = new ThemeListener();
                    themeListener.ThemeChanged += (_) => HandleThemeChange();
                }
                else if (themeListener != null)
                {
                    themeListener.Dispose();
                    themeListener = null;
                }
            }
            catch (Exception e)
            {
                if (!loggedImmersiveDarkException)
                {
                    Logger.LogError($"HandleThemeChange exception. Please install .NET 4.", e);
                    loggedImmersiveDarkException = true;
                }
            }
        }

        public static int UpdateUIThemeMethod(string themeName)
        {
            switch (themeName?.ToUpperInvariant())
            {
                case "LIGHT":
                    // OobeShellPage.OobeShellHandler.RequestedTheme = ElementTheme.Light;
                    ShellPage.ShellHandler.RequestedTheme = ElementTheme.Light;
                    break;
                case "DARK":
                    // OobeShellPage.OobeShellHandler.RequestedTheme = ElementTheme.Dark;
                    ShellPage.ShellHandler.RequestedTheme = ElementTheme.Dark;
                    break;
                case "SYSTEM":
                    // OobeShellPage.OobeShellHandler.RequestedTheme = ElementTheme.Default;
                    ShellPage.ShellHandler.RequestedTheme = ElementTheme.Default;
                    break;
                default:
                    Logger.LogError($"Unexpected theme name: {themeName}");
                    break;
            }

            HandleThemeChange();
            return 0;
        }

        private static ISettingsUtils settingsUtils = new SettingsUtils();

        private static MainWindow settingsWindow;
        private static OobeWindow oobeWindow;
        private static FlyoutWindow flyoutWindow;
        private static ThemeListener themeListener;

        public static void ClearSettingsWindow()
        {
            settingsWindow = null;
        }

        public static MainWindow GetSettingsWindow()
        {
            return settingsWindow;
        }

        public static OobeWindow GetOobeWindow()
        {
            return oobeWindow;
        }

        public static FlyoutWindow GetFlyoutWindow()
        {
            return flyoutWindow;
        }

        public static void SetOobeWindow(OobeWindow window)
        {
            oobeWindow = window;
        }

        public static void SetFlyoutWindow(FlyoutWindow window)
        {
            flyoutWindow = window;
        }

        public static void ClearOobeWindow()
        {
            oobeWindow = null;
        }

        public static void ClearFlyoutWindow()
        {
            flyoutWindow = null;
        }

        public static Type GetPage(string settingWindow)
        {
            switch (settingWindow)
            {
                case "Dashboard": return typeof(DashboardPage);
                case "Overview": return typeof(GeneralPage);
                case "AlwaysOnTop": return typeof(AlwaysOnTopPage);
                case "Awake": return typeof(AwakePage);
                case "CmdNotFound": return typeof(CmdNotFoundPage);
                case "ColorPicker": return typeof(ColorPickerPage);
                case "FancyZones": return typeof(FancyZonesPage);
                case "FileLocksmith": return typeof(FileLocksmithPage);
                case "Run": return typeof(PowerLauncherPage);
                case "ImageResizer": return typeof(ImageResizerPage);
                case "KBM": return typeof(KeyboardManagerPage);
                case "MouseUtils": return typeof(MouseUtilsPage);
                case "MouseWithoutBorders": return typeof(MouseWithoutBordersPage);
                case "PowerRename": return typeof(PowerRenamePage);
                case "QuickAccent": return typeof(PowerAccentPage);
                case "FileExplorer": return typeof(PowerPreviewPage);
                case "ShortcutGuide": return typeof(ShortcutGuidePage);
                case "PowerOCR": return typeof(PowerOcrPage);
                case "VideoConference": return typeof(VideoConferencePage);
                case "MeasureTool": return typeof(MeasureToolPage);
                case "Hosts": return typeof(HostsPage);
                case "RegistryPreview": return typeof(RegistryPreviewPage);
                case "PastePlain": return typeof(PastePlainPage);
                case "Peek": return typeof(PeekPage);
                case "CropAndLock": return typeof(CropAndLockPage);
                case "EnvironmentVariables": return typeof(EnvironmentVariablesPage);
                default:
                    // Fallback to Dashboard
                    Debug.Assert(false, "Unexpected SettingsWindow argument value");
                    return typeof(DashboardPage);
            }
        }
    }
}
