using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Xml;
using Cairn.Atx.Ui.Services;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Cairn.Atx.Ui.Editor;

/// <summary>
/// Builds the .atx (TOML) syntax highlighting. The rules are fixed; the colours come from the
/// active theme's resource dictionary, so the light and dark colour sets are the same definition
/// rendered twice rather than two files to keep in step.
/// </summary>
public static class AtxHighlighting
{
    /// <summary>Builds a highlighting definition using the current theme's syntax colours.</summary>
    public static IHighlightingDefinition Build(ThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        string xshd = Template
            .Replace("$COMMENT$", Hex(theme.Color("Syntax.CommentColor", Colors.Green)), StringComparison.Ordinal)
            .Replace("$TABLE$", Hex(theme.Color("Syntax.TableColor", Colors.Purple)), StringComparison.Ordinal)
            .Replace("$KEY$", Hex(theme.Color("Syntax.KeyColor", Colors.Navy)), StringComparison.Ordinal)
            .Replace("$STRING$", Hex(theme.Color("Syntax.StringColor", Colors.Firebrick)), StringComparison.Ordinal)
            .Replace("$NUMBER$", Hex(theme.Color("Syntax.NumberColor", Colors.Teal)), StringComparison.Ordinal)
            .Replace("$BOOL$", Hex(theme.Color("Syntax.BooleanColor", Colors.SteelBlue)), StringComparison.Ordinal)
            .Replace("$PUNCT$", Hex(theme.Color("Syntax.PunctuationColor", Colors.Gray)), StringComparison.Ordinal);

        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string Hex(Color color) =>
        string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);

    private const string Template = @"<SyntaxDefinition name=""ATX"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""$COMMENT$"" />
  <Color name=""Table"" foreground=""$TABLE$"" fontWeight=""bold"" />
  <Color name=""Key"" foreground=""$KEY$"" />
  <Color name=""Str"" foreground=""$STRING$"" />
  <Color name=""Number"" foreground=""$NUMBER$"" />
  <Color name=""Boolean"" foreground=""$BOOL$"" fontWeight=""bold"" />
  <Color name=""Punctuation"" foreground=""$PUNCT$"" />

  <RuleSet ignoreCase=""false"">
    <Span color=""Comment"">
      <Begin>\#</Begin>
    </Span>

    <Span color=""Str"" multiline=""false"">
      <Begin>&quot;</Begin>
      <End>&quot;</End>
      <RuleSet>
        <Span begin=""\\"" end=""."" />
      </RuleSet>
    </Span>

    <Span color=""Str"" multiline=""false"">
      <Begin>'</Begin>
      <End>'</End>
    </Span>

    <Rule color=""Table"">\[\[?\s*[A-Za-z_][A-Za-z0-9_.\-]*\s*\]?\]</Rule>

    <Rule color=""Key"">[A-Za-z_][A-Za-z0-9_\-]*(?=\s*=)</Rule>

    <Keywords color=""Boolean"">
      <Word>true</Word>
      <Word>false</Word>
    </Keywords>

    <Rule color=""Number"">[+\-]?\b\d[\d_]*(\.[\d_]+)?([eE][+\-]?\d+)?\b</Rule>

    <Rule color=""Punctuation"">[=,\[\]{}]</Rule>
  </RuleSet>
</SyntaxDefinition>";
}
