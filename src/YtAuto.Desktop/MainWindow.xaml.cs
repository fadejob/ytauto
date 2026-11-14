using System.Windows;
using System.Windows.Controls;
using YtAuto.Desktop.ViewModels;

namespace YtAuto.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    // Checked hodisasi sichqoncha ham, klaviatura (strelkalar) ham uchun ishlaydi.
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is RadioButton { Tag: string page })
            vm.NavigateCommand.Execute(page);
    }
}
