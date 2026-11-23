using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Infrastructure;
using SmartGallery.Infrastructure.Video;
using SmartGallery.Application.Importing;
using SmartGallery.Imaging;
using SmartGallery.AI.Models;
using SmartGallery.AI.Florence;
using SmartGallery.Application.AI;
using SmartGallery.Application.Catalog;
using SmartGallery.Worker;
using SmartGallery.Services;
using SmartGallery.ViewModels;

namespace SmartGallery
{
    public partial class App : Microsoft.UI.Xaml.Application
    {
        private Window? _window;
        private IHost? _host;
        private bool _isStopping;

        internal IServiceProvider Services =>
            _host?.Services ?? throw new InvalidOperationException("Host není inicializován.");

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                // No default configuration files, environment configuration or console logging.
                var builder = Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings { DisableDefaults = true });
                builder.Logging.AddDebug();
                builder.Services.AddSmartGalleryCatalog();
                builder.Services.AddSingleton<FfmpegTools>();
                builder.Services.AddSingleton<GalleryVideoService>();
                builder.Services.AddSingleton<IMediaDateReader, WindowsMediaDateReader>();
                builder.Services.AddSingleton<ICaptionImagePreprocessor, WindowsCaptionImagePreprocessor>();
                builder.Services.AddSingleton<IPhotoPixelFingerprintReader, WindowsPhotoPixelFingerprintReader>();
                builder.Services.AddSingleton<GalleryPhotoDuplicates>();
                builder.Services.AddSingleton<CaptionModelVerificationCache>();
                builder.Services.AddSingleton<ILocalCaptionModelInspector, LocalCaptionModelInspector>();
                builder.Services.AddSingleton<HttpClient>(_ => new HttpClient
                {
                    Timeout = System.Threading.Timeout.InfiniteTimeSpan
                });
                builder.Services.AddSingleton<ILocalModelProvisioner, LocalModelProvisioner>();
                builder.Services.AddSingleton<ICaptionRuntimeValidator, FlorenceCpuRuntimeValidator>();
                builder.Services.AddSingleton<IImageCaptionGenerator, FlorenceImageCaptionGenerator>();
                builder.Services.AddSingleton<GalleryPhotoAnalyzer>();
                builder.Services.AddSingleton<ModelPreparationWorker>();
                builder.Services.AddSingleton<ILocalModelPreparation>(
                    services => services.GetRequiredService<ModelPreparationWorker>());
                builder.Services.AddHostedService<ModelPreparationWorker>(
                    services => services.GetRequiredService<ModelPreparationWorker>());
                builder.Services.AddSingleton<AppReadinessState>();
                builder.Services.AddSingleton<ModelFooterViewModel>();
                builder.Services.AddSingleton<IFolderPicker>(
                    new WindowsFolderPicker(() => _window
                        ?? throw new InvalidOperationException("Hlavní okno není dostupné.")));
                builder.Services.AddSingleton<IImportDialogService>(services =>
                    new ImportDialogService(
                        () => _window ?? throw new InvalidOperationException("Hlavní okno není dostupné."),
                        services.GetRequiredService<IFolderImportService>(),
                        services.GetRequiredService<IImportPreviewService>(),
                        services.GetRequiredService<ILogger<ImportDialogService>>()));
                builder.Services.AddSingleton<IPhotoCaptionDialogService>(services =>
                    new PhotoCaptionDialogService(
                        () => _window ?? throw new InvalidOperationException("Hlavní okno není dostupné."),
                        services.GetRequiredService<IImageCaptionGenerator>(),
                        services.GetRequiredService<IMediaDateReader>(),
                        services.GetRequiredService<ILogger<PhotoCaptionDialogService>>()));
                builder.Services.AddTransient<GalleryViewModel>();
                _host = builder.Build();
                await _host.StartAsync();

                _window = new MainWindow();
                _window.AppWindow.Closing += OnWindowClosing;
                _window.Activate();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                await StopHostAsync();
                _window = new Window
                {
                    Title = "SmartGallery – chyba spuštění",
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = $"Aplikaci se nepodařilo inicializovat.\n\n{exception}",
                            IsTextSelectionEnabled = true,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(32)
                        }
                    }
                };
                _window.Activate();
            }
        }

        private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            args.Cancel = true;
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
            try
            {
                if (_window is MainWindow mainWindow)
                {
                    await mainWindow.CancelPendingOperationsAsync();
                }
                if (_host is not null)
                {
                    try
                    {
                        await _host.Services.GetRequiredService<IImportDialogService>().CancelAndWaitAsync();
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine(exception);
                    }
                    try
                    {
                        await _host.Services.GetRequiredService<IPhotoCaptionDialogService>().CancelAndWaitAsync();
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine(exception);
                    }
                }
                await StopHostAsync();
            }
            finally
            {
                sender.Closing -= OnWindowClosing;
                _window?.Close();
                _window = null;
            }
        }

        private async Task StopHostAsync()
        {
            var host = _host;
            if (host is null)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.StopAsync(timeout.Token);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
            finally
            {
                _host = null;
                try
                {
                    host.Dispose();
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                }
            }
        }
    }
}
