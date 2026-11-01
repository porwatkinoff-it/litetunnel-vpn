using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using LiteTunnel.Core;

namespace LiteTunnel.App.Controls;

/// <summary>A measured ping in the chosen look: time, bars, both or a dot. Nothing before the first ping.</summary>
public sealed class PingBadge : StackPanel
{
    public static readonly StyledProperty<PingResult?> PingProperty =
        AvaloniaProperty.Register<PingBadge, PingResult?>(nameof(Ping));

    public static readonly StyledProperty<PingDisplay> DisplayProperty =
        AvaloniaProperty.Register<PingBadge, PingDisplay>(nameof(Display));

    public PingBadge()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public PingResult? Ping
    {
        get => GetValue(PingProperty);
        set => SetValue(PingProperty, value);
    }

    public PingDisplay Display
    {
        get => GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PingProperty || change.Property == DisplayProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Ping is not { } ping)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        var tone = ping.Tone().ToLowerInvariant();
        AutomationProperties.SetName(this, ping.Label());
        ToolTip.SetTip(this, Display == PingDisplay.Time ? null : ping.Label());
        if (Display is PingDisplay.Time or PingDisplay.Both)
        {
            var text = new TextBlock { Text = ping.Label(), VerticalAlignment = VerticalAlignment.Center };
            text.Classes.AddRange(["caption", "tabular", tone]);
            Children.Add(text);
        }
        if (Display is PingDisplay.Bars or PingDisplay.Both)
        {
            var bars = new PingBars { Level = ping.Level(), VerticalAlignment = VerticalAlignment.Center };
            bars.Classes.Add(tone);
            Children.Add(bars);
        }
        if (Display == PingDisplay.Dots)
        {
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Classes.Add(ping is PingMs ? "connected" : "danger");
            Children.Add(dot);
        }
    }
}
