using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace HamulProto.UI.Pages;

public partial class SettingsPage : UserControl
{
    private bool _loadingSettings = true;

    public event EventHandler? ThemeChanged;
    public event EventHandler? SoundVolumeChanged;

    public SettingsPage()
    {
        InitializeComponent();

        SelectByTag(ThemeCombo, Config.Current.Theme, "Dark");
        SelectByTag(LanguageCombo, Config.Current.Language, "es");
        VolumeSlider.Value = Config.Current.SoundVolume;
        VolumeValue.Text = $"{Config.Current.SoundVolume}%";
        _loadingSettings = false;
    }

    private static void SelectByTag(ComboBox comboBox, string? value, string fallback)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (item.Tag as string == (value ?? fallback))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (item.Tag as string == fallback)
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme)
            return;

        Config.Current.Theme = theme;
        SaveSettings();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || LanguageCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string language)
            return;

        Config.Current.Language = language;
        SaveSettings();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int volume = (int)Math.Round(VolumeSlider.Value);
        if (VolumeValue != null)
            VolumeValue.Text = $"{volume}%";
        if (_loadingSettings)
            return;

        Config.Current.SoundVolume = volume;
        SaveSettings();
        SoundVolumeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SaveSettings()
    {
        try
        {
            Config.Current.Save();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"No se pudieron guardar los ajustes: {ex.Message}", "Hamul",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
