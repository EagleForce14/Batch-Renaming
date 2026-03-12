using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace BulkRenamer
{
    public sealed partial class HistoryPage : Page
    {
        public ObservableCollection<HistoryEntry> History => HistoryManager.History;
        private readonly ResourceLoader? _resources;

        public HistoryPage()
        {
            this.InitializeComponent();
            try { _resources = new ResourceLoader(); } catch { }
            this.Loaded += HistoryPage_Loaded;
            History.CollectionChanged += History_CollectionChanged;
        }

        private string GetString(string key) => _resources?.GetString(key) ?? key;
        private string FormatString(string key, params object[] args)
        {
            var format = GetString(key);
            try
            {
                return string.Format(format, args);
            }
            catch
            {
                return format;
            }
        }

        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (History.Count == 0)
            {
                await HistoryManager.LoadAsync();
            }
            UpdateEmptyState();
        }

        private void History_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            if (History.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                HistoryList.Visibility = Visibility.Collapsed;
                ClearHistoryButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                HistoryList.Visibility = Visibility.Visible;
                ClearHistoryButton.Visibility = Visibility.Visible;
            }
        }

        private void OnRestoreSettings(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is RenameSettings settings)
            {
                // Navigate to RenamingPage with settings
                Frame.Navigate(typeof(RenamingPage), new NavigationToRenamingArgs
                {
                    Settings = settings,
                    FromHistory = true
                });
            }
        }

        private async void OnUndoBatch(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is HistoryEntry entry && !entry.IsUndone)
            {
                int count = 0;
                int errors = 0;

                foreach (var record in entry.Records.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(record.NewPath))
                        {
                            if (!File.Exists(record.OriginalPath))
                            {
                                File.Move(record.NewPath, record.OriginalPath);
                                count++;
                            }
                            else
                            {
                                errors++; 
                            }
                        }
                        else
                        {
                            errors++;
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }

                if (count > 0)
                {
                    entry.IsUndone = true;
                    await HistoryManager.SaveAsync();
                    ShowInfo(FormatString("UndoSuccessMessage", count), InfoBarSeverity.Success);
                }
                else
                {
                    ShowInfo(GetString("UndoErrorMessage"), InfoBarSeverity.Error);
                }
            }
        }

        private async void OnRedoBatch(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is HistoryEntry entry && entry.IsUndone)
            {
                int count = 0;
                int errors = 0;

                foreach (var record in entry.Records)
                {
                    try
                    {
                        if (File.Exists(record.OriginalPath))
                        {
                            if (!File.Exists(record.NewPath))
                            {
                                File.Move(record.OriginalPath, record.NewPath);
                                count++;
                            }
                            else
                            {
                                errors++; 
                            }
                        }
                        else
                        {
                            errors++;
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                }

                if (count > 0)
                {
                    entry.IsUndone = false;
                    await HistoryManager.SaveAsync();
                    ShowInfo(FormatString("RedoSuccessMessage", count), InfoBarSeverity.Success);
                }
                else
                {
                    ShowInfo(GetString("RedoErrorMessage"), InfoBarSeverity.Error);
                }
            }
        }

        private void ShowInfo(string message, InfoBarSeverity severity)
        {
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private async void OnClearHistory(object sender, RoutedEventArgs e)
        {
            await HistoryManager.ClearAsync();
            UpdateEmptyState();
        }
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
