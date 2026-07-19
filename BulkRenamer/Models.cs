using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text;
using Windows.Globalization;
using System.Collections.Generic;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Xml.Linq;
using System.Collections.Concurrent;


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
        private readonly ConcurrentDictionary<string, object?> _metadata = new(StringComparer.OrdinalIgnoreCase);
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

        public FileEntry(string fullPath, string? rootFolder = null, bool includeSubfolders = false, int importIndex = 0)
        {
            FullPath = fullPath;
            OriginalName = Path.GetFileName(fullPath);
            Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            Extension = Path.GetExtension(fullPath);
            RootFolder = rootFolder ?? string.Empty;
            ImportIndex = importIndex;

            try
            {
                var info = new FileInfo(fullPath);
                FileSize = info.Length;
                CreatedAt = info.CreationTime;
                ModifiedAt = info.LastWriteTime;
            }
            catch
            {
                FileSize = 0;
                CreatedAt = DateTime.MinValue;
                ModifiedAt = DateTime.MinValue;
            }

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
        public int ImportIndex { get; }
        public long FileSize { get; }
        public DateTime CreatedAt { get; }
        public DateTime ModifiedAt { get; }

        public async Task LoadMetadataAsync()
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(FullPath);
                await LoadTypedImageMetadataAsync(file);
                await LoadTypedMusicMetadataAsync(file);
                await LoadTypedVideoMetadataAsync(file);

                await LoadPropertyBatchAsync(file, "System.Photo.DateTaken", "System.Image.HorizontalSize", "System.Image.VerticalSize", "System.Photo.CameraModel");
                await LoadPropertyBatchAsync(file, "System.Photo.LensModel");
                await LoadPropertyBatchAsync(file, "System.Photo.ISOSpeed");
                await LoadPropertyBatchAsync(file, "System.Photo.FNumber");
                await LoadPropertyBatchAsync(file, "System.Photo.ExposureTime");
                await LoadPropertyBatchAsync(file, "System.Music.TrackNumber", "System.Music.DiscNumber", "System.Music.PartOfSet", "System.Music.DisplayArtist", "System.Music.AlbumTitle", "System.Music.AlbumArtist", "System.Music.Year", "System.Music.Genre");
                await LoadPropertyBatchAsync(file, "System.Media.Duration", "System.Media.DateEncoded");
                await LoadPropertyBatchAsync(file, "System.Video.FrameWidth", "System.Video.FrameHeight", "System.Video.FrameRate", "System.Video.Compression", "System.Video.EncodingBitrate");
                await LoadPropertyBatchAsync(file, "System.Title", "System.Author", "System.Subject", "System.Keywords", "System.Company");
                await LoadPropertyBatchAsync(file, "System.Document.PageCount", "System.Document.DateCreated", "System.Document.DateSaved");
            }
            catch
            {
                // The direct file reader below can still provide document metadata.
            }

            await Task.Run(LoadDocumentMetadataFallback);
            OnPropertyChanged(nameof(MetadataLoaded));
        }

        private async Task LoadPropertyBatchAsync(StorageFile file, params string[] propertyNames)
        {
            try
            {
                var values = await file.Properties.RetrievePropertiesAsync(propertyNames);
                foreach (var pair in values) SetMetadata(pair.Key, pair.Value);
            }
            catch
            {
                // A missing property handler must not cancel the other metadata categories.
            }
        }

        private async Task LoadTypedImageMetadataAsync(StorageFile file)
        {
            try
            {
                var image = await file.Properties.GetImagePropertiesAsync();
                SetMetadata("System.Image.HorizontalSize", image.Width);
                SetMetadata("System.Image.VerticalSize", image.Height);
                if (image.DateTaken.Year > 1601) SetMetadata("System.Photo.DateTaken", image.DateTaken);
                SetMetadata("System.Photo.CameraModel", image.CameraModel);
                SetMetadata("System.GPS.LatitudeDecimal", image.Latitude);
                SetMetadata("System.GPS.LongitudeDecimal", image.Longitude);
                SetMetadata("System.Title", image.Title);
            }
            catch { }
        }

        private async Task LoadTypedMusicMetadataAsync(StorageFile file)
        {
            try
            {
                var music = await file.Properties.GetMusicPropertiesAsync();
                SetMetadata("System.Title", music.Title);
                SetMetadata("System.Music.DisplayArtist", music.Artist);
                SetMetadata("System.Music.AlbumTitle", music.Album);
                SetMetadata("System.Music.AlbumArtist", music.AlbumArtist);
                SetMetadata("System.Music.TrackNumber", music.TrackNumber);
                SetMetadata("System.Music.Year", music.Year);
                SetMetadata("System.Music.Genre", music.Genre);
                SetMetadata("System.Media.Duration", music.Duration.Ticks);
            }
            catch { }
        }

        private async Task LoadTypedVideoMetadataAsync(StorageFile file)
        {
            try
            {
                var video = await file.Properties.GetVideoPropertiesAsync();
                SetMetadata("System.Title", video.Title);
                SetMetadata("System.Video.FrameWidth", video.Width);
                SetMetadata("System.Video.FrameHeight", video.Height);
                SetMetadata("System.Video.EncodingBitrate", video.Bitrate);
                SetMetadata("System.GPS.LatitudeDecimal", video.Latitude);
                SetMetadata("System.GPS.LongitudeDecimal", video.Longitude);
                SetMetadata("System.Media.Duration", video.Duration.Ticks);
            }
            catch { }
        }

        public bool MetadataLoaded => _metadata.Count > 0;

        private void LoadDocumentMetadataFallback()
        {
            var extension = Extension.ToLowerInvariant();
            if (extension is ".docx" or ".xlsx" or ".pptx")
            {
                LoadOpenXmlMetadata();
            }
            else if (extension == ".pdf")
            {
                LoadPdfMetadata();
            }
        }

        private void LoadOpenXmlMetadata()
        {
            try
            {
                using var archive = ZipFile.OpenRead(FullPath);
                ReadOpenXmlPart(archive, "docProps/core.xml", true);
                ReadOpenXmlPart(archive, "docProps/app.xml", false);
            }
            catch { }
        }

        private void ReadOpenXmlPart(ZipArchive archive, string path, bool core)
        {
            var entry = archive.GetEntry(path);
            if (entry is null) return;
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            string? Value(string localName) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

            if (core)
            {
                SetMetadata("System.Title", Value("title"));
                SetMetadata("System.Author", Value("creator"));
                SetMetadata("System.Subject", Value("subject"));
                SetMetadata("System.Keywords", Value("keywords"));
                SetMetadata("System.Document.DateCreated", ParseDate(Value("created")));
                SetMetadata("System.Document.DateSaved", ParseDate(Value("modified")));
            }
            else
            {
                SetMetadata("System.Company", Value("Company"));
                SetMetadata("System.Document.PageCount", Value("Pages") ?? Value("Slides") ?? Value("Worksheets"));
            }
        }

        private void LoadPdfMetadata()
        {
            try
            {
                var bytes = File.ReadAllBytes(FullPath);
                var text = Encoding.Latin1.GetString(bytes);
                SetMetadata("System.Document.PageCount", Regex.Matches(text, @"/Type\s*/Page(?!s)\b").Count);
                SetMetadata("System.Title", PdfInfo(text, "Title"));
                SetMetadata("System.Author", PdfInfo(text, "Author"));
                SetMetadata("System.Subject", PdfInfo(text, "Subject"));
                SetMetadata("System.Keywords", PdfInfo(text, "Keywords"));
                SetMetadata("System.Document.DateCreated", ParsePdfDate(PdfInfo(text, "CreationDate")));
                SetMetadata("System.Document.DateSaved", ParsePdfDate(PdfInfo(text, "ModDate")));
            }
            catch { }
        }

        private static string? PdfInfo(string text, string name)
        {
            var match = Regex.Match(text, $@"/{Regex.Escape(name)}\s*\((?<value>(?:\\.|[^\\)])*)\)");
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : null;
        }

        private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;
        private static DateTimeOffset? ParsePdfDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var normalized = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
            return DateTimeOffset.TryParseExact(normalized[..Math.Min(normalized.Length, 14)], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? date : null;
        }

        private void SetMetadata(string key, object? value)
        {
            if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
            if (value is byte or short or int or long or ushort or uint or ulong or float or double or decimal)
            {
                if (Convert.ToDouble(value, CultureInfo.InvariantCulture) == 0) return;
            }
            if (value is DateTimeOffset dto && dto.Year <= 1601) return;
            if (value is DateTime dt && dt.Year <= 1601) return;
            _metadata[key] = value;
        }

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

            if (settings.UseTemplate && !string.IsNullOrWhiteSpace(settings.NameTemplate))
            {
                workingBaseName = ExpandTemplate(settings.NameTemplate, index);
                preservedPart = Regex.IsMatch(settings.NameTemplate, @"\{filename(?::[^{}]+)?\}", RegexOptions.IgnoreCase)
                    ? string.Empty
                    : Extension;
            }
            else switch (settings.Scope)
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
            if (!settings.UseTemplate && settings.Scope == RenameScope.Extension)
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
            else if (settings.UseTemplate || settings.Scope == RenameScope.Name)
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

        private string ExpandTemplate(string template, int index)
        {
            return Regex.Replace(template, @"\{(?<key>[a-z-]+)(?::(?<format>[^{}]+))?\}", match =>
            {
                var key = match.Groups["key"].Value.ToLowerInvariant();
                var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;

                try
                {
                    return key switch
                    {
                        "name" => Path.GetFileNameWithoutExtension(OriginalName),
                        "ext" => Extension.TrimStart('.'),
                        "filename" => OriginalName,
                        "index" => (index + 1).ToString(format ?? "0", CultureInfo.CurrentCulture),
                        "created" => CreatedAt.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                        "modified" => ModifiedAt.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                        "folder" => Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar)),
                        "parent-folder" => Path.GetFileName(Path.GetDirectoryName(Directory.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty),
                        "size" => FileSize.ToString(format ?? "0", CultureInfo.CurrentCulture),
                        "date-taken" => FormatMetadataDate("System.Photo.DateTaken", format),
                        "width" => MetadataText("System.Image.HorizontalSize", "System.Video.FrameWidth"),
                        "height" => MetadataText("System.Image.VerticalSize", "System.Video.FrameHeight"),
                        "orientation" => GetOrientation(),
                        "camera-model" => MetadataText("System.Photo.CameraModel"),
                        "lens" => MetadataText("System.Photo.LensModel"),
                        "iso" => FormatMetadataNumber("System.Photo.ISOSpeed", null),
                        "aperture" => PrefixMetadata("f", "System.Photo.FNumber"),
                        "exposure" => FormatExposureTime(),
                        "latitude" => MetadataText("System.GPS.LatitudeDecimal"),
                        "longitude" => MetadataText("System.GPS.LongitudeDecimal"),
                        "track" => FormatMetadataNumber("System.Music.TrackNumber", format),
                        "disc" => FormatMetadataNumber("System.Music.DiscNumber", format) is { Length: > 0 } disc ? disc : ParsePart("System.Music.PartOfSet", 0, format),
                        "disc-total" => ParsePart("System.Music.PartOfSet", 1, format),
                        "audio-title" => MetadataText("System.Title"),
                        "document-title" => MetadataText("System.Title"),
                        "artist" => MetadataText("System.Music.DisplayArtist"),
                        "album" => MetadataText("System.Music.AlbumTitle"),
                        "album-artist" => MetadataText("System.Music.AlbumArtist"),
                        "year" => MetadataText("System.Music.Year"),
                        "genre" => MetadataText("System.Music.Genre"),
                        "duration" => FormatDuration(),
                        "resolution" => GetResolution(),
                        "quality" => GetQuality(),
                        "fps" => FormatDividedNumber("System.Video.FrameRate", 1000d),
                        "codec" => MetadataText("System.Video.Compression"),
                        "bitrate" => FormatDividedNumber("System.Video.EncodingBitrate", 1000d),
                        "media-created" => FormatMetadataDate("System.Media.DateEncoded", format),
                        "author" => MetadataText("System.Author"),
                        "subject" => MetadataText("System.Subject"),
                        "keywords" => MetadataText("System.Keywords"),
                        "pages" => MetadataText("System.Document.PageCount"),
                        "company" => MetadataText("System.Company"),
                        "document-created" => CreatedAt.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                        "document-saved" => ModifiedAt.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                        _ => match.Value
                    };
                }
                catch (FormatException)
                {
                    return match.Value;
                }
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private object? MetadataValue(params string[] keys) => keys.Select(k => _metadata.TryGetValue(k, out var value) ? value : null).FirstOrDefault(v => v is not null);
        private string MetadataText(params string[] keys)
        {
            var value = MetadataValue(keys);
            return value switch
            {
                null => string.Empty,
                string[] strings => string.Join(", ", strings.Where(s => !string.IsNullOrWhiteSpace(s))),
                IEnumerable<string> strings => string.Join(", ", strings.Where(s => !string.IsNullOrWhiteSpace(s))),
                _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty
            };
        }
        private string FormatMetadataDate(string key, string? format)
        {
            var value = MetadataValue(key);
            return value switch
            {
                DateTimeOffset dto => dto.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                DateTime dt => dt.ToString(format ?? "yyyy-MM-dd", CultureInfo.CurrentCulture),
                _ => string.Empty
            };
        }
        private string FormatMetadataNumber(string key, string? format) => double.TryParse(MetadataText(key), out var n) ? n.ToString(format ?? "0", CultureInfo.CurrentCulture) : string.Empty;
        private string FormatDividedNumber(string key, double divisor) => double.TryParse(MetadataText(key), out var n) ? (n / divisor).ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
        private string ParsePart(string key, int part, string? format)
        {
            var pieces = MetadataText(key).Split('/');
            return pieces.Length > part && int.TryParse(pieces[part], out var n) ? n.ToString(format ?? "0", CultureInfo.CurrentCulture) : string.Empty;
        }
        private string PrefixMetadata(string prefix, string key) { var value = MetadataText(key); return string.IsNullOrEmpty(value) ? string.Empty : prefix + value; }
        private string FormatExposureTime()
        {
            var value = MetadataValue("System.Photo.ExposureTime");
            if (value is null) return string.Empty;
            try
            {
                var seconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (seconds <= 0) return string.Empty;
                if (seconds < 1)
                {
                    var denominator = Math.Max(1, (int)Math.Round(1d / seconds));
                    return $"1-{denominator}s";
                }
                return $"{seconds.ToString("0.###", CultureInfo.CurrentCulture)}s";
            }
            catch
            {
                return string.Empty;
            }
        }
        private string GetResolution() { var w = MetadataText("System.Video.FrameWidth", "System.Image.HorizontalSize"); var h = MetadataText("System.Video.FrameHeight", "System.Image.VerticalSize"); return string.IsNullOrEmpty(w) || string.IsNullOrEmpty(h) ? string.Empty : $"{w}x{h}"; }
        private string GetOrientation() { var w = Convert.ToDouble(MetadataValue("System.Image.HorizontalSize") ?? 0); var h = Convert.ToDouble(MetadataValue("System.Image.VerticalSize") ?? 0); return w == 0 || h == 0 ? string.Empty : w > h ? "Landscape" : w < h ? "Portrait" : "Square"; }
        private string GetQuality() { var h = Convert.ToInt32(MetadataValue("System.Video.FrameHeight") ?? 0); return h >= 2160 ? "4K" : h >= 1440 ? "1440p" : h >= 1080 ? "1080p" : h >= 720 ? "720p" : h > 0 ? $"{h}p" : string.Empty; }
        private string FormatDuration() { if (!long.TryParse(MetadataText("System.Media.Duration"), out var ticks)) return string.Empty; var duration = TimeSpan.FromTicks(ticks); return duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss"); }

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
        public bool UseTemplate { get; set; }
        public string NameTemplate { get; set; } = string.Empty;
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
                    if (Settings.UseTemplate && !string.IsNullOrWhiteSpace(Settings.NameTemplate)) sb.Append(string.Format(loader.GetString("History_Template"), Settings.NameTemplate));
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

    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var visible = value is true;
            if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
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

    public enum SortOption
    {
        Original,
        Name,
        Extension,
        Directory,
        Size,
        Modified,
        Created
    }

    [System.Text.Json.Serialization.JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNameCaseInsensitive = true)]
    [System.Text.Json.Serialization.JsonSerializable(typeof(RenameSettings))]
    internal partial class RenameSettingsContext : System.Text.Json.Serialization.JsonSerializerContext
    {
    }
}
