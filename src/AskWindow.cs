using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TaskWall;

/// <summary>One-line question (e.g. a custom estimate): returns the text, or null when cancelled.</summary>
public sealed class AskWindow : DarkWindow
{
    readonly TextBox _box = new() { Style = (Style)Application.Current.Resources["FieldBox"], Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _error = Label("", 11.5, "FgDim");
    readonly Func<string, string?>? _validate;
    string? _result;

    AskWindow(string title, string prompt, string initial, Func<string, string?>? validate)
    {
        _validate = validate;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        var p = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        p.Children.Add(Label(prompt, 12.5, "FgDim"));
        _box.Text = initial;
        p.Children.Add(_box);
        _error.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Btn(L.T("Anuluj"), "SecondaryButton", (_, _) => Close()));
        buttons.Children.Add(Btn(L.T("Zapisz"), "PrimaryButton", (_, _) => Accept()));
        p.Children.Add(buttons);
        Content = p;
        _box.TextChanged += (_, _) => _error.Text = _validate?.Invoke(_box.Text) ?? "";
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); _error.Text = _validate?.Invoke(_box.Text) ?? ""; };
    }

    void Accept()
    {
        _result = _box.Text.Trim();
        Close();
    }

    public static string? Ask(string title, string prompt, string initial = "", Func<string, string?>? validate = null)
    {
        var w = new AskWindow(title, prompt, initial, validate);
        w.ShowDialog();
        return w._result;
    }
}
