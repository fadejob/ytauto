using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YtAuto.Desktop.ViewModels;

namespace YtAuto.Desktop.Views;

/// <summary>
/// Pleyerlar LoadedBehavior=Manual: ijro shu yerdan boshqariladi (Play/Stop/Close faqat Manual rejimda ruxsat etiladi —
/// aks holda WPF "Cannot control media unless LoadedBehavior or UnloadedBehavior is set to Manual" xatosini beradi).
/// </summary>
public partial class JobDetailsView : UserControl
{
    private JobDetailsViewModel? _vm;

    public JobDetailsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as JobDetailsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(JobDetailsViewModel.VideoSource)) Apply(Player, _vm.VideoSource);
        else if (e.PropertyName == nameof(JobDetailsViewModel.AudioSource)) Apply(AudioPlayer, _vm.AudioSource);
    }

    private static void Apply(MediaElement player, Uri? source)
    {
        if (source is null)
        {
            player.Stop();
            player.Close();
            player.Source = null;
            return;
        }
        player.Source = source;
        player.Play();
    }

    // Sahifadan chiqilganda media fayllar bo'shatiladi (fayl bloklanib qolmaydi).
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Apply(Player, null); Apply(AudioPlayer, null);
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.StopMedia(); }
    }
}
