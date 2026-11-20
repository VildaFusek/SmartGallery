using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using SmartGallery.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SmartGallery
{
    /// <summary>
    /// The application window. This hosts a Frame that displays pages. Add your
    /// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
    /// can use Page features such as navigation events and the Loaded lifecycle.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public ModelFooterViewModel FooterViewModel { get; }

        internal Task CancelPendingOperationsAsync()
        {
            if (RootFrame.Content is MainPage page)
            {
                page.CancelPendingOperations();
                return page.PendingGalleryChanges;
            }
            return Task.CompletedTask;
        }

        public MainWindow()
        {
            FooterViewModel = ((App)Microsoft.UI.Xaml.Application.Current).Services
                .GetRequiredService<ModelFooterViewModel>();
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            // Navigate the root frame to the main page on startup.
            RootFrame.Navigate(typeof(MainPage));
        }
    }
}
