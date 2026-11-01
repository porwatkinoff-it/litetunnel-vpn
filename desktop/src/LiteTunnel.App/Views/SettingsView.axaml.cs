using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LiteTunnel.App.ViewModels;

namespace LiteTunnel.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Typed fields (DNS, ping address, MTU) apply when they lose focus or on Enter.
        AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox) Commit();
        }, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is TextBox) Commit();
        }, RoutingStrategies.Bubble);
    }

    private void Commit() => (DataContext as SettingsViewModel)?.CommitFields();
}
