using System.Windows;
using System.Windows.Controls;
using YtAuto.Desktop.ViewModels;

namespace YtAuto.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // PasswordBox bog'lanmaydi (xavfsizlik): qiymat faqat VM'ga uzatiladi va saqlangach tozalanadi.
    private void GeminiKey_Changed(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel vm) vm.GeminiKeyInput = GeminiKey.Password; }

    private void ClientSecret_Changed(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel vm) vm.ClientSecretInput = ClientSecret.Password; }

    private void PgConnection_Changed(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel vm) vm.PgConnectionInput = PgConnection.Password; }
}
