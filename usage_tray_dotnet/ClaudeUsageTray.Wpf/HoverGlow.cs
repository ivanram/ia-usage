using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeUsageTray;

/// <summary>
/// A soft radial glow that follows the cursor across a button, faded in/out
/// rather than an instant on/off — purely cosmetic, opt-in per element via
/// Attach(), and globally toggleable through GloballyEnabled so the
/// Settings switch can turn every attached instance off at once without
/// each caller having to know about the setting. Nothing like this existed
/// in the app before; scope is deliberately limited to a handful of real
/// action buttons in SettingsWindow/StatsWindow (see callers) — window
/// chrome (close/maximize) and the color/palette swatches are skipped.
/// </summary>
public static class HoverGlow
{
    private const int FadeInMs = 130;
    private const int FadeOutMs = 240;
    private const double PeakOpacity = 0.14;

    public static bool GloballyEnabled { get; set; } = true;

    public static void Attach(FrameworkElement target, Func<Brush> glowColor)
    {
        var adorner = new GlowAdorner(target, glowColor);
        var attached = false;

        void EnsureAdorner()
        {
            if (attached) return;
            var layer = AdornerLayer.GetAdornerLayer(target);
            if (layer is null) return;
            layer.Add(adorner);
            attached = true;
        }

        target.Loaded += (_, _) => EnsureAdorner();
        target.Unloaded += (_, _) =>
        {
            if (!attached) return;
            AdornerLayer.GetAdornerLayer(target)?.Remove(adorner);
            attached = false;
        };

        target.PreviewMouseMove += (_, e) => adorner.MoveTo(e.GetPosition(target));
        target.MouseEnter += (_, _) =>
        {
            if (!GloballyEnabled) return;
            EnsureAdorner();
            adorner.Fade(1.0);
        };
        target.MouseLeave += (_, _) => adorner.Fade(0.0);

        if (target.IsLoaded) EnsureAdorner();
    }

    private sealed class GlowAdorner : Adorner
    {
        public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
            nameof(Strength), typeof(double), typeof(GlowAdorner),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        private readonly Func<Brush> _glowColor;
        private Point _center;

        public double Strength
        {
            get => (double)GetValue(StrengthProperty);
            set => SetValue(StrengthProperty, value);
        }

        public GlowAdorner(UIElement adornedElement, Func<Brush> glowColor) : base(adornedElement)
        {
            _glowColor = glowColor;
            IsHitTestVisible = false;
            _center = new Point(adornedElement.RenderSize.Width / 2, adornedElement.RenderSize.Height / 2);
        }

        public void MoveTo(Point point)
        {
            _center = point;
            if (Strength > 0.0) InvalidateVisual();
        }

        public void Fade(double target)
        {
            var duration = TimeSpan.FromMilliseconds(target > 0 ? FadeInMs : FadeOutMs);
            var anim = new DoubleAnimation(target, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
            BeginAnimation(StrengthProperty, anim);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!HoverGlow.GloballyEnabled || Strength <= 0.002) return;

            var size = AdornedElement.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            var reach = Math.Max(36.0, Math.Min(size.Height * 1.6, 120.0));

            var color = (_glowColor() as SolidColorBrush)?.Color ?? Colors.White;
            var center = color;
            center.A = (byte)(PeakOpacity * Strength * 255);
            var edge = color;
            edge.A = 0;

            var gradient = new RadialGradientBrush
            {
                GradientOrigin = new Point(_center.X / size.Width, _center.Y / size.Height),
                Center = new Point(_center.X / size.Width, _center.Y / size.Height),
                RadiusX = reach / size.Width,
                RadiusY = reach / size.Height,
            };
            gradient.GradientStops.Add(new GradientStop(center, 0.0));
            gradient.GradientStops.Add(new GradientStop(edge, 1.0));
            gradient.Freeze();

            drawingContext.DrawRectangle(gradient, null, new Rect(size));
        }
    }
}
