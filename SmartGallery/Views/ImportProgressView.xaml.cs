using Microsoft.UI.Xaml.Controls;
using SmartGallery.ViewModels;

namespace SmartGallery.Views;

public sealed partial class ImportProgressView : UserControl
{
    public ImportProgressViewModel ViewModel { get; } = new();

    public ImportProgressView()
    {
        InitializeComponent();
    }
}
