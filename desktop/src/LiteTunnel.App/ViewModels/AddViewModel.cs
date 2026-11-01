using LiteTunnel.App.Controls;

namespace LiteTunnel.App.ViewModels;

/// <summary>Adding a subscription or servers: from the clipboard, typed in, or from a QR code in a picture.</summary>
public sealed class AddViewModel : PageViewModel
{
    private string text = "";
    private bool working;

    public AddViewModel(MainViewModel main) : base(main)
    {
        Paste = Command.Async(PasteAsync, () => !working);
        AddText = Command.Async(() => ImportAsync(text), () => !working && !string.IsNullOrWhiteSpace(text));
        FromImage = Command.Async(FromImageAsync, () => !working);
    }

    public override string Title => "Добавить";

    public override bool IsSheet => true;

    public string Text
    {
        get => text;
        set
        {
            if (Set(ref text, value)) AddText.Refresh();
        }
    }

    public bool IsWorking
    {
        get => working;
        private set
        {
            if (!Set(ref working, value)) return;
            Paste.Refresh();
            AddText.Refresh();
            FromImage.Refresh();
        }
    }

    public Command Paste { get; }

    public Command AddText { get; }

    public Command FromImage { get; }

    private async Task PasteAsync()
    {
        var pasted = await Main.Shell.PasteAsync();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            Model.Show("В буфере обмена нет текста", error: true);
            return;
        }
        await ImportAsync(pasted);
    }

    private async Task FromImageAsync()
    {
        string? found;
        try
        {
            await using var image = await Main.Shell.PickImageAsync();
            if (image is null) return;
            found = await Task.Run(() => QrReader.Decode(image));
        }
        catch (Exception)
        {
            // The image decoder throws its own kinds for a file that is not a picture.
            Model.Show("Не удалось открыть изображение", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(found))
        {
            Model.Show("На картинке нет QR-кода", error: true);
            return;
        }
        await ImportAsync(found);
    }

    private async Task ImportAsync(string value)
    {
        IsWorking = true;
        bool added;
        try
        {
            added = await Model.ImportTextAsync(value);
        }
        finally
        {
            IsWorking = false;
        }
        if (added) Main.Close(this);
    }
}
