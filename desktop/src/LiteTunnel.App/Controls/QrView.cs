using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ZXing.QrCode.Internal;

namespace LiteTunnel.App.Controls;

/// <summary>A QR code on a white card, black on white whatever the theme, so every camera reads it.</summary>
public sealed class QrView : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<QrView, string?>(nameof(Text));

    private const double MaxSide = 260, Padding = 16;
    private Geometry? modules;
    private int count;

    static QrView() => AffectsMeasure<QrView>(TextProperty);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) Encode();
    }

    private void Encode()
    {
        modules = null;
        count = 0;
        if (string.IsNullOrEmpty(Text)) return;
        QRCode code;
        try
        {
            code = Encoder.encode(Text, ErrorCorrectionLevel.M, new Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8" });
        }
        catch (ZXing.WriterException)
        {
            return;
        }
        var matrix = code.Matrix;
        count = matrix.Width;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (int y = 0; y < matrix.Height; y++)
            {
                for (int x = 0; x < matrix.Width; x++)
                {
                    if (matrix[x, y] != 1) continue;
                    g.BeginFigure(new Point(x, y), true);
                    g.LineTo(new Point(x + 1, y));
                    g.LineTo(new Point(x + 1, y + 1));
                    g.LineTo(new Point(x, y + 1));
                    g.EndFigure(true);
                }
            }
        }
        modules = geometry;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (modules is null) return default;
        double side = Math.Min(MaxSide, double.IsInfinity(availableSize.Width) ? MaxSide : availableSize.Width);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        if (modules is not { } geometry || count == 0) return;
        double side = Math.Min(Bounds.Width, Bounds.Height);
        var card = new Rect((Bounds.Width - side) / 2, 0, side, side);
        context.DrawRectangle(Brushes.White, null, card, 12, 12);
        double cell = (side - 2 * Padding) / count;
        var transform = Matrix.CreateScale(cell, cell) * Matrix.CreateTranslation(card.X + Padding, card.Y + Padding);
        using (context.PushTransform(transform))
            context.DrawGeometry(Brushes.Black, null, geometry);
    }
}
