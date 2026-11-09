using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace HamulProto;

// Rueda overlay: transparente, "click-through" y sin activarse (no le quita el foco al juego).
// Solo se ven textos, líneas finas que se desvanecen y un punto en el centro.
public class WheelWindow : Window
{
    const int Slots = 8;
    const double WheelSize = 560;
    const double C = WheelSize / 2;
    const double Outer = 262, Inner = 40;
    const double LabelR = 168, LabelW = 124;

    static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x1F);

    readonly Canvas canvas = new() { Width = WheelSize, Height = WheelSize };
    readonly System.Windows.Shapes.Path[] wedges = new System.Windows.Shapes.Path[Slots];
    readonly TextBlock[] labels = new TextBlock[Slots];
    readonly Point[] labelPos = new Point[Slots];
    readonly string[] names = new string[Slots];
    int selected = -1;

    public WheelWindow(List<string> soundNames)
    {
        Width = WheelSize;
        Height = WheelSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Content = canvas;

        double step = 2 * Math.PI / Slots;

        // 1) Sombra suave detrás para que el texto se lea sobre cualquier juego
        double vr = Outer + 10;
        var vignette = new System.Windows.Shapes.Ellipse
        {
            Width = vr * 2,
            Height = vr * 2,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xA0, 0x00, 0x00, 0x00), 0.0),
                    new GradientStop(Color.FromArgb(0x70, 0x00, 0x00, 0x00), 0.6),
                    new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1.0)
                }
            }
        };
        Canvas.SetLeft(vignette, C - vr);
        Canvas.SetTop(vignette, C - vr);
        canvas.Children.Add(vignette);

        // 2) Zonas de cada sector (solo se iluminan al apuntar)
        for (int i = 0; i < Slots; i++)
        {
            double a0 = -Math.PI / 2 + i * step;
            double a1 = a0 + step;

            var fig = new PathFigure { StartPoint = Pt(a0, Outer), IsClosed = true };
            fig.Segments.Add(new ArcSegment(Pt(a1, Outer), new Size(Outer, Outer), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(Pt(a1, Inner), true));
            fig.Segments.Add(new ArcSegment(Pt(a0, Inner), new Size(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true));

            var wedge = new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { fig }),
                Fill = Brushes.Transparent
            };
            wedges[i] = wedge;
            canvas.Children.Add(wedge);
        }

        // 3) Líneas finas que se desvanecen entre sectores
        for (int i = 0; i < Slots; i++)
        {
            double a = -Math.PI / 2 + i * step;
            Point p0 = Pt(a, Inner + 4);
            Point p1 = Pt(a, Outer);

            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = p0,
                EndPoint = p1
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));

            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = p0.X, Y1 = p0.Y, X2 = p1.X, Y2 = p1.Y,
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = Glow(Orange, 8, 0.55)
            });
        }

        // 4) Textos
        for (int i = 0; i < Slots; i++)
        {
            double mid = -Math.PI / 2 + i * step + step / 2;
            labelPos[i] = Pt(mid, LabelR);

            var tb = new TextBlock
            {
                Width = LabelW,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed
            };
            labels[i] = tb;
            canvas.Children.Add(tb);
        }

        // 5) Punto central (donde empieza el mouse)
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = 32,
            Height = 32,
            Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(ring, C - 16);
        Canvas.SetTop(ring, C - 16);
        canvas.Children.Add(ring);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brushes.White,
            Effect = Glow(Orange, 10, 0.9)
        };
        Canvas.SetLeft(dot, C - 5);
        Canvas.SetTop(dot, C - 5);
        canvas.Children.Add(dot);

        SetNames(soundNames);

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(h, -20);
            Native.SetWindowLong(h, -20, ex | 0x80 | 0x20 | 0x08000000); // TOOLWINDOW | TRANSPARENT | NOACTIVATE
        };
    }

    static Point Pt(double a, double r) => new(C + r * Math.Cos(a), C + r * Math.Sin(a));

    static DropShadowEffect Glow(Color color, double blur, double opacity) =>
        new() { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };

    static string Short(string s) => s.Length > 28 ? s[..27] + "…" : s;

    // Actualiza los nombres de los 8 sectores ("" = sector vacío, no se dibuja)
    public void SetNames(IReadOnlyList<string> newNames)
    {
        for (int i = 0; i < Slots; i++)
            names[i] = i < newNames.Count ? newNames[i] ?? "" : "";

        for (int i = 0; i < Slots; i++) RefreshLabel(i);
    }

    void RefreshLabel(int i)
    {
        var tb = labels[i];
        string text = names[i];

        if (text.Length == 0)
        {
            tb.Visibility = Visibility.Collapsed;
            return;
        }

        bool sel = i == selected;
        tb.Visibility = Visibility.Visible;
        tb.Text = Short(text);
        tb.FontSize = sel ? 21 : 15;
        tb.FontWeight = sel ? FontWeights.Bold : FontWeights.SemiBold;
        tb.Foreground = sel ? new SolidColorBrush(Orange) : Brushes.White;
        tb.Effect = sel ? Glow(Orange, 16, 0.95) : Glow(Colors.Black, 7, 1.0);

        // Centrar verticalmente según el alto real del texto
        tb.Measure(new Size(LabelW, double.PositiveInfinity));
        Canvas.SetLeft(tb, labelPos[i].X - LabelW / 2);
        Canvas.SetTop(tb, labelPos[i].Y - tb.DesiredSize.Height / 2);
    }

    public void OpenAt(double centerX, double centerY)
    {
        Left = centerX - WheelSize / 2;
        Top = centerY - WheelSize / 2;
        Show();
    }

    public void SetSelected(int idx)
    {
        if (idx == selected) return;
        selected = idx;

        for (int i = 0; i < Slots; i++)
        {
            wedges[i].Fill = i == idx ? SelectedFill() : Brushes.Transparent;
            RefreshLabel(i);
        }
    }

    static Brush SelectedFill()
    {
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0x5A, 0x1F), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xE5, 0x25, 0x2A), 1));
        return b;
    }
}