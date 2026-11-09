using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NAudio.Wave;

// --- ALIAS PARA EVITAR CONFLICTOS CON WINDOWS FORMS ---
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Cursors = System.Windows.Input.Cursors;
// --------------------------------------------------------

namespace HamulProto;

public class SoundItem
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string Date { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string PlayIcon { get; set; } = "▶";

    public string CategoryLabel => string.IsNullOrEmpty(Category) ? "—" : Category;
}

public partial class DashboardWindow : Window
{
    // ---------- Geometría de la rueda ----------
    private const int Slots = 8;
    private const double Center = 250;
    private const double OuterR = 225;
    private const double InnerR = 72;

    private readonly string soundsDir;
    private readonly string[] _shortcuts = {
        "Ctrl+Shift+F1", "Ctrl+Shift+F2", "Ctrl+Shift+F3", "Ctrl+Shift+F4",
        "Ctrl+Shift+F5", "Ctrl+Shift+F6", "Ctrl+Shift+F7", "Ctrl+Shift+F8"
    };

    // Qué archivo está asignado a cada sector (se guarda en slots.json)
    private readonly string?[] _slotFiles = new string?[Slots];
    private string SlotsPath => Path.Combine(Config.Dir, "slots.json");

    // Categoría de cada sonido por nombre de archivo (se guarda en categories.json)
    private Dictionary<string, string> _categoryMap = new();
    private string CategoriesPath => Path.Combine(Config.Dir, "categories.json");
    private static readonly string[] Categories = { "Saludo", "GG", "Burla", "Épico", "Chill", "Ayuda", "Otros" };
    private string _activeCategory = "Todos";

    private List<SoundItem> _allItems = new();
    private readonly System.Windows.Shapes.Path?[] _sectorPaths = new System.Windows.Shapes.Path?[Slots];

    private IWavePlayer? _currentOutput;
    private AudioFileReader? _currentPreviewReader;
    private SoundItem? _currentPlayingItem;
    private Point _dragStartPoint;

    private int _hoverSlot = -1;   // sector bajo el mouse
    private int _dropSlot = -1;    // sector bajo el mouse mientras arrastras
    private int _playingSlot = -1; // sector que está sonando

    public DashboardWindow()
    {
        InitializeComponent();
        SettingsPage.ThemeChanged += (_, _) => ApplySavedTheme();
        SettingsPage.SoundVolumeChanged += (_, _) =>
        {
            if (_currentPreviewReader != null)
                _currentPreviewReader.Volume = Config.Current.SoundVolume / 100f;
        };
        ApplySavedTheme();

        // Que al maximizar no tape la barra de tareas
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;

        soundsDir = Path.Combine(Config.Dir, "sounds");
        Directory.CreateDirectory(soundsDir);

        LoadSlots();
        LoadCategories();
        BuildCategoryChips();
        LoadSounds();
    }

    // ==========================================
    // TEMA
    // ==========================================
    private void ApplySavedTheme()
    {
        ResourceDictionary newTheme;
        try
        {
            newTheme = LoadThemeDictionary(Config.Current.Theme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"No se pudo cargar el tema guardado; se usará Dark. {ex}");
            newTheme = LoadThemeDictionary("Dark");
        }

        var dictionaries = Resources.MergedDictionaries;
        var oldTheme = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);

        dictionaries.Add(newTheme);
        if (oldTheme != null)
            dictionaries.Remove(oldTheme);
    }

    private static ResourceDictionary LoadThemeDictionary(string? theme)
    {
        var themeName = theme?.Trim();
        if (themeName is not ("Dark" or "Light" or "Fire"))
            themeName = "Dark";

        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Hamul;component/Themes/{themeName}Theme.xaml")
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCurrent();
        base.OnClosed(e);
    }

    // ==========================================
    // VENTANA (barra de título propia)
    // ==========================================
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        StopCurrent();
        Close();
    }

    // ==========================================
    // GUARDAR / CARGAR SECTORES Y CATEGORÍAS
    // ==========================================
    private void LoadSlots()
    {
        try
        {
            if (!File.Exists(SlotsPath)) return;
            var saved = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(SlotsPath));
            if (saved == null) return;
            for (int i = 0; i < Slots && i < saved.Length; i++) _slotFiles[i] = saved[i];
        }
        catch { }
    }

    private void SaveSlots()
    {
        try { File.WriteAllText(SlotsPath, JsonSerializer.Serialize(_slotFiles)); }
        catch (Exception ex) { MessageBox.Show("Error al guardar los sectores: " + ex.Message); }
    }

    private void LoadCategories()
    {
        try
        {
            if (!File.Exists(CategoriesPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CategoriesPath));
            if (saved != null) _categoryMap = saved;
        }
        catch { }
    }

    private void SaveCategories()
    {
        try { File.WriteAllText(CategoriesPath, JsonSerializer.Serialize(_categoryMap)); }
        catch (Exception ex) { MessageBox.Show("Error al guardar las categorías: " + ex.Message); }
    }

    // ==========================================
    // CARGAR LISTA DE SONIDOS
    // ==========================================
    private void LoadSounds()
    {
        // Limpiar sectores cuyo archivo ya no existe
        for (int i = 0; i < Slots; i++)
            if (_slotFiles[i] != null && !File.Exists(_slotFiles[i])) _slotFiles[i] = null;

        var files = Directory.GetFiles(soundsDir)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _allItems = files.Select(f =>
        {
            int slot = Array.IndexOf(_slotFiles, f);
            return new SoundItem
            {
                Name = Path.GetFileNameWithoutExtension(f),
                Category = _categoryMap.TryGetValue(Path.GetFileName(f), out var c) ? c : "",
                Shortcut = slot >= 0 ? _shortcuts[slot] : "Libre",
                FilePath = f,
                Date = File.GetLastWriteTime(f).ToString("dd/MM/yyyy")
            };
        }).ToList();

        // Si algo está sonando, mantener su icono de pausa
        if (_currentPlayingItem != null)
        {
            var again = _allItems.FirstOrDefault(x => x.FilePath == _currentPlayingItem.FilePath);
            if (again != null) { again.PlayIcon = "⏸"; _currentPlayingItem = again; }
        }

        SoundsList.ItemsSource = _allItems;
        ApplyFilter();
        DrawWheel();
    }

    // ==========================================
    // BUSCADOR Y CATEGORÍAS
    // ==========================================
    private void ApplyFilter()
    {
        string q = SearchBox?.Text.Trim() ?? "";

        var filtered = _allItems
            .Where(i => _activeCategory == "Todos" || i.Category == _activeCategory)
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AllSoundsList.ItemsSource = filtered;
        EmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_allItems == null) return;
        ApplyFilter();
    }

    private void BuildCategoryChips()
    {
        CategoryPanel.Children.Clear();

        foreach (var name in new[] { "Todos" }.Concat(Categories))
        {
            string cat = name;
            var chip = new Button
            {
                Content = cat,
                Style = (Style)FindResource("ChipButton"),
                Tag = cat == _activeCategory ? "active" : null
            };
            chip.Click += (_, _) =>
            {
                _activeCategory = cat;
                BuildCategoryChips();
                ApplyFilter();
            };
            CategoryPanel.Children.Add(chip);
        }
    }

    // Clic derecho en un sonido de "My Sounds": elegir categoría
    private void AllSoundsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container == null ||
            AllSoundsList.ItemContainerGenerator.ItemFromContainer(container) is not SoundItem sound) return;

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu"), PlacementTarget = container };

        foreach (var name in Categories)
        {
            string cat = name;
            var mi = new MenuItem { Header = "Categoría: " + cat, Style = (Style)FindResource("DarkMenuItem") };
            mi.Click += (_, _) => SetCategory(sound, cat);
            menu.Items.Add(mi);
        }

        var clear = new MenuItem { Header = "Quitar categoría", Style = (Style)FindResource("DarkMenuItem") };
        clear.Click += (_, _) => SetCategory(sound, null);
        menu.Items.Add(clear);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SetCategory(SoundItem sound, string? category)
    {
        string key = Path.GetFileName(sound.FilePath);
        if (string.IsNullOrEmpty(category)) _categoryMap.Remove(key);
        else _categoryMap[key] = category;

        SaveCategories();
        LoadSounds();
    }

    // ==========================================
    // DIBUJAR LA RUEDA (estilo neón, sin relleno)
    // ==========================================
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x7A, 0x1A);
    private static readonly Color Red = Color.FromRgb(0xE5, 0x25, 0x2A);

    private static DropShadowEffect Glow(Color color, double blur, double opacity) =>
        new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };

    private void AddRing(double radius, Color color, double thickness)
    {
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(ring, Center - radius);
        Canvas.SetTop(ring, Center - radius);
        WheelCanvas.Children.Add(ring);
    }

    private void DrawWheel()
    {
        WheelCanvas.Children.Clear();
        Array.Clear(_sectorPaths, 0, _sectorPaths.Length);

        double step = 2 * Math.PI / Slots;

        // Anillos decorativos
        AddRing(OuterR + 12, Color.FromArgb(0x38, 0xFF, 0x7A, 0x1A), 1);
        AddRing(OuterR + 22, Color.FromArgb(0x16, 0xE5, 0x25, 0x2A), 1);

        // 1) Sectores (invisibles hasta pasar el mouse / arrastrar / sonar)
        for (int i = 0; i < Slots; i++)
        {
            int index = i; // copia local para el clic
            double a0 = -Math.PI / 2 + i * step;
            double a1 = a0 + step;

            var path = new System.Windows.Shapes.Path
            {
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = i
            };

            path.MouseEnter += (_, _) => { _hoverSlot = index; UpdateSectorFills(); };
            path.MouseLeave += (_, _) => { if (_hoverSlot == index) _hoverSlot = -1; UpdateSectorFills(); };
            path.MouseLeftButtonDown += (_, _) => PlaySlot(index);   // clic: escuchar
            path.MouseRightButtonDown += (_, _) =>                    // clic derecho: vaciar
            {
                _slotFiles[index] = null;
                SaveSlots();
                LoadSounds();
            };

            var figure = new PathFigure { StartPoint = GetPoint(a0, OuterR), IsClosed = true };
            figure.Segments.Add(new ArcSegment(GetPoint(a1, OuterR), new Size(OuterR, OuterR), 0, false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(GetPoint(a1, InnerR), true));
            figure.Segments.Add(new ArcSegment(GetPoint(a0, InnerR), new Size(InnerR, InnerR), 0, false, SweepDirection.Counterclockwise, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            path.Data = geometry;

            _sectorPaths[i] = path;
            WheelCanvas.Children.Add(path);
        }

        // 2) Líneas divisorias con degradado y brillo
        for (int i = 0; i < Slots; i++)
        {
            double a = -Math.PI / 2 + i * step;
            Point p0 = GetPoint(a, InnerR + 8);
            Point p1 = GetPoint(a, OuterR + 4);

            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = p0,
                EndPoint = p1
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xE5, 0x25, 0x2A), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0x8A, 0x1F), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xD0, 0x9A), 1.0));

            var line = new System.Windows.Shapes.Line
            {
                X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                Stroke = brush,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = Glow(Orange, 10, 0.8),
                IsHitTestVisible = false
            };
            WheelCanvas.Children.Add(line);
        }

        // 3) Textos
        for (int i = 0; i < Slots; i++)
        {
            string? file = _slotFiles[i];
            bool filled = file != null;

            string name = filled ? Path.GetFileNameWithoutExtension(file!) : "Vacío";
            if (name.Length > 32) name = name[..31] + "…";

            var panel = new StackPanel { Width = 120, IsHitTestVisible = false };

            panel.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = filled ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                FontSize = filled ? 15 : 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Effect = filled ? Glow(Colors.Black, 6, 0.9) : null
            });

            panel.Children.Add(new TextBlock
            {
                Text = _shortcuts[i],
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(filled ? Orange : Color.FromArgb(0x60, 0x8E, 0x8E, 0x9A))
            });

            double mid = -Math.PI / 2 + i * step + step / 2;
            Point pt = GetPoint(mid, (InnerR + OuterR) / 2);

            panel.Measure(new Size(120, double.PositiveInfinity));
            Canvas.SetLeft(panel, pt.X - 60);
            Canvas.SetTop(panel, pt.Y - panel.DesiredSize.Height / 2);
            WheelCanvas.Children.Add(panel);
        }

        // 4) Centro
        double hubR = InnerR - 8;
        var hub = new System.Windows.Shapes.Ellipse
        {
            Width = hubR * 2,
            Height = hubR * 2,
            Fill = new RadialGradientBrush(Color.FromRgb(0x08, 0x08, 0x0B), Color.FromRgb(0x3A, 0x0D, 0x10)),
            Stroke = new SolidColorBrush(Orange),
            StrokeThickness = 2,
            Effect = Glow(Red, 22, 0.8),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(hub, Center - hubR);
        Canvas.SetTop(hub, Center - hubR);
        WheelCanvas.Children.Add(hub);

        var note = new TextBlock
        {
            Text = "♪",
            FontSize = 32,
            Foreground = new SolidColorBrush(Orange),
            Effect = Glow(Orange, 12, 0.9),
            IsHitTestVisible = false
        };
        note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(note, Center - note.DesiredSize.Width / 2);
        Canvas.SetTop(note, Center - note.DesiredSize.Height / 2);
        WheelCanvas.Children.Add(note);

        UpdateSectorFills();
    }

    private static Point GetPoint(double angle, double radius) =>
        new Point(Center + radius * Math.Cos(angle), Center + radius * Math.Sin(angle));

    private static Brush SectorFill(bool hover, bool playing)
    {
        if (!hover && !playing) return Brushes.Transparent;

        byte alpha = (byte)(playing ? 0x95 : 0x55);
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0xFF, 0x5A, 0x1F), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xE5, 0x25, 0x2A), 1));
        return brush;
    }

    private void UpdateSectorFills()
    {
        for (int i = 0; i < Slots; i++)
        {
            var p = _sectorPaths[i];
            if (p == null) continue;
            p.Fill = SectorFill(i == _hoverSlot || i == _dropSlot, i == _playingSlot);
        }
    }

    // Devuelve el sector bajo un punto del canvas, o -1 si está fuera del anillo
    private static int GetSlotAt(Point p)
    {
        double dx = p.X - Center;
        double dy = p.Y - Center;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < InnerR || dist > OuterR) return -1;

        double ang = Math.Atan2(dy, dx) + Math.PI / 2;
        if (ang < 0) ang += 2 * Math.PI;
        return (int)(ang / (2 * Math.PI / Slots)) % Slots;
    }

    // ==========================================
    // DRAG & DROP
    // ==========================================
    private void AllSoundsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void AllSoundsList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        Vector diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance)
            return;

        if (sender is not ListBox listBox) return;

        // No iniciar arrastre desde los botones (play / eliminar)
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;

        if (listBox.ItemContainerGenerator.ItemFromContainer(item) is SoundItem soundItem)
        {
            var dragData = new DataObject(DataFormats.Text, soundItem.FilePath);
            DragDrop.DoDragDrop(item, dragData, DragDropEffects.Copy);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void WheelCanvas_DragOver(object sender, DragEventArgs e)
    {
        int slot = GetSlotAt(e.GetPosition(WheelCanvas));
        e.Effects = slot >= 0 ? DragDropEffects.Copy : DragDropEffects.None;

        if (slot != _dropSlot)
        {
            _dropSlot = slot;
            UpdateSectorFills();
        }
        e.Handled = true;
    }

    private void WheelCanvas_DragLeave(object sender, DragEventArgs e)
    {
        _dropSlot = -1;
        UpdateSectorFills();
    }

    private void WheelCanvas_Drop(object sender, DragEventArgs e)
    {
        _dropSlot = -1;

        if (!e.Data.GetDataPresent(DataFormats.Text))
        {
            UpdateSectorFills();
            return;
        }

        string filePath = (string)e.Data.GetData(DataFormats.Text);
        int slot = GetSlotAt(e.GetPosition(WheelCanvas));

        if (slot < 0)
        {
            UpdateSectorFills();
            return;
        }

        AssignFileToSlot(filePath, slot);
    }

    private void AssignFileToSlot(string filePath, int slotIndex)
    {
        // Un mismo sonido no puede estar en dos sectores
        for (int i = 0; i < Slots; i++)
            if (_slotFiles[i] == filePath) _slotFiles[i] = null;

        _slotFiles[slotIndex] = filePath;
        SaveSlots();
        LoadSounds();
    }

    // ==========================================
    // REPRODUCIR Y ELIMINAR
    // ==========================================
    private void PlayFile(string path, SoundItem? item = null, int slot = -1)
    {
        StopCurrent();

        try
        {
            var reader = new AudioFileReader(path);
            reader.Volume = Config.Current.SoundVolume / 100f;
            _currentPreviewReader = reader;
            var output = new WaveOutEvent();
            output.Init(reader);
            _currentOutput = output;

            output.PlaybackStopped += (_, _) =>
            {
                bool naturalEnd = _currentOutput == output; // false si lo detuvo StopCurrent
                output.Dispose();
                reader.Dispose();
                if (naturalEnd)
                {
                    _currentOutput = null;
                    _currentPreviewReader = null;
                    ResetPlaybackState();
                }
            };

            output.Play();

            _currentPlayingItem = item;
            if (item != null) item.PlayIcon = "⏸";
            _playingSlot = slot;
            UpdateSectorFills();
            if (item != null) RefreshList();
        }
        catch (Exception ex)
        {
            _currentOutput = null;
            MessageBox.Show("Error al reproducir: " + ex.Message);
        }
    }

    private void StopCurrent()
    {
        var output = _currentOutput;
        _currentOutput = null;
        _currentPreviewReader = null;
        output?.Stop();
        ResetPlaybackState();
    }

    private void ResetPlaybackState()
    {
        bool listChanged = false;

        if (_currentPlayingItem != null)
        {
            _currentPlayingItem.PlayIcon = "▶";
            _currentPlayingItem = null;
            listChanged = true;
        }

        if (_playingSlot != -1)
        {
            _playingSlot = -1;
            UpdateSectorFills();
        }

        if (listChanged) RefreshList();
    }

    private void RefreshList()
    {
        SoundsList.ItemsSource = null;
        SoundsList.ItemsSource = _allItems;
        ApplyFilter();
    }

    private void PlaySlot(int index)
    {
        if (_playingSlot == index) { StopCurrent(); return; }

        string? file = _slotFiles[index];
        if (file == null || !File.Exists(file)) return;

        PlayFile(file, null, index);
    }

    private void PlayPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundItem item)
            PlayFile(item.FilePath);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundItem item)
        {
            if (_currentPlayingItem?.FilePath == item.FilePath)
            {
                StopCurrent();
                return;
            }
            PlayFile(item.FilePath, item);
        }
    }

    private void DeleteSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SoundItem itemToDelete)
        {
            var result = MessageBox.Show($"¿Seguro que quieres eliminar '{itemToDelete.Name}'?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                StopCurrent();
                File.Delete(itemToDelete.FilePath);

                for (int i = 0; i < Slots; i++)
                    if (_slotFiles[i] == itemToDelete.FilePath) _slotFiles[i] = null;
                SaveSlots();

                if (_categoryMap.Remove(Path.GetFileName(itemToDelete.FilePath))) SaveCategories();

                LoadSounds();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al eliminar: " + ex.Message);
            }
        }
    }

    private void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Archivos de Audio|*.mp3;*.wav", Multiselect = true };
        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                try { File.Copy(file, Path.Combine(soundsDir, Path.GetFileName(file)), overwrite: true); } catch { }
            }
            LoadSounds();
        }
    }

    // ==========================================
    // NAVEGACIÓN
    // ==========================================
    private void ShowSoundWheel_Click(object sender, RoutedEventArgs e)
    {
        SoundWheelView.Visibility = Visibility.Visible;
        MySoundsView.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        BtnSoundWheel.Tag = "active";
        BtnMySounds.Tag = null;
        BtnSettings.Tag = null;
    }

    private void ShowMySounds_Click(object sender, RoutedEventArgs e)
    {
        SoundWheelView.Visibility = Visibility.Collapsed;
        MySoundsView.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;

        BtnMySounds.Tag = "active";
        BtnSoundWheel.Tag = null;
        BtnSettings.Tag = null;
    }

    private void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        SoundWheelView.Visibility = Visibility.Collapsed;
        MySoundsView.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;

        BtnSettings.Tag = "active";
        BtnSoundWheel.Tag = null;
        BtnMySounds.Tag = null;

    }
}