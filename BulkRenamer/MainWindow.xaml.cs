using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace BulkRenamer
{
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<FileEntry> Files { get; } = new();
        public ObservableCollection<FileEntry> FilteredFiles { get; } = new();
        private FileFilter _currentFilter = FileFilter.All;
        private bool _isInitialized;
        private bool _canApply;
        private bool _suppressUpdates;
        private bool _isUpdatingFilter;
        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private readonly ResourceLoader? _resources;
        private Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

        public MainWindow()
        {
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            InitializeComponent();
            try
            {
                _resources = new ResourceLoader();
            }
            catch
            {
                // ResourceLoader may fail in Release builds with trimming enabled
                // The app will continue using resource keys as fallback
                _resources = null;
            }
            FilesList.ItemsSource = FilteredFiles;
            _isInitialized = true;
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "favicon.ico"));
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBarHost);
            Files.CollectionChanged += OnFilesCollectionChanged;
            UpdateEmptyState();
            UpdateCounts();
            UpdateApplyState();
            // Initialiser FilteredFiles avec tous les fichiers (il n'y en a pas encore au démarrage)
            UpdateFilteredFiles();
        }

        private async void OnAddFiles(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            AddFilePaths(files.Select(f => f.Path));
        }

        private async void OnAddFolder(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            bool includeSubfolders = false;

            var subfolders = await folder.GetFoldersAsync();
            if (subfolders.Any())
            {
                includeSubfolders = await ConfirmIncludeSubfolders(folder.Name);
                if (!includeSubfolders)
                {
                    subfolders = new List<StorageFolder>();
                }
            }

            var paths = new List<string>();

            var files = await folder.GetFilesAsync();
            paths.AddRange(files.Select(f => f.Path));

            if (includeSubfolders)
            {
                foreach (var sub in subfolders)
                {
                    var subFiles = await sub.GetFilesAsync();
                    paths.AddRange(subFiles.Select(f => f.Path));
                }
            }

            AddFilePaths(paths, folder.Path, includeSubfolders);
        }

        private void OnFilesDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }

            e.Handled = true;
        }

        private async void OnFilesDrop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var fileItems = items.OfType<StorageFile>();
            var folderItems = DeduplicateNestedFolders(items.OfType<StorageFolder>());

            // Add dropped files directly
            var directFiles = fileItems.Select(f => f.Path).ToList();
            if (directFiles.Count > 0)
            {
                AddFilePaths(directFiles);
            }

            // Handle dropped folders with optional subfolder inclusion prompt per folder
            foreach (var folder in folderItems)
            {
                bool includeSubfolders = false;
                var subfolders = await folder.GetFoldersAsync();
                if (subfolders.Any())
                {
                    includeSubfolders = await ConfirmIncludeSubfolders(folder.Name);
                }

                var paths = new List<string>();
                var rootFiles = await folder.GetFilesAsync();
                paths.AddRange(rootFiles.Select(f => f.Path));

                if (includeSubfolders)
                {
                    foreach (var sub in subfolders)
                    {
                        var subFiles = await sub.GetFilesAsync();
                        paths.AddRange(subFiles.Select(f => f.Path));
                    }
                }

                if (paths.Count > 0)
                {
                    AddFilePaths(paths, folder.Path, includeSubfolders);
                }
            }

            e.Handled = true;
        }

        private static IEnumerable<StorageFolder> DeduplicateNestedFolders(IEnumerable<StorageFolder> folders)
        {
            var list = folders.OrderBy(f => f.Path.Length).ToList();
            var result = new List<StorageFolder>();

            foreach (var folder in list)
            {
                var isNested = result.Any(parent => IsPathUnder(folder.Path, parent.Path));
                if (!isNested)
                {
                    result.Add(folder);
                }
            }

            return result;
        }

        private static bool IsPathUnder(string path, string parent)
        {
            var parentWithSep = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(parentWithSep, StringComparison.OrdinalIgnoreCase);
        }

        private void OnRemoveSelected(object sender, RoutedEventArgs e)
        {
            var selected = FilesList.SelectedItems.OfType<FileEntry>().ToList();
            foreach (var item in selected)
            {
                Files.Remove(item);
            }

            UpdatePreview();
            UpdateEmptyState();
            UpdateCounts();
        }

        private async void OnClearAll(object sender, RoutedEventArgs e)
        {
            if (Files.Count > 0)
            {
                var dialog = new ContentDialog
                {
                    Title = GetString("ClearDialogTitle"),
                    Content = GetString("ClearDialogContent"),
                    PrimaryButtonText = GetString("ClearDialogPrimary"),
                    CloseButtonText = GetString("ClearDialogClose"),
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await ShowDialogAsync(dialog);
                if (result != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            Files.Clear();
            UpdateEmptyState();
            StatusBar.IsOpen = false;
            UpdateCounts();
        }

        private void OnRuleChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }
            UpdatePreview();
            UpdateCounts();
            UpdateApplyState();
        }

        private void OnNumberRuleChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }
            UpdatePreview();
            UpdateCounts();
            UpdateApplyState();
        }

        private void OnCaseChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }
            UpdatePreview();
            UpdateCounts();
            UpdateApplyState();
        }

        private void OnInsertRuleChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }
            UpdatePreview();
            UpdateCounts();
            UpdateApplyState();
        }

        private void OnRegexOptionChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }

            UpdatePreview();
        }

        private void OnFileEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _suppressUpdates)
            {
                return;
            }

            UpdatePreview();
        }

        private void OnPreviewClick(object sender, RoutedEventArgs e)
        {
            UpdatePreview();
        }

        private void OnUndo(object sender, RoutedEventArgs e)
        {
            if (_suppressUpdates)
            {
                return;
            }

            _suppressUpdates = true;

            PrefixBox.Text = string.Empty;
            SuffixBox.Text = string.Empty;
            SearchBox.Text = string.Empty;
            ReplaceBox.Text = string.Empty;
            InsertTextBox.Text = string.Empty;
            InsertPositionBox.Value = 0;
            RegexToggle.IsChecked = false;
            RegexAllToggle.IsChecked = false;

            CaseModeBox.SelectedIndex = 0;
            ScopeBox.SelectedIndex = 0;
            RemoveAccentsToggle.IsChecked = false;

            NumberingToggle.IsChecked = false;
            NumberStartBox.Value = 1;
            NumberPaddingBox.Value = 3;
            NumberSeparatorBox.Text = string.Empty;

            RemoveFirstToggle.IsChecked = false;
            RemoveFirstBox.Value = 0;
            RemoveLastToggle.IsChecked = false;
            RemoveLastBox.Value = 0;

            foreach (var file in Files)
            {
                file.IsEnabled = true;
                // file.PreviewName = file.OriginalName; // Replaced by ResetPreview which clears it
                file.ResetPreview();
            }

            _suppressUpdates = false;
            UpdatePreview();
        }

        private async void OnSaveConfig(object sender, RoutedEventArgs e)
        {
            var settings = ReadSettings();

            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "BatchRenamer_Config"
            };
            savePicker.FileTypeChoices.Add("Configuration JSON", new List<string> { ".json" });

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(savePicker, hwnd);

            var file = await savePicker.PickSaveFileAsync();
            if (file == null) return;

            try
            {
                var json = JsonSerializer.Serialize(settings, RenameSettingsContext.Default.RenameSettings);
                await FileIO.WriteTextAsync(file, json);

                ShowInfo(GetString("SaveConfigSuccessMessage"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowInfo(FormatString("ErrorMessage", ex.Message), InfoBarSeverity.Error);
            }
        }

        private async void OnLoadConfig(object sender, RoutedEventArgs e)
        {
            var openPicker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            openPicker.FileTypeFilter.Add(".json");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(openPicker, hwnd);

            var file = await openPicker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                var json = await FileIO.ReadTextAsync(file);
                var settings = JsonSerializer.Deserialize(json, RenameSettingsContext.Default.RenameSettings);
                if (settings == null)
                {
                    ShowInfo(GetString("LoadConfigErrorMessage"), InfoBarSeverity.Error);
                    return;
                }

                ApplySettings(settings);
                ShowInfo(GetString("LoadConfigSuccessMessage"), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowInfo(FormatString("LoadConfigErrorMessage", ex.Message), InfoBarSeverity.Error);
            }
        }

        private void ApplySettings(RenameSettings settings)
        {
            _suppressUpdates = true;

            PrefixBox.Text = settings.Prefix;
            SuffixBox.Text = settings.Suffix;
            SearchBox.Text = settings.Search;
            ReplaceBox.Text = settings.Replace;
            InsertTextBox.Text = settings.InsertText;
            InsertPositionBox.Value = settings.InsertPosition;
            RegexToggle.IsChecked = settings.UseRegex;
            RegexAllToggle.IsChecked = settings.ReplaceAllOccurrences;
            RemoveAccentsToggle.IsChecked = settings.RemoveAccents;

            // Set Case
            CaseModeBox.SelectedIndex = settings.CaseOption switch
            {
                NameCaseOption.Lower => 1,
                NameCaseOption.Upper => 2,
                NameCaseOption.Title => 3,
                _ => 0
            };

            // Set Scope
            ScopeBox.SelectedIndex = settings.Scope switch
            {
                RenameScope.Extension => 1,
                RenameScope.Full => 2,
                _ => 0
            };

            // Numbering
            NumberingToggle.IsChecked = settings.UseNumbering;
            NumberStartBox.Value = settings.NumberStart;
            NumberPaddingBox.Value = settings.NumberPadding;
            NumberSeparatorBox.Text = settings.NumberSeparator;

            // Remove characters
            RemoveFirstToggle.IsChecked = settings.RemoveFirstChars;
            RemoveFirstBox.Value = settings.RemoveFirstCount;
            RemoveLastToggle.IsChecked = settings.RemoveLastChars;
            RemoveLastBox.Value = settings.RemoveLastCount;

            _suppressUpdates = false;
            UpdatePreview();
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (Files.Count == 0)
            {
                ShowInfo(GetString("ApplyNoFilesMessage"), InfoBarSeverity.Warning);
                return;
            }

            // Ensure previews reflect the latest rules before applying
            UpdatePreview();

            // Comprehensive conflict detection including all files in the list (enabled or not)
            var finalFileStates = Files.Select(f => new
            {
                File = f,
                FinalPath = Path.Combine(f.Directory, (f.IsEnabled && !string.IsNullOrEmpty(f.PreviewName)) ? f.PreviewName : f.OriginalName)
            }).ToList();

            var conflictGroup = finalFileStates
                .GroupBy(s => s.FinalPath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .FirstOrDefault();

            if (conflictGroup != null)
            {
                var conflictingOriginalNames = string.Join(", ", conflictGroup.Select(c => $"'{c.File.OriginalName}'"));
                var targetName = Path.GetFileName(conflictGroup.Key);

                var errorMessage = FormatString("ConflictErrorMessage", conflictingOriginalNames, targetName);
                ShowInfo(errorMessage, InfoBarSeverity.Error);
                return;
            }

            var settings = ReadSettings();
            int success = 0;
            int skipped = 0;

            foreach (var file in Files)
            {
                if (!file.IsEnabled)
                {
                    skipped++;
                    continue;
                }

                var targetName = string.IsNullOrWhiteSpace(file.PreviewName) ? file.OriginalName : file.PreviewName;
                var targetPath = Path.Combine(file.Directory, targetName);

                if (string.Equals(file.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                if (File.Exists(targetPath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    File.Move(file.FullPath, targetPath);
                    file.RefreshAfterApply(targetPath);
                    success++;
                }
                catch (Exception ex)
                {
                    ShowInfo(FormatString("ErrorMessage", ex.Message), InfoBarSeverity.Error);
                }
            }

            UpdatePreview();

            if (success > 0)
            {
                ShowInfo(FormatString("ApplySuccessMessage", success, skipped), InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(GetString("ApplyNoneMessage"), InfoBarSeverity.Warning);
            }

            UpdateApplyState();
        }

        private void UpdatePreview()
        {
            var settings = ReadSettings();

            var numberingIndex = 0;

            for (var i = 0; i < Files.Count; i++)
            {
                Files[i].UpdatePreview(settings, numberingIndex);

                if (Files[i].IsEnabled)
                {
                    numberingIndex++;
                }
            }
            UpdateEmptyState();
            UpdateCounts();
            UpdateApplyState();
        }

        public void ImportFiles(IEnumerable<string> paths)
        {
            if (paths is null)
            {
                return;
            }

            AddFilePaths(paths);
        }

        private int AddFilePaths(IEnumerable<string> paths, string? rootFolder = null, bool includeSubfolders = false)
        {
            var added = 0;

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!File.Exists(path))
                {
                    continue;
                }

                if (Files.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var entry = new FileEntry(path, rootFolder, includeSubfolders);
                Files.Add(entry);
                added++;
            }

            if (added > 0)
            {
                UpdatePreview();
                UpdateEmptyState();
                ShowInfo(FormatString("AddFilesMessage", Files.Count), InfoBarSeverity.Success);
            }
            else
            {
                UpdateEmptyState();
            }

            UpdateCounts();
            UpdateApplyState();

            return added;
        }

        private RenameSettings ReadSettings()
        {
            var numberingActive = NumberingToggle?.IsChecked == true;
            var separator = NumberSeparatorBox?.Text;
            if (string.IsNullOrEmpty(separator))
            {
                separator = "_";
            }

            return new RenameSettings
            {
                Prefix = PrefixBox.Text ?? string.Empty,
                Suffix = SuffixBox.Text ?? string.Empty,
                Search = SearchBox.Text ?? string.Empty,
                Replace = ReplaceBox.Text ?? string.Empty,
                InsertText = InsertTextBox.Text ?? string.Empty,
                InsertPosition = (int)(InsertPositionBox.Value >= 0 ? InsertPositionBox.Value : 0),
                UseNumbering = numberingActive,
                NumberStart = (int)(NumberStartBox.Value > 0 ? NumberStartBox.Value : 1),
                NumberPadding = (int)(NumberPaddingBox.Value > 0 ? NumberPaddingBox.Value : 2),
                NumberSeparator = separator,
                CaseOption = GetCaseOption(),
                Scope = GetScopeOption(),
                RemoveAccents = RemoveAccentsToggle?.IsChecked == true,
                UseRegex = RegexToggle?.IsChecked == true,
                ReplaceAllOccurrences = RegexAllToggle?.IsChecked == true,
                RemoveFirstChars = RemoveFirstToggle?.IsChecked == true,
                RemoveFirstCount = (int)(RemoveFirstBox.Value >= 0 ? RemoveFirstBox.Value : 0),
                RemoveLastChars = RemoveLastToggle?.IsChecked == true,
                RemoveLastCount = (int)(RemoveLastBox.Value >= 0 ? RemoveLastBox.Value : 0)
            };
        }

        private RenameScope GetScopeOption()
        {
            if (ScopeBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag switch
                {
                    "Extension" => RenameScope.Extension,
                    "Full" => RenameScope.Full,
                    _ => RenameScope.Name
                };
            }
            return RenameScope.Name;
        }

        private NameCaseOption GetCaseOption()
        {
            if (CaseModeBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag switch
                {
                    "Lower" => NameCaseOption.Lower,
                    "Upper" => NameCaseOption.Upper,
                    "Title" => NameCaseOption.Title,
                    _ => NameCaseOption.None
                };
            }

            return NameCaseOption.None;
        }

        private void UpdateEmptyState()
        {
            EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCounts()
        {
            OnPropertyChanged(nameof(TotalFilesCount));
            OnPropertyChanged(nameof(EnabledFilesCount));
            OnPropertyChanged(nameof(ChangedFilesCount));
            OnPropertyChanged(nameof(NomHeaderText));
            OnPropertyChanged(nameof(ApercuHeaderText));
        }

        private void UpdateApplyState()
        {
            var anyEnabled = Files.Any(f => f.IsEnabled);
            // Since PreviewName is now empty when no change is needed, checking for !IsNullOrEmpty is sufficient
            // We also need to check if the explicit string comparison is still needed if PreviewName was somehow set to OriginalName explicitly.
            var anyChange = Files.Any(f => f.IsEnabled && !string.IsNullOrEmpty(f.PreviewName));
            CanApply = anyEnabled && anyChange;
        }

        private async Task<bool> ConfirmIncludeSubfolders(string folderName)
        {
            var dialog = new ContentDialog
            {
                Title = GetString("ConfirmSubfoldersTitle"),
                Content = FormatString("ConfirmSubfoldersContent", folderName),
                PrimaryButtonText = GetString("ConfirmSubfoldersYes"),
                CloseButtonText = GetString("ConfirmSubfoldersNo"),
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await ShowDialogAsync(dialog);
            return result == ContentDialogResult.Primary;
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            await _dialogGate.WaitAsync();
            try
            {
                dialog.XamlRoot = (Content as FrameworkElement)?.XamlRoot;
                return await dialog.ShowAsync();
            }
            finally
            {
                _dialogGate.Release();
            }
        }

        private void ShowInfo(string message, InfoBarSeverity severity)
        {
            StatusBar.Title = message;
            StatusBar.Message = string.Empty;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (var item in e.NewItems.OfType<FileEntry>())
                {
                    item.PropertyChanged += OnFileEntryPropertyChanged;
                }
            }

            if (e.OldItems is not null)
            {
                foreach (var item in e.OldItems.OfType<FileEntry>())
                {
                    item.PropertyChanged -= OnFileEntryPropertyChanged;
                }
            }

            UpdateCounts();
            UpdateApplyState();
            UpdateDirectoryColumnState();
            UpdateFilteredFiles();
        }

        private void OnFileEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileEntry.IsEnabled))
            {
                UpdateCounts();
                UpdateApplyState();
                // Mise à jour du filtre uniquement si le filtre actuel dépend de IsEnabled
                if (_currentFilter == FileFilter.EnabledOnly || _currentFilter == FileFilter.DisabledOnly)
                {
                    UpdateFilteredFiles();
                }
            }
            else if (e.PropertyName == nameof(FileEntry.PreviewName))
            {
                UpdateApplyState();
                // Mise à jour du filtre uniquement si le filtre actuel dépend de PreviewName
                if (_currentFilter == FileFilter.WithChanges || _currentFilter == FileFilter.WithoutChanges)
                {
                    UpdateFilteredFiles();
                }
            }
        }

        private void UpdateDirectoryColumnState()
        {
            var columnWidth = ShowDirectoryColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            var columnVisibility = ShowDirectoryColumn ? Visibility.Visible : Visibility.Collapsed;

            foreach (var file in Files)
            {
                file.DirectoryColumnWidth = columnWidth;
                file.DirectoryColumnVisibility = columnVisibility;
            }

            OnPropertyChanged(nameof(ShowDirectoryColumn));
            OnPropertyChanged(nameof(DirectoryColumnWidth));
            OnPropertyChanged(nameof(DirectoryColumnVisibility));
        }

        public int TotalFilesCount => Files.Count;
        public int EnabledFilesCount => Files.Count(f => f.IsEnabled);
        public int ChangedFilesCount => Files.Count(f => f.IsEnabled && !string.IsNullOrEmpty(f.PreviewName));
        public string NomHeaderText => FormatString("NomHeaderText", TotalFilesCount);
        public string ApercuHeaderText => FormatString("ApercuHeaderText", ChangedFilesCount);
        public bool ShowDirectoryColumn => Files.Any(f => !string.IsNullOrEmpty(f.RootFolder));
        public GridLength DirectoryColumnWidth => ShowDirectoryColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        public Visibility DirectoryColumnVisibility => ShowDirectoryColumn ? Visibility.Visible : Visibility.Collapsed;
        public bool CanApply
        {
            get => _canApply;
            private set
            {
                if (_canApply == value)
                {
                    return;
                }

                _canApply = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private string GetString(string resourceKey)
        {
            if (_resources == null)
            {
                return resourceKey;
            }
            
            try
            {
                var value = _resources.GetString(resourceKey);
                return string.IsNullOrEmpty(value) ? resourceKey : value;
            }
            catch
            {
                return resourceKey;
            }
        }

        private string FormatString(string resourceKey, params object[] args)
        {
            var format = GetString(resourceKey);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch
            {
                // If formatting fails, return the format string with args appended
                return $"{format} [{string.Join(", ", args)}]";
            }
        }

        private void UpdateFilteredFiles()
        {
            // Null safety pour mode Release - vérifier que tout est initialisé
            if (!_isInitialized || _isUpdatingFilter || _dispatcherQueue == null || Files == null || FilteredFiles == null) 
                return;
            
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_isUpdatingFilter || FilteredFiles == null) return;
                _isUpdatingFilter = true;
                
                try
                {
                    FilteredFiles.Clear();
                    foreach (var file in Files)
                    {
                        if (FilterFile(file))
                        {
                            FilteredFiles.Add(file);
                        }
                    }
                }
                finally
                {
                    _isUpdatingFilter = false;
                }
            });
        }

        private bool FilterFile(FileEntry file)
        {
            return _currentFilter switch
            {
                FileFilter.EnabledOnly => file.IsEnabled,
                FileFilter.DisabledOnly => !file.IsEnabled,
                FileFilter.WithChanges => !string.IsNullOrEmpty(file.PreviewName),
                FileFilter.WithoutChanges => string.IsNullOrEmpty(file.PreviewName),
                _ => true
            };
        }

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
            {
                return;
            }

            _currentFilter = Enum.Parse<FileFilter>((string)item.Tag);
            UpdateFilteredFiles();
        }
    }

    public class FileEntry : INotifyPropertyChanged
    {
        private string _previewName = string.Empty;
        private bool _isEnabled = true;
        private GridLength _directoryColumnWidth = new GridLength(1, GridUnitType.Star);
        private Visibility _directoryColumnVisibility = Visibility.Visible;
        private static readonly Brush HighlightBrush = new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);
        private static Brush? _defaultPreviewBrush;
        private static Brush DefaultPreviewBrush
        {
            get
            {
                if (_defaultPreviewBrush == null)
                {
                    try
                    {
                        _defaultPreviewBrush = (Brush)Application.Current.Resources["SystemControlForegroundBaseHighBrush"];
                    }
                    catch
                    {
                        _defaultPreviewBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
                    }
                }
                return _defaultPreviewBrush;
            }
        }

        public FileEntry(string fullPath, string? rootFolder = null, bool includeSubfolders = false)
        {
            FullPath = fullPath;
            OriginalName = Path.GetFileName(fullPath);
            Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            Extension = Path.GetExtension(fullPath);
            RootFolder = rootFolder ?? string.Empty;

            if (!string.IsNullOrEmpty(RootFolder))
            {
                IsRootFolderItem = string.Equals(Directory.TrimEnd(Path.DirectorySeparatorChar), RootFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                IsFromSubfolder = includeSubfolders && !IsRootFolderItem;
                DisplayDirectory = IsRootFolderItem
                    ? Path.GetFileName(RootFolder)
                    : Path.GetRelativePath(RootFolder, Directory);
            }
            else
            {
                DisplayDirectory = Directory;
            }
        }

        public string FullPath { get; private set; }
        public string OriginalName { get; private set; }
        public string Directory { get; }
        public string Extension { get; private set; }
        public string RootFolder { get; }
        public bool IsRootFolderItem { get; }
        public bool IsFromSubfolder { get; }
        public string DisplayDirectory { get; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewForeground));
            }
        }

        public string PreviewName
        {
            get => _previewName;
            set
            {
                if (_previewName == value)
                {
                    return;
                }

                _previewName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewForeground));
            }
        }

        public Brush PreviewForeground => ShouldHighlight ? HighlightBrush : DefaultPreviewBrush;
        public bool ShouldHighlight => IsEnabled && !string.IsNullOrEmpty(PreviewName);

        public GridLength DirectoryColumnWidth
        {
            get => _directoryColumnWidth;
            set
            {
                if (_directoryColumnWidth.Equals(value))
                {
                    return;
                }

                _directoryColumnWidth = value;
                OnPropertyChanged();
            }
        }

        public Visibility DirectoryColumnVisibility
        {
            get => _directoryColumnVisibility;
            set
            {
                if (_directoryColumnVisibility == value)
                {
                    return;
                }

                _directoryColumnVisibility = value;
                OnPropertyChanged();
            }
        }

        public void UpdatePreview(RenameSettings settings, int index)
        {
            string workingBaseName;
            string preservedPart = string.Empty;

            switch (settings.Scope)
            {
                case RenameScope.Extension:
                    // Operate on extension without the dot
                    var ext = Extension;
                    if (ext.StartsWith('.'))
                    {
                        ext = ext.Substring(1);
                    }
                    workingBaseName = ext;
                    preservedPart = Path.GetFileNameWithoutExtension(OriginalName);
                    break;

                case RenameScope.Full:
                    workingBaseName = OriginalName;
                    preservedPart = string.Empty;
                    break;

                case RenameScope.Name:
                default:
                    workingBaseName = Path.GetFileNameWithoutExtension(OriginalName);
                    preservedPart = Extension;
                    break;
            }

            if (!string.IsNullOrWhiteSpace(settings.Search))
            {
                var replacement = settings.Replace ?? string.Empty;

                if (settings.UseRegex)
                {
                    try
                    {
                        var regex = new Regex(settings.Search, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        workingBaseName = settings.ReplaceAllOccurrences
                            ? regex.Replace(workingBaseName, replacement)
                            : regex.Replace(workingBaseName, replacement, 1);
                    }
                    catch (ArgumentException)
                    {
                        // Invalid regex: leave baseName unchanged
                    }
                }
                else
                {
                    workingBaseName = workingBaseName.Replace(settings.Search, replacement, StringComparison.OrdinalIgnoreCase);
                }
            }

            // Remove characters by position
            if (settings.RemoveFirstChars && settings.RemoveFirstCount > 0)
            {
                var count = Math.Min(settings.RemoveFirstCount, workingBaseName.Length);
                workingBaseName = workingBaseName.Substring(count);
            }

            if (settings.RemoveLastChars && settings.RemoveLastCount > 0)
            {
                var count = Math.Min(settings.RemoveLastCount, workingBaseName.Length);
                if (count < workingBaseName.Length)
                {
                    workingBaseName = workingBaseName.Substring(0, workingBaseName.Length - count);
                }
                else
                {
                    workingBaseName = string.Empty;
                }
            }

            workingBaseName = settings.Prefix + workingBaseName + settings.Suffix;

            if (settings.RemoveAccents)
            {
                workingBaseName = RemoveDiacritics(workingBaseName);
            }

            workingBaseName = ApplyCase(workingBaseName, settings.CaseOption);

            if (!string.IsNullOrEmpty(settings.InsertText))
            {
                var pos = Math.Clamp(settings.InsertPosition, 0, workingBaseName.Length);
                workingBaseName = workingBaseName.Insert(pos, settings.InsertText);
            }

            if (settings.UseNumbering)
            {
                var number = settings.NumberStart + index;
                var formatted = number.ToString($"D{settings.NumberPadding}");
                workingBaseName = $"{workingBaseName}{settings.NumberSeparator}{formatted}";
            }

            // Reassemble
            string finalName;
            if (settings.Scope == RenameScope.Extension)
            {
                if (string.IsNullOrEmpty(workingBaseName))
                {
                    finalName = preservedPart;
                }
                else
                {
                    finalName = preservedPart + "." + workingBaseName;
                }
            }
            else if (settings.Scope == RenameScope.Name)
            {
                finalName = workingBaseName + preservedPart; // preservedPart is Extension (with dot)
            }
            else // Full
            {
                finalName = workingBaseName;
            }

            var sanitized = Sanitize(finalName);
            
            // If the calculated name is identical to the original, we set PreviewName to empty.
            if (string.Equals(sanitized, OriginalName, StringComparison.Ordinal))
            {
                PreviewName = string.Empty;
            }
            else
            {
                PreviewName = sanitized;
            }
        }

        public void ResetPreview()
        {
            PreviewName = string.Empty;
        }

        public void RefreshAfterApply(string newPath)
        {
            FullPath = newPath;
            OriginalName = Path.GetFileName(newPath);
            Extension = Path.GetExtension(newPath);
            // After applying, the file is in its "new original" state.
            // Reset preview to empty to signify no pending changes relative to the new original.
            PreviewName = string.Empty;
            OnPropertyChanged(nameof(OriginalName));
            OnPropertyChanged(nameof(Extension));
        }

        private static string Sanitize(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var ch in invalidChars)
            {
                value = value.Replace(ch, '_');
            }

            return value;
        }

        private static string ApplyCase(string value, NameCaseOption option)
        {
            return option switch
            {
                NameCaseOption.Lower => value.ToLowerInvariant(),
                NameCaseOption.Upper => value.ToUpperInvariant(),
                NameCaseOption.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower()),
                _ => value
            };
        }

        private static string RemoveDiacritics(string text)
        {
            var normalizedString = text.Normalize(NormalizationForm.FormD);
            var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

            for (int i = 0; i < normalizedString.Length; i++)
            {
                char c = normalizedString[i];
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    stringBuilder.Append(c);
                }
            }

            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class RenameSettings
    {
        public string Prefix { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public string Search { get; set; } = string.Empty;
        public string Replace { get; set; } = string.Empty;
        public string InsertText { get; set; } = string.Empty;
        public int InsertPosition { get; set; }
        public bool UseNumbering { get; set; }
        public int NumberStart { get; set; } = 1;
        public int NumberPadding { get; set; } = 2;
        public string NumberSeparator { get; set; } = "_";
        public NameCaseOption CaseOption { get; set; } = NameCaseOption.None;
        public RenameScope Scope { get; set; } = RenameScope.Name;
        public bool RemoveAccents { get; set; }
        public bool UseRegex { get; set; }
        public bool ReplaceAllOccurrences { get; set; }
        public bool RemoveFirstChars { get; set; }
        public int RemoveFirstCount { get; set; }
        public bool RemoveLastChars { get; set; }
        public int RemoveLastCount { get; set; }
    }

    public sealed class BoolToOpacityConverter : IValueConverter
    {
        public double EnabledOpacity { get; set; } = 1d;
        public double DisabledOpacity { get; set; } = 0.4d;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                return b ? EnabledOpacity : DisabledOpacity;
            }

            return EnabledOpacity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is double d)
            {
                return d >= ((EnabledOpacity + DisabledOpacity) / 2);
            }

            return true;
        }
    }

    public enum NameCaseOption
    {
        None,
        Lower,
        Upper,
        Title
    }

    public enum RenameScope
    {
        Name,
        Extension,
        Full
    }

    public enum FileFilter
    {
        All,
        EnabledOnly,
        DisabledOnly,
        WithChanges,
        WithoutChanges
    }

    // JsonSerializerContext pour le trimming-safe
    [System.Text.Json.Serialization.JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNameCaseInsensitive = true)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(RenameSettings))]
    internal partial class RenameSettingsContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
