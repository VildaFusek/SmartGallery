using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartGallery.Services;

public sealed class AppReadinessState : ObservableObject
{
    private string _catalog = "Katalog: čeká na připojení";
    private string _import = "Import: čeká na knihovnu";
    private bool _captioningReady;

    public bool CaptioningReady
    {
        get => _captioningReady;
        set => SetProperty(ref _captioningReady, value);
    }

    public string Catalog
    {
        get => _catalog;
        set
        {
            if (SetProperty(ref _catalog, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Import
    {
        get => _import;
        set
        {
            if (SetProperty(ref _import, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Summary => $"{Catalog} · {Import}";
}
