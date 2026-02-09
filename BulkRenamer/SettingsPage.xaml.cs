using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Globalization;
using System;
using System.Linq;

namespace BulkRenamer
{
    public sealed partial class SettingsPage : Page
    {
        private bool _isInitialized;
        private readonly ResourceLoader _resourceLoader = new();

        public SettingsPage()
        {
            this.InitializeComponent();
            LoadCurrentLanguage();
        }

        private void LoadCurrentLanguage()
        {
            var currentLang = ApplicationLanguages.PrimaryLanguageOverride;
            
            // If override is not set, try to match the first language in the system list that we support
            if (string.IsNullOrEmpty(currentLang))
            {
                 // Default to checking system pref vs our items
                 // But for simplicity in UI, if not overridden, we might show 'System Default' or just pick one if it matches.
                 // For now, let's just default to English if nothing matches.
                 currentLang = "en-US"; 
                 
                 // Try to see if system language matches "fr"
                 var systemLang = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault();
                 if (systemLang != null && systemLang.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
                 {
                     currentLang = "fr-FR";
                 }
            }

            foreach (ComboBoxItem item in LanguageComboBox.Items)
            {
                if (item.Tag.ToString() == currentLang)
                {
                    LanguageComboBox.SelectedItem = item;
                    break;
                }
            }

            if (LanguageComboBox.SelectedItem == null)
            {
                LanguageComboBox.SelectedIndex = 0;
            }
            
            _isInitialized = true;
        }

        private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;

            if (LanguageComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                string newLang = selectedItem.Tag.ToString();
                
                // Only proceed if it is different from current override (or system if override is empty)
                string currentOverride = ApplicationLanguages.PrimaryLanguageOverride;
                if (newLang == currentOverride) return;

                // Show confirmation dialog
                ContentDialog dialog = new ContentDialog();
                // Ensure XamlRoot is set for WinUI 3
                if (this.XamlRoot != null)
                {
                    dialog.XamlRoot = this.XamlRoot;
                }
                
                dialog.Title = _resourceLoader.GetString("RestartDialogTitle");
                dialog.Content = _resourceLoader.GetString("RestartDialogContent");
                dialog.PrimaryButtonText = _resourceLoader.GetString("RestartDialogPrimary");
                dialog.CloseButtonText = _resourceLoader.GetString("RestartDialogClose");
                dialog.DefaultButton = ContentDialogButton.Primary;

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                     ApplicationLanguages.PrimaryLanguageOverride = newLang;
                     AppInstance.Restart("");
                }
                else
                {
                    // Revert selection without triggering logic again
                    _isInitialized = false;
                    LoadCurrentLanguage(); 
                    _isInitialized = true;
                }
            }
        }
    }
}
