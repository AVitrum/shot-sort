using System.Windows.Controls;

namespace ScreenSorter.App;

/// <summary>
/// An icon in a tinted circle beside a title and a sentence. A user control rather than a template, because WPF
/// leaves text inside a control template out of the accessibility tree, and screen readers would skip the guide.
/// </summary>
public partial class IconRow : UserControl
{
    /// <summary>Creates an empty row.</summary>
    public IconRow() => InitializeComponent();

    /// <summary>The icon, as a character from the Segoe Fluent Icons font.</summary>
    public string Glyph
    {
        get => GlyphBlock.Text;
        set => GlyphBlock.Text = value;
    }

    /// <summary>The row's title.</summary>
    public string Title
    {
        get => TitleBlock.Text;
        set => TitleBlock.Text = value;
    }

    /// <summary>The sentence under the title.</summary>
    public string Text
    {
        get => SentenceBlock.Text;
        set => SentenceBlock.Text = value;
    }
}
