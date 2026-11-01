using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LiteTunnel.App.Controls;

/// <summary>Four signal bars, as many filled as <see cref="Level"/>.</summary>
public sealed class PingBars : Control
{
    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<PingBars, int>(nameof(Level));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<PingBars>();

    private const double BarWidth = 3, Gap = 2, BarsHeight = 13;

    static PingBars() => AffectsRender<PingBars>(LevelProperty, ForegroundProperty);

    public int Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(4 * BarWidth + 3 * Gap, BarsHeight);

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } brush) return;
        double top = (Bounds.Height - BarsHeight) / 2;
        for (int i = 0; i < 4; i++)
        {
            double h = 4 + i * 3;
            var rect = new Rect(i * (BarWidth + Gap), top + BarsHeight - h, BarWidth, h);
            using (context.PushOpacity(i < Level ? 1 : 0.25))
                context.DrawRectangle(brush, null, rect, 1, 1);
        }
    }
}
