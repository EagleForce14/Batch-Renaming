using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using AppLifecycle = Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace BulkRenamer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public Window? MainWindow => _window;

        /// <summary>

        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            AppLifecycle.AppInstance.GetCurrent().Activated += OnAppActivated;
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var activation = AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation?.Data is IActivatedEventArgs activatedArgs)
            {
                HandleActivation(activatedArgs);
            }

            EnsureWindow().Activate();
        }

        private void OnAppActivated(object? sender, AppLifecycle.AppActivationArguments args)
        {
            if (args.Data is IActivatedEventArgs activatedArgs)
            {
                HandleActivation(activatedArgs, activateWindow: true);
            }
        }

        private void HandleActivation(IActivatedEventArgs args, bool activateWindow = false)
        {
            if (args is FileActivatedEventArgs fileArgs)
            {
                var window = EnsureWindow();
                var files = fileArgs.Files.OfType<StorageFile>().Select(f => f.Path);
                window.ImportFiles(files);

                if (activateWindow)
                {
                    window.Activate();
                }
            }
        }

        private MainWindow EnsureWindow()
        {
            if (_window is MainWindow mainWindow)
            {
                return mainWindow;
            }

            mainWindow = new MainWindow();
            _window = mainWindow;

            if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, true);
            }

            if (_window.Content is FrameworkElement root)
            {
                ApplyTitleBarTheme(root.ActualTheme);
                root.ActualThemeChanged += OnRootThemeChanged;
            }

            return mainWindow;
        }

        private void OnRootThemeChanged(FrameworkElement sender, object args)
        {
            ApplyTitleBarTheme(sender.ActualTheme);
        }

        private void ApplyTitleBarTheme(ElementTheme theme)
        {
            if (_window?.AppWindow?.TitleBar is not AppWindowTitleBar titleBar)
            {
                return;
            }

            var isDark = theme == ElementTheme.Dark;
            var foreground = isDark ? Colors.White : Colors.Black;
            var hoverBackground = isDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            var pressedBackground = isDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00);

            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;

            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = foreground;
            titleBar.ForegroundColor = foreground;
        }
    }
}
