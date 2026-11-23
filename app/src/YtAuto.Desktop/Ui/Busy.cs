using System.Windows;

namespace YtAuto.Desktop.Ui;

/// <summary>
/// Tugma ichidagi spinner: <c>ui:Busy.IsBusy="{Binding Installing}"</c> - amal bajarilayotganda shu tugmaning o'zida aylanadi
/// (tugma bosilmaydi, boshqa joylar ishlayveradi). Stil: App.xaml dagi Button shabloni.
/// </summary>
public static class Busy
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
        "IsBusy", typeof(bool), typeof(Busy), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsBusy(DependencyObject o) => (bool)o.GetValue(IsBusyProperty);
    public static void SetIsBusy(DependencyObject o, bool v) => o.SetValue(IsBusyProperty, v);
}
