using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using System.Text.Json;

namespace BulkRenamer
{
    public static class HistoryManager
    {
        private const string FileName = "history.json";
        public static ObservableCollection<HistoryEntry> History { get; private set; } = new ObservableCollection<HistoryEntry>();

        public static async Task LoadAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var item = await folder.TryGetItemAsync(FileName);
                if (item != null && item is StorageFile file)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                    if (list != null)
                    {
                        History.Clear();
                        // Add in reverse order or just sort? Let's keep file order but maybe UI sorts.
                        // Actually let's assume file stores newest last.
                        // We probably want to visualize Newest First.
                        list.Reverse();
                        foreach (var entry in list)
                        {
                            History.Add(entry);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        public static async Task AddEntryAsync(HistoryEntry entry)
        {
            History.Insert(0, entry);
            await SaveAsync();
        }

        public static async Task SaveAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
                
                // We want to store in chronological order ideally? Or just store as is.
                var list = new List<HistoryEntry>(History);
                list.Reverse(); // Store oldest first so append works logically if we were appending (we are overwriting though)

                var json = JsonSerializer.Serialize(list);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception)
            {
                // Ignore errors
            }
        }

        public static async Task ClearAsync()
        {
            History.Clear();
            await SaveAsync();
        }
    }
}
