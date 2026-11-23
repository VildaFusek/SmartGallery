using Microsoft.UI.Xaml;
using SmartGallery.Application.Catalog;

namespace SmartGallery;

public sealed partial class AnalysisWindow : Window
{
    internal event Action<bool>? StartRequested;
    internal event Action? StopRequested;
    internal event Action<GalleryFile?>? PreviewRequested;
    internal Microsoft.UI.Xaml.Controls.Image PreviewImage => Thumbnail;
    private Guid? _fileId;
    private string? _lastEvent;
    private int _issueCount;

    public AnalysisWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(750, 720));
        Start.Click += (_, _) => StartRequested?.Invoke(Scope.SelectedIndex == 1);
        Stop.Click += (_, _) => { Stop.IsEnabled = false; StopRequested?.Invoke(); };
    }

    internal void Begin()
    {
        Scope.IsEnabled = Start.IsEnabled = false;
        Stop.IsEnabled = true;
        _lastEvent = null;
        History.Items.Clear();
        Issues.Items.Clear();
        IssuePanel.Visibility = Visibility.Collapsed;
        _issueCount = 0;
        Details.Text = "";
        _fileId = null;
        PreviewRequested?.Invoke(null);
        Update("Příprava kontroly…", 0, 0);
    }

    internal void Update(string stage, int file, int total)
    {
        Phase.Text = stage;
        FileLabel.Text = file < 0 ? "Probíhá kontrola souborů…" : $"Aktuální krok: {file} %";
        FileBar.IsIndeterminate = file < 0;
        FileBar.Value = Math.Max(0, file);
        TotalLabel.Text = $"Celá úloha: {total} %";
        TotalBar.Value = total;
    }

    internal void ShowFile(GalleryFile file, string message)
    {
        Details.Text = $"{file.RelativePath}\n{file.SizeBytes:N0} B · {file.LastWriteTimeUtc.LocalDateTime:g}\n{message}";
        if (_fileId != file.Id)
        {
            _fileId = file.Id;
            PreviewRequested?.Invoke(file);
        }
        Record($"{file.Name}: {message}");
    }

    internal void Record(string message)
    {
        if (_lastEvent == message) return;
        _lastEvent = message;
        History.Items.Insert(0, $"{DateTime.Now:T}  {message}");
        if (History.Items.Count > 80) History.Items.RemoveAt(History.Items.Count - 1);
    }

    internal void ShowIssue(GalleryFile file, string message)
    {
        ShowFile(file, message);
        IssuePanel.Header = $"Potíže při kontrole: {++_issueCount} (posledních 100)";
        IssuePanel.Visibility = Visibility.Visible;
        Issues.Items.Insert(0, new Microsoft.UI.Xaml.Controls.TextBlock
            { Text = $"{file.RelativePath}\n{message}", TextWrapping = TextWrapping.Wrap });
        if (Issues.Items.Count > 100) Issues.Items.RemoveAt(Issues.Items.Count - 1);
    }

    internal void Finish(string message)
    {
        FileBar.IsIndeterminate = false;
        Phase.Text = message;
        Record(message);
        Stop.IsEnabled = false;
        Start.IsEnabled = Scope.IsEnabled = true;
    }
}
