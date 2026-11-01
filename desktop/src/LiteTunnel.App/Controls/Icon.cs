using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LiteTunnel.App.Controls;

/// <summary>A Material Symbols icon: one path on the 960 grid, drawn in the text colour around it.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 20);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        double scale = Size / 960;
        var offset = Matrix.CreateTranslation((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
            context.DrawGeometry(brush, null, data);
    }
}
