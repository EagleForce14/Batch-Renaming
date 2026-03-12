using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text;
using Windows.Globalization;
using System.Collections.Generic;
using Windows.ApplicationModel.Resources;


namespace BulkRenamer
{
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

    public sealed class NavigationToRenamingArgs
    {
        public RenameSettings Settings { get; init; } = new();
        public bool FromHistory { get; init; }
    }

    public class HistoryEntry : INotifyPropertyChanged
    {
        private bool _isUndone;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int FileCount { get; set; }
        public RenameSettings Settings { get; set; } = new();
        public List<FileHistoryRecord> Records { get; set; } = new();

        public bool IsUndone
        {
            get => _isUndone;
            set
            {
                if (_isUndone != value)
                {
                    _isUndone = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(CanUndo));
                    OnPropertyChanged(nameof(CanRedo));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Summary
        {
            get
            {
                try
                {
                    var loader = new ResourceLoader();
                    var key = FileCount == 1 ? "History_FileCountSingular" : "History_FileCountPlural";
                    var fileText = string.Format(loader.GetString(key), FileCount);
                    return $"{Timestamp:g} • {fileText}";
                }
                catch
                {
                    return $"{Timestamp:g} • {FileCount} files";
                }
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public string StatusText
        {
            get
            {
                try
                {
                    var loader = new ResourceLoader();
                    return IsUndone ? loader.GetString("History_Status_Undone") : loader.GetString("History_Status_Applied");
                }
                catch
                {
                    return IsUndone ? "Undone" : "Applied";
                }
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool CanUndo => !IsUndone;

        [System.Text.Json.Serialization.JsonIgnore]
        public bool CanRedo => IsUndone;

        [System.Text.Json.Serialization.JsonIgnore]
        public Microsoft.UI.Xaml.Media.Brush StatusColor => IsUndone 
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed) 
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);

        [System.Text.Json.Serialization.JsonIgnore]
        public string SettingsSummary {
            get {
                try
                {
                    var loader = new ResourceLoader();
                    var sb = new StringBuilder();
                    if (!string.IsNullOrEmpty(Settings.Prefix)) sb.Append(string.Format(loader.GetString("History_Prefix"), Settings.Prefix));
                    if (!string.IsNullOrEmpty(Settings.Suffix)) sb.Append(string.Format(loader.GetString("History_Suffix"), Settings.Suffix));
                    if (!string.IsNullOrEmpty(Settings.Search)) sb.Append(string.Format(loader.GetString("History_Search"), Settings.Search));
                    if (Settings.CaseOption != NameCaseOption.None) 
                    {
                        var caseVal = loader.GetString($"History_Case_{Settings.CaseOption}");
                        sb.Append(string.Format(loader.GetString("History_Case"), caseVal));
                    }
                    if (Settings.UseNumbering) sb.Append(loader.GetString("History_Numbering"));
                    if (sb.Length == 0) return loader.GetString("History_CustomRules");
                    return sb.ToString();
                }
                catch
                {
                    return "Custom Rules";
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class FileHistoryRecord
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
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

    [System.Text.Json.Serialization.JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNameCaseInsensitive = true)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(RenameSettings))]
    internal partial class RenameSettingsContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
