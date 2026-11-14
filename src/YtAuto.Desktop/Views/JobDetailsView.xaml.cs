using System.Windows;
using System.Windows.Controls;
using YtAuto.Desktop.ViewModels;

namespace YtAuto.Desktop.Views;

public partial class JobDetailsView : UserControl
{
    public JobDetailsView() => InitializeComponent();

    // Sahifadan chiqilganda media fayllar bo'shatiladi (fayl bloklanib qolmaydi).
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Player.Close(); AudioPlayer.Close();
        if (DataContext is JobDetailsViewModel vm) vm.StopMedia();
    }
}
