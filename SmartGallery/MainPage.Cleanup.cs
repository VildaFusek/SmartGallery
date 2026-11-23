using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SmartGallery.Application.Catalog;
using SmartGallery.Imaging;
using SmartGallery.ViewModels;

namespace SmartGallery;

public sealed partial class MainPage
{
    private AnalysisWindow? _analysisWindow;
    private CancellationTokenSource? _analysisCancellation;
    private Task? _analysisTask;
    private ContentDialog? _analysisReview;

    private void OnAnalysisWindowClick(object sender, RoutedEventArgs e)
    {
        if (_analysisWindow is { } existing) { existing.Activate(); return; }
        var window = _analysisWindow = new AnalysisWindow();
        if (window.Content is FrameworkElement root) root.RequestedTheme = ActualTheme;
        window.StartRequested += async selected =>
        {
            if (_analysisCancellation is not null || !ViewModel.CanChangeFilters)
            {
                window.Finish("Otevřete galerii a počkejte na dokončení rozpracované úlohy.");
                return;
            }
            var files = selected ? SelectedPhotos() : ViewModel.GalleryPhotos();
            if (files.Length == 0) { window.Finish("V tomto rozsahu nejsou žádné fotografie."); return; }
            using var request = new CancellationTokenSource();
            _analysisCancellation = request;
            window.Begin();
            try
            {
                _analysisTask = ViewModel.CleanupAsync(files, scan => ReviewCleanupAsync(scan, window, request.Token),
                    (label, file, total) => { if (_analysisWindow == window) window.Update(label, file, total); },
                    request.Token, (file, message) => { if (_analysisWindow == window) window.ShowFile(file, message); },
                    (file, message) => { if (_analysisWindow == window) window.ShowIssue(file, message); });
                await _analysisTask;
                if (_analysisWindow == window) window.Finish(ViewModel.StatusMessage);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("{0}", error);
                if (_analysisWindow == window) window.Finish("Kontrolu se nepodařilo dokončit. Obnovte galerii a zkuste to znovu.");
            }
            finally { _analysisTask = null; _analysisCancellation = null; }
        };
        window.StopRequested += CancelAnalysis;
        window.AppWindow.Closing += (_, _) => CancelAnalysis();
        window.PreviewRequested += file =>
        {
            var image = window.PreviewImage;
            if (_mediaRequests.Remove(image, out var pending)) pending.Cancel();
            image.Source = null;
            if (file is not null) _ = LoadFileVisualAsync(image, (media, ct) =>
                LoadThumbnailAsync(image, media, ct, fallbackToPhoto: true), file);
        };
        window.Closed += (_, _) =>
        {
            if (_mediaRequests.Remove(window.PreviewImage, out var pending)) pending.Cancel();
            _analysisWindow = null;
        };
        window.Activate();
    }

    private void CancelAnalysis()
    {
        _analysisCancellation?.Cancel();
        _analysisReview?.Hide();
    }

    private async Task<IReadOnlyList<CleanupChoice>?> ReviewCleanupAsync(CleanupScan scan, AnalysisWindow window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        window.Record($"Kontrola dokončena. Návrhy k posouzení: {scan.Rows.Count}.");
        var rows = scan.Rows.OrderByDescending(r => r.Exact).ThenByDescending(r => r.OfferTrash)
            .ThenBy(r => r.Keep?.RelativePath ?? r.File.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var actions = rows.Select(r => r.Exact || (r.Screenshot && !r.Protected) ? CleanupAction.Trash : CleanupAction.Keep).ToArray();
        var visible = Enumerable.Range(0, rows.Length).ToArray();
        var position = visible.ToArray();
        string[] categories = ["Všechny výsledky", "Snímky obrazovky", "Shodné kopie a obrazy", "Podobné fotografie", "Úspora místa"];
        var filter = new ComboBox { Header = "Zobrazit", HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var c = 0; c < categories.Length; c++)
        {
            int count = c == 0 ? rows.Length : rows.Count(r => Category(r) == c);
            if (count > 0) filter.Items.Add(new ComboBoxItem { Tag = c, Content = $"{categories[c]} ({count})" });
        }
        filter.SelectedIndex = 0;
        var labels = new System.Collections.ObjectModel.ObservableCollection<string>(rows.Select((r, i) => Label(i)));
        var list = new ListView { ItemsSource = labels, Height = 175, SelectionMode = ListViewSelectionMode.Single };
        var protect = new CheckBox { Content = "Chránit možné originály", IsChecked = true };
        var exact = new CheckBox { Content = "Přesunout všechny bajtově shodné kopie do koše", IsChecked = true,
            Visibility = ShowWhen(rows.Any(r => r.Exact)) };
        var optimize = new Button { Content = "Optimalizovat vhodné ponechané soubory",
            Visibility = ShowWhen(rows.Any(r => r.Format.HasValue)) };
        var screens = new CheckBox { Content = "Snímky obrazovky do koše", IsChecked = true,
            Visibility = ShowWhen(rows.Any(r => r.Screenshot)) };
        var reason = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var current = new Image { Height = 94 };
        var retained = new Image { Height = 94 };
        var previews = new Grid { ColumnSpacing = 6 };
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        previews.ColumnDefinitions.Add(new ColumnDefinition());
        AddPreview(current, 0, "Posuzovaný soubor");
        AddPreview(retained, 1, "Doporučeno ponechat");
        var action = new ComboBox { Header = "Akce pro posuzovaný soubor", HorizontalAlignment = HorizontalAlignment.Stretch };
        var bulk = new StackPanel { Spacing = 4 };
        foreach (var control in new UIElement[] { protect, exact, screens, optimize }) bulk.Children.Add(control);
        var options = new Expander { Header = "Hromadné volby pro všechny výsledky", Content = bulk, HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var control in new UIElement[] { summary, filter, list, previews, reason, action, options }) panel.Children.Add(control);
        var dialog = new ContentDialog { Title = "Výsledky kontroly galerie", Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Provést vybrané akce", CloseButtonText = "Zavřít bez změn", DefaultButton = ContentDialogButton.Close };
        var updating = false;
        int trashCount = 0, optimizeCount = 0;
        long trashBytes = 0;
        for (int i = 0; i < rows.Length; i++) Count(i, actions[i], 1);
        list.SelectionChanged += (_, _) => { if (!updating) ShowRow(); };
        filter.SelectionChanged += (_, _) =>
        {
            if (filter.SelectedItem is not ComboBoxItem { Tag: int category }) return;
            var previous = list.SelectedIndex >= 0 ? visible[list.SelectedIndex] : -1;
            updating = true;
            visible = Enumerable.Range(0, rows.Length).Where(i => category == 0 || Category(rows[i]) == category).ToArray();
            Array.Fill(position, -1);
            labels = new System.Collections.ObjectModel.ObservableCollection<string>(visible.Select(Label));
            for (int n = 0; n < visible.Length; n++)
                position[visible[n]] = n;
            list.ItemsSource = labels;
            list.SelectedIndex = previous >= 0 && position[previous] >= 0 ? position[previous] : 0;
            updating = false;
            ShowRow();
        };
        action.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedIndex < 0 || action.SelectedItem is not ComboBoxItem { Tag: int code }) return;
            var value = (CleanupAction)code;
            var index = list.SelectedIndex;
            var i = visible[index];
            updating = true;
            Set(i, value);
            list.SelectedIndex = index;
            updating = false;
            Summary();
        };
        exact.Click += (_, _) => Bulk(exact);
        screens.Click += (_, _) => Bulk(screens);
        protect.Click += (_, _) => Bulk(protect);
        optimize.Click += (_, _) =>
        {
            var index = list.SelectedIndex;
            updating = true;
            for (var i = 0; i < rows.Length; i++)
                if (actions[i] == CleanupAction.Keep && rows[i].Format.HasValue && (protect.IsChecked != true || !rows[i].Protected))
                    Set(i, CleanupAction.Optimize);
            list.SelectedIndex = Math.Max(index, 0);
            updating = false;
            ShowRow();
            Summary();
        };
        list.SelectedIndex = 0;
        Summary();
        try
        {
            dialog.XamlRoot = window.Content.XamlRoot;
            _analysisReview = dialog;
            return await dialog.ShowAsync() == ContentDialogResult.Primary && !token.IsCancellationRequested
                ? rows.Select((r, i) => new CleanupChoice(r, actions[i])).ToArray() : null;
        }
        finally { _analysisReview = null; Clear(current); Clear(retained); }

        static int Category(CleanupSuggestion r) => r.Screenshot ? 1 : r.Exact || r.Pixels is not null ? 2 : r.Keep is not null ? 3 : 4;
        string Label(int i)
        {
            var row = rows[i];
            var category = row.Screenshot ? "Screenshot" : row.Exact ? "Shodná kopie" : row.Pixels is not null ? "Shodný obraz" : row.Keep is not null ? "Podobný snímek" :
                row.OfferTrash ? "Možný screenshot" : "Možná úspora";
            return $"{ActionName(actions[i])}   ·   {row.File.Name}   ·   {category}";
        }
        static string ActionName(CleanupAction a) => a switch
        {
            CleanupAction.Trash => "Do koše", CleanupAction.Optimize => "Optimalizovat", _ => "Ponechat"
        };
        void AddPreview(Image image, int column, string title)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = title });
            box.Children.Add(image);
            Grid.SetColumn(box, column);
            previews.Children.Add(box);
        }
        void Clear(Image image)
        {
            if (_mediaRequests.Remove(image, out var request)) request.Cancel();
            image.Source = null;
        }
        void Preview(Image image, GalleryFile? file)
        {
            Clear(image);
            if (file is not null) _ = LoadFileVisualAsync(image, (media, ct) => LoadThumbnailAsync(image, media, ct, fallbackToPhoto: true), file);
        }
        void Count(int i, CleanupAction a, int sign)
        {
            if (a == CleanupAction.Trash) { trashCount += sign; trashBytes += sign * rows[i].File.SizeBytes; }
            if (a == CleanupAction.Optimize) optimizeCount += sign;
        }
        void Set(int i, CleanupAction value)
        {
            if (actions[i] == value) return;
            Count(i, actions[i], -1);
            actions[i] = value;
            Count(i, value, 1);
            if (position[i] >= 0) labels[position[i]] = Label(i);
        }
        void Bulk(CheckBox source)
        {
            var index = list.SelectedIndex;
            updating = true;
            for (int i = 0; i < rows.Length; i++)
            {
                if (source == exact && rows[i].Exact) Set(i, exact.IsChecked == true ? CleanupAction.Trash : CleanupAction.Keep);
                if (source == screens && rows[i].Screenshot && (protect.IsChecked != true || !rows[i].Protected))
                    Set(i, screens.IsChecked == true ? CleanupAction.Trash : CleanupAction.Keep);
                if (source == protect && protect.IsChecked == true && rows[i].Protected) Set(i, CleanupAction.Keep);
            }
            list.SelectedIndex = Math.Max(0, index);
            updating = false;
            ShowRow();
            Summary();
        }
        void ShowRow()
        {
            if (list.SelectedIndex < 0) return;
            int i = visible[list.SelectedIndex];
            var r = rows[i];
            var locked = r.Protected && protect.IsChecked == true;
            reason.Text = $"{r.File.RelativePath}\n{r.Reason}" + (r.Keep is null ? "" : $"\nPonechat: {r.Keep.RelativePath}") +
                (locked ? "\nPůvod není prokazatelný nebo soubor pochází z fotoaparátu. Ochrana ho ponechá." : "") +
                (r.Format is null ? "" : $"\nOdhad: vyzkoušet {r.Format}. Nahradí se pouze menším výsledkem se shodnými pixely a profilem; EXIF se nepřenese.");
            Preview(current, r.File);
            Preview(retained, r.Keep);
            previews.Children[1].Visibility = ShowWhen(r.Keep is not null);
            updating = true;
            action.Items.Clear();
            foreach (var a in Enum.GetValues<CleanupAction>())
            {
                if (a != CleanupAction.Keep && (locked || (a == CleanupAction.Trash ? !r.OfferTrash : !r.Format.HasValue))) continue;
                var item = new ComboBoxItem { Content = ActionName(a), Tag = (int)a };
                action.Items.Add(item);
                if (a == actions[i]) action.SelectedItem = item;
            }
            updating = false;
        }
        void Summary()
        {
            exact.IsChecked = !rows.Where((r, i) => r.Exact && actions[i] != CleanupAction.Trash).Any();
            var eligibleScreens = rows.Select((r, i) => (r, i)).Where(x => x.r.Screenshot && (!x.r.Protected || protect.IsChecked != true));
            screens.IsEnabled = eligibleScreens.Any();
            screens.IsChecked = screens.IsEnabled && eligibleScreens.All(x => actions[x.i] == CleanupAction.Trash);
            optimize.IsEnabled = rows.Where((r, i) => r.Format.HasValue && actions[i] == CleanupAction.Keep &&
                (!r.Protected || protect.IsChecked != true)).Any();
            summary.Text = $"Celý výběr:   Do koše {trashCount}   ·   Ponechat {rows.Length - trashCount - optimizeCount}   ·   Optimalizovat {optimizeCount}" +
                $"\nDo koše se po potvrzení přesune {trashBytes:N0} B. Místo se uvolní až jeho vysypáním." +
                (scan.Skipped > 0 ? $" Přeskočeno při kontrole: {scan.Skipped}." : "");
            dialog.IsPrimaryButtonEnabled = trashCount + optimizeCount > 0;
        }
    }
}
