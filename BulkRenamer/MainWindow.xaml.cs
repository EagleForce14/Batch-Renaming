using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinRT.Interop;

namespace BulkRenamer
{
    public sealed partial class MainWindow : Window
    {
        private bool _backToHistoryActive;
        private bool _syncingNavSelection;

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
            ContentFrame.Navigated += ContentFrame_Navigated;
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
            if (_syncingNavSelection)
            {
                return;
            }

            if (args.IsSettingsSelected)
            {
                ContentFrame.Navigate(typeof(SettingsPage), null, args.RecommendedNavigationTransitionInfo);
            }
            else if (args.SelectedItemContainer != null)
            {
                 if (args.SelectedItemContainer.Tag?.ToString() == "Home")
                 {
                     _backToHistoryActive = false;
                     UpdateBackButtonVisibility();
                     ContentFrame.Navigate(typeof(RenamingPage), null, args.RecommendedNavigationTransitionInfo);
                 }
                 else if (args.SelectedItemContainer.Tag?.ToString() == "History")
                 {
                     _backToHistoryActive = false;
                     UpdateBackButtonVisibility();
                     ContentFrame.Navigate(typeof(HistoryPage), null, args.RecommendedNavigationTransitionInfo);
                 }
            }
        }

        private async void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            if (!_backToHistoryActive)
            {
                return;
            }

            if (ContentFrame.Content is RenamingPage page)
            {
                var allow = await page.ConfirmLeaveIfPendingAsync();
                if (!allow)
                {
                    return;
                }
            }

            if (ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack();
            }
            else
            {
                ContentFrame.Navigate(typeof(HistoryPage));
            }

            _backToHistoryActive = false;
            UpdateBackButtonVisibility();
        }

        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.SourcePageType == typeof(RenamingPage) && e.Parameter is NavigationToRenamingArgs navArgs && navArgs.FromHistory)
            {
                _backToHistoryActive = true;
            }
            else
            {
                _backToHistoryActive = false;
            }

            UpdateBackButtonVisibility();
            SyncNavSelectionWithPage(e.SourcePageType);
        }

        private void SyncNavSelectionWithPage(Type sourcePageType)
        {
            try
            {
                _syncingNavSelection = true;

                if (sourcePageType == typeof(SettingsPage))
                {
                    NavView.SelectedItem = NavView.SettingsItem;
                    return;
                }

                var targetTag = sourcePageType == typeof(HistoryPage) ? "History" : "Home";
                var targetItem = NavView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), targetTag, StringComparison.Ordinal));

                if (targetItem != null)
                {
                    NavView.SelectedItem = targetItem;
                }
            }
            finally
            {
                _syncingNavSelection = false;
            }
        }

        private void UpdateBackButtonVisibility()
        {
            NavView.IsBackEnabled = _backToHistoryActive;
            NavView.IsBackButtonVisible = _backToHistoryActive ? NavigationViewBackButtonVisible.Visible : NavigationViewBackButtonVisible.Collapsed;
        }
    }
}
