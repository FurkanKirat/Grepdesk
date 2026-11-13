using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class CodeEditorToolsTests
{
    [Fact]
    public void Search_supports_literal_case_whole_word_and_regex()
    {
        var text = "Foo foo foobar foo_1 foo";

        var literal = CodeEditorSearch.FindMatches(text, "foo", matchCase: false, wholeWord: false, useRegex: false, out var valid1);
        Assert.True(valid1);
        Assert.Equal(5, literal.Count);

        var cased = CodeEditorSearch.FindMatches(text, "foo", matchCase: true, wholeWord: false, useRegex: false, out var valid2);
        Assert.True(valid2);
        Assert.Equal(4, cased.Count);

        var word = CodeEditorSearch.FindMatches(text, "foo", matchCase: true, wholeWord: true, useRegex: false, out var valid3);
        Assert.True(valid3);
        Assert.Equal(2, word.Count);
        Assert.Equal(new TextSpan(4, 3), word[0]);
        Assert.Equal(new TextSpan(21, 3), word[1]);

        var regex = CodeEditorSearch.FindMatches(text, @"foo_\d+", matchCase: false, wholeWord: false, useRegex: true, out var valid4);
        Assert.True(valid4);
        Assert.Single(regex);
        Assert.Equal(new TextSpan(15, 5), regex[0]);
    }

    [Fact]
    public void Search_handles_invalid_regex_without_throwing()
    {
        var matches = CodeEditorSearch.FindMatches("hello", "(unclosed", matchCase: false, wholeWord: false, useRegex: true, out var valid);

        Assert.False(valid);
        Assert.Empty(matches);
    }

    [Fact]
    public void Search_navigation_wraps_forward_and_backward()
    {
        TextSpan[] matches = [new(2, 3), new(10, 3), new(20, 3)];

        Assert.Equal(1, CodeEditorSearch.FindMatchIndex(matches, caretOffset: 5, backward: false));
        Assert.Equal(0, CodeEditorSearch.FindMatchIndex(matches, caretOffset: 25, backward: false));
        Assert.Equal(1, CodeEditorSearch.FindMatchIndex(matches, caretOffset: 15, backward: true));
        Assert.Equal(2, CodeEditorSearch.FindMatchIndex(matches, caretOffset: 1, backward: true));
        Assert.Equal(-1, CodeEditorSearch.FindMatchIndex([], caretOffset: 0, backward: false));
    }

    [Fact]
    public void Replace_supports_single_and_all_with_regex_groups()
    {
        var single = CodeEditorSearch.ReplaceSingle("item_42", @"item_(\d+)", "id:$1", matchCase: false, useRegex: true);
        Assert.Equal("id:42", single);

        var updated = CodeEditorSearch.ReplaceAll(
            "a=1; b=22; c=333;",
            @"(\w+)=(\d+)",
            "$1:$2",
            matchCase: false,
            wholeWord: false,
            useRegex: true,
            out var count);

        Assert.Equal(3, count);
        Assert.Equal("a:1; b:22; c:333;", updated);
    }

    [Theory]
    [InlineData("12", 50, true, 12, 1)]
    [InlineData("12:7", 50, true, 12, 7)]
    [InlineData("999:3", 50, true, 50, 3)]
    [InlineData("0", 50, true, 1, 1)]
    [InlineData("abc", 50, false, 1, 1)]
    public void TryParseGoToLine_parses_and_clamps(string input, int totalLines, bool expectedOk, int expectedLine, int expectedCol)
    {
        var ok = CodeEditorSearch.TryParseGoToLine(input, totalLines, out var line, out var col);

        Assert.Equal(expectedOk, ok);
        if (expectedOk)
        {
            Assert.Equal(expectedLine, line);
            Assert.Equal(expectedCol, col);
        }
    }

    [Fact]
    public void BracketMatcher_finds_nested_pairs_and_skips_strings_and_comments()
    {
        var code = "void F() { var s = \"}\"; /* ) */ if (a[0] == ')') { return; } }";
        var outerOpen = code.IndexOf('{');
        var outerClose = code.LastIndexOf('}');
        var innerOpen = code.IndexOf('{', outerOpen + 1);
        var innerClose = code.LastIndexOf('}', outerClose - 1);

        Assert.Equal((outerOpen, outerClose), BracketMatcher.FindMatchingBracket(code, outerOpen));
        Assert.Equal((outerOpen, outerClose), BracketMatcher.FindMatchingBracket(code, outerClose + 1));
        Assert.Equal((innerOpen, innerClose), BracketMatcher.FindMatchingBracket(code, innerOpen));

        var arrOpen = code.IndexOf('[');
        var arrClose = code.IndexOf(']');
        Assert.Equal((arrOpen, arrClose), BracketMatcher.FindMatchingBracket(code, arrClose));
    }

    [Fact]
    public void CodeFolder_creates_brace_and_region_foldings()
    {
        var cs = """
                 #region Helpers
                 class C
                 {
                     void M()
                     {
                         var x = 1;
                     }
                 }
                 #endregion
                 """;

        var folds = CodeFolder.GetFoldings(cs, ".cs");

        Assert.Contains(folds, f => f.Title == "Helpers");
        Assert.Contains(folds, f => f.Title == "{...}");
        Assert.True(folds.Count >= 3);
        for (var i = 1; i < folds.Count; i++)
            Assert.True(folds[i - 1].StartOffset <= folds[i].StartOffset);
    }

    [Fact]
    public void CodeFolder_creates_xml_and_markdown_foldings()
    {
        var xml = "<Root>\n  <Child>\n    <Leaf />\n  </Child>\n</Root>";
        var xmlFolds = CodeFolder.GetFoldings(xml, ".axaml");
        Assert.Contains(xmlFolds, f => f.Title == "<Root>");
        Assert.Contains(xmlFolds, f => f.Title == "<Child>");

        var md = "# Title\nIntro\n## Sub\nText\n# Next\nEnd";
        var mdFolds = CodeFolder.GetFoldings(md, ".md");
        Assert.Contains(mdFolds, f => f.Title == "Title");
        Assert.Contains(mdFolds, f => f.Title == "Sub");
        Assert.Contains(mdFolds, f => f.Title == "Next");
    }

    [Fact]
    public void SymbolExtractor_extracts_symbols_across_languages_and_finds_enclosing()
    {
        var cs = """
                 namespace Demo;
                 public class Widget
                 {
                     public void Render(int x)
                     {
                         if (x > 0) { }
                     }
                 }
                 """;
        var csSymbols = CodeSymbolExtractor.ExtractSymbols(cs, ".cs");
        Assert.Contains(csSymbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(csSymbols, s => s.Kind == "fn" && s.Name == "Render");
        Assert.DoesNotContain(csSymbols, s => s.Name == "if");
        Assert.Equal("Render", CodeSymbolExtractor.FindEnclosingSymbol(csSymbols, 6)?.Name);

        var py = "class Worker:\n    async def run(self):\n        pass\n";
        var pySymbols = CodeSymbolExtractor.ExtractSymbols(py, ".py");
        Assert.Equal(2, pySymbols.Count);
        Assert.Equal(("class", "Worker"), (pySymbols[0].Kind, pySymbols[0].Name));
        Assert.Equal(("def", "run"), (pySymbols[1].Kind, pySymbols[1].Name));

        var json = "{\n  \"name\": \"grepdesk\",\n  \"version\": \"1.0\"\n}";
        var jsonSymbols = CodeSymbolExtractor.ExtractSymbols(json, ".json");
        Assert.Equal(2, jsonSymbols.Count);
        Assert.Equal("name", jsonSymbols[0].Name);

        var md = "# Heading 1\n```\n# Not a heading\n```\n## Heading 2\n";
        var mdSymbols = CodeSymbolExtractor.ExtractSymbols(md, ".md");
        Assert.Equal(2, mdSymbols.Count);
        Assert.Equal(("H1", "Heading 1"), (mdSymbols[0].Kind, mdSymbols[0].Name));
        Assert.Equal(("H2", "Heading 2"), (mdSymbols[1].Kind, mdSymbols[1].Name));
    }

    [Fact]
    public void Formatter_validates_and_formats_json_and_xml()
    {
        Assert.True(CodeFormatter.CanFormat("config.json"));
        Assert.True(CodeFormatter.CanFormat("MainWindow.axaml"));
        Assert.False(CodeFormatter.CanFormat("Program.cs"));

        var json = CodeFormatter.TryFormat("{\"a\":1,\"b\":\"şğü\"}\r\n", "a.json");
        Assert.True(json.Success);
        Assert.Contains("\r\n", json.FormattedText);
        Assert.Contains("şğü", json.FormattedText);

        var badJson = CodeFormatter.TryFormat("{\n  \"a\": \n}", "a.json");
        Assert.False(badJson.Success);
        Assert.True(badJson.ErrorLine >= 2);

        var xml = CodeFormatter.TryFormat("<Root><Item id=\"1\"/></Root>", "a.xml");
        Assert.True(xml.Success);
        Assert.Contains("<Item id=\"1\" />", xml.FormattedText);

        var badXml = CodeFormatter.TryFormat("<Root>\n<Unclosed>\n</Root>", "a.axaml");
        Assert.False(badXml.Success);
        Assert.True(badXml.ErrorLine >= 2);
    }

    [Fact]
    public void Formatter_detects_line_endings_indentation_and_toggles_comments()
    {
        Assert.Equal("CRLF", CodeFormatter.DetectLineEnding("a\r\nb\r\n"));
        Assert.Equal("LF", CodeFormatter.DetectLineEnding("a\nb\n"));
        Assert.Equal("a\r\nb", CodeFormatter.ConvertLineEndings("a\nb", toCrlf: true));
        Assert.Equal("a\nb", CodeFormatter.ConvertLineEndings("a\r\nb", toCrlf: false));

        Assert.Equal("Tabs", CodeFormatter.DetectIndentation("fn a() {\n\treturn 1;\n}"));
        Assert.Equal("Spaces: 2", CodeFormatter.DetectIndentation("{\n  \"a\": 1\n}"));
        Assert.Equal("Spaces: 4", CodeFormatter.DetectIndentation("class A\n{\n    int x;\n}"));

        var csText = "    var a = 1;\n    var b = 2;";
        var (commented, s1, l1, ns1, nl1) = CodeFormatter.ToggleLineComment(csText, 0, csText.Length, "test.cs");
        Assert.Equal("    // var a = 1;\n    // var b = 2;", commented);
        var (uncommented, _, _, _, _) = CodeFormatter.ToggleLineComment(commented, ns1, nl1, "test.cs");
        Assert.Equal(csText, uncommented);

        var (pyCommented, _, _, _, _) = CodeFormatter.ToggleLineComment("x = 1", 0, 0, "app.py");
        Assert.Equal("# x = 1", pyCommented);

        var (sqlCommented, _, _, _, _) = CodeFormatter.ToggleLineComment("SELECT 1;", 0, 0, "query.sql");
        Assert.Equal("-- SELECT 1;", sqlCommented);

        var (xmlCommented, _, _, _, _) = CodeFormatter.ToggleLineComment("<Tag />", 0, 0, "view.axaml");
        Assert.Equal("<!-- <Tag /> -->", xmlCommented);
        var (xmlUncommented, _, _, _, _) = CodeFormatter.ToggleLineComment(xmlCommented, 0, 0, "view.axaml");
        Assert.Equal("<Tag />", xmlUncommented);
    }
}
