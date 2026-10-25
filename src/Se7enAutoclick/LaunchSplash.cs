using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Se7enAutoclick;

/// <summary>
/// Animation de lancement « les clics », silencieuse, environ 2,3 s : un curseur arrive, clique cinq fois de gauche à
/// droite et chaque clic (onde verte) fait apparaître une lettre de SE7EN ; puis une rafale de clics rapides fait
/// défiler le compteur jusqu'à « 7 CPS » sous AUTOCLICK. Un clic la passe.
/// </summary>
public sealed class LaunchSplash : Window
{
    private static readonly Color Accent = Color.FromRgb(0x3D, 0xFF, 0x8C);
    private const double W = 560, H = 330;
    private readonly Canvas _stage = new() { Width = W, Height = H, ClipToBounds = true };
    private readonly TextBlock _cps;
    private bool _closing;

    public LaunchSplash()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = W;
        Height = H;
        Cursor = Cursors.Hand;

        var card = new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = new RadialGradientBrush(Color.FromRgb(0x14, 0x24, 0x1B), Color.FromRgb(0x07, 0x0B, 0x09)) { RadiusX = .8, RadiusY = .9 },
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x3D, 0xFF, 0x8C)),
            BorderThickness = new Thickness(1),
            Child = _stage,
        };
        Content = card;
        var font = TryFont("FontDisplay") ?? new FontFamily("Segoe UI Black");
        var mono = TryFont("FontMono") ?? new FontFamily("Consolas");

        // grille de points (la zone de clic)
        for (double x = 20; x < W; x += 28)
            for (double y = 20; y < H; y += 28)
                Add(new Ellipse { Width = 2, Height = 2, Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0xFF, 0x8C)) }, x, y);

        string[] letters = ["S", "E", "7", "E", "N"];
        double lw = 74, x0 = (W - lw * letters.Length) / 2, ly = 92;
        var cursor = MakeCursor();
        Add(cursor, W + 40, H + 40);
        var t = TimeSpan.FromMilliseconds(320);
        double prevX = W + 40, prevY = H + 40;
        for (int i = 0; i < letters.Length; i++)
        {
            double cx = x0 + lw * i + lw / 2, cy = ly + 52;
            var tb = new TextBlock
            {
                Text = letters[i], FontFamily = font, FontSize = 74, FontWeight = FontWeights.Bold,
                Foreground = letters[i] == "7" ? new SolidColorBrush(Accent) : Brushes.White,
                Width = lw, TextAlignment = TextAlignment.Center, Opacity = 0,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(.2, .2),
            };
            Add(tb, x0 + lw * i, ly);
            // le curseur glisse vers la lettre, clique : onde + lettre
            var move = TimeSpan.FromMilliseconds(i == 0 ? 260 : 150);
            Animate(cursor, Canvas.LeftProperty, prevX, cx, t, move, new CubicEase { EasingMode = EasingMode.EaseOut });
            Animate(cursor, Canvas.TopProperty, prevY, cy, t, move, new CubicEase { EasingMode = EasingMode.EaseOut });
            var click = t + move;
            Press(cursor, click);
            Ripple(cx, cy, click, 64);
            Animate(tb, OpacityProperty, 0, 1, click, TimeSpan.FromMilliseconds(120));
            var pop = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut };
            Animate((ScaleTransform)tb.RenderTransform, ScaleTransform.ScaleXProperty, .2, 1, click, TimeSpan.FromMilliseconds(420), pop);
            Animate((ScaleTransform)tb.RenderTransform, ScaleTransform.ScaleYProperty, .2, 1, click, TimeSpan.FromMilliseconds(420), pop);
            prevX = cx;
            prevY = cy;
            t = click + TimeSpan.FromMilliseconds(30);
        }

        // AUTOCLICK + rafale de clics, compteur de CPS
        var name = new TextBlock { Text = "A U T O C L I C K", FontFamily = font, FontSize = 18, Foreground = new SolidColorBrush(Accent), Opacity = 0, Width = W, TextAlignment = TextAlignment.Center };
        Add(name, 0, 196);
        Animate(name, OpacityProperty, 0, 1, t, TimeSpan.FromMilliseconds(250));
        _cps = new TextBlock { Text = "0 CPS", FontFamily = mono, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), Width = W, TextAlignment = TextAlignment.Center, Opacity = 0 };
        Add(_cps, 0, 236);
        Animate(_cps, OpacityProperty, 0, 1, t, TimeSpan.FromMilliseconds(200));
        double bx = W / 2, by = 205;
        Animate(cursor, Canvas.LeftProperty, prevX, bx + 60, t, TimeSpan.FromMilliseconds(160));
        Animate(cursor, Canvas.TopProperty, prevY, by + 18, t, TimeSpan.FromMilliseconds(160));
        var burst = t + TimeSpan.FromMilliseconds(170);
        for (int k = 0; k < 7; k++)
        {
            var at = burst + TimeSpan.FromMilliseconds(k * 62);
            Press(cursor, at);
            Ripple(bx + 60, by + 18, at, 30);
            int n = k + 1;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = at };
            timer.Tick += (_, _) => { timer.Stop(); _cps.Text = $"{n} CPS"; };
            timer.Start();
        }
        var foot = new TextBlock { Text = "Cliquer pour passer", FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), Width = W, TextAlignment = TextAlignment.Center };
        Add(foot, 0, H - 34);

        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        var end = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2350) };
        end.Tick += (_, _) => { end.Stop(); Finish(); };
        end.Start();
        MouseLeftButtonDown += (_, _) => Finish();
        KeyDown += (_, _) => Finish();
    }

    /// <summary>Raised once the splash has faded out.</summary>
    public event EventHandler? Done;

    private void Finish()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { Close(); Done?.Invoke(this, EventArgs.Empty); };
        BeginAnimation(OpacityProperty, fade);
    }

    private FontFamily? TryFont(string key) => Application.Current?.TryFindResource(key) as FontFamily;

    private void Add(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        _stage.Children.Add(e);
    }

    private static void Animate(DependencyObject target, DependencyProperty p, double from, double to, TimeSpan begin, TimeSpan dur, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(from, to, dur) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        if (target is Animatable an) an.BeginAnimation(p, a);
        else if (target is UIElement ui) ui.BeginAnimation(p, a);
    }

    private void Ripple(double cx, double cy, TimeSpan at, double size)
    {
        var ring = new Ellipse
        {
            Width = size, Height = size, Stroke = new SolidColorBrush(Accent), StrokeThickness = 2, Opacity = 0,
            RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(.2, .2),
        };
        Add(ring, cx - size / 2, cy - size / 2);
        var st = (ScaleTransform)ring.RenderTransform;
        Animate(st, ScaleTransform.ScaleXProperty, .2, 1.4, at, TimeSpan.FromMilliseconds(420), new QuadraticEase { EasingMode = EasingMode.EaseOut });
        Animate(st, ScaleTransform.ScaleYProperty, .2, 1.4, at, TimeSpan.FromMilliseconds(420), new QuadraticEase { EasingMode = EasingMode.EaseOut });
        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = at };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(.95, TimeSpan.Zero));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(420)));
        ring.BeginAnimation(OpacityProperty, fade);
    }

    private static void Press(FrameworkElement cursor, TimeSpan at)
    {
        var st = (ScaleTransform)cursor.RenderTransform;
        foreach (var p in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var k = new DoubleAnimationUsingKeyFrames { BeginTime = at };
            k.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(.78, TimeSpan.FromMilliseconds(45)));
            k.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(110)));
            st.BeginAnimation(p, k);
        }
    }

    private static FrameworkElement MakeCursor()
    {
        var path = new Path
        {
            Data = Geometry.Parse("M0,0 L0,22 L6,16.5 L10,26 L14,24.4 L10,15 L17.5,15 Z"),
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x09)),
            StrokeThickness = 1.4,
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x3D, 0xFF, 0x8C), BlurRadius = 14, ShadowDepth = 0, Opacity = .55 },
        };
        Panel.SetZIndex(path, 10);
        return path;
    }
}
