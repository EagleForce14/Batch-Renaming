using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using WinRT.Interop;

namespace BulkRenamer
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico"));
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBarHost);
            
            // Navigate to home logic is handled by setting IsSelected=True in XAML which triggers SelectionChanged
            // But usually we need to do it manually on first load to ensure Frame content is set.
            ContentFrame.Navigate(typeof(RenamingPage));
        }

        public void ImportFiles(System.Collections.Generic.IEnumerable<string> files)
        {
            if (ContentFrame.Content is RenamingPage page)
            {
                page.ImportFiles(files);
            }
            else
            {
                ContentFrame.Navigate(typeof(RenamingPage), files);
            }
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage), null, args.RecommendedNavigationTransitionInfo);
            }
            else if (args.SelectedItemContainer != null)
            {
                 if (args.SelectedItemContainer.Tag?.ToString() == "Home")
                 {
                     ContentFrame.Navigate(typeof(RenamingPage), null, args.RecommendedNavigationTransitionInfo);
                 }
            }
        }
    }
}
