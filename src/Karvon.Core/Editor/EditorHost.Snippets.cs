using System.Text.Json;
using System.Text.RegularExpressions;
using AvaloniaEdit.Snippets;
using Karvon.Lsp;
using EditSnippet = AvaloniaEdit.Snippets.Snippet;

namespace Karvon.Editor;

/// <summary>
/// Visual Studio's C# code snippets: a shortcut (prop, ctor, for...) and Tab, or Tab twice from IntelliSense, puts the
/// snippet in its place. Tab and Shift+Tab go through its fields (a field's copies follow it), Enter ends it with the caret
/// at its end, Escape where the caret is.
/// </summary>
public sealed partial class EditorHost
{
    /// <summary>A snippet: "$name=default$" is a field (its first time), "$name$" the same field again, "$end$" the caret.</summary>
    sealed record CodeSnippet(string Shortcut, string Title, string Body);

    static readonly CodeSnippet[] CSharpSnippets =
    [
        new("#if", "Code snippet for #if", "#if $expression=true$\n$end$\n#endif"),
        new("#region", "Code snippet for #region", "#region $name=MyRegion$\n$end$\n#endregion"),
        new("~", "Code snippet for destructor", "~$classname$()\n{\n\t$end$\n}"),
        new("attribute", "Code snippet for attribute using recommended pattern",
            "[System.AttributeUsage(System.AttributeTargets.$target=All$, Inherited = $inherited=false$, AllowMultiple = $allowmultiple=true$)]\n"
            + "sealed class $name=My$Attribute : System.Attribute\n{\n\treadonly string positionalString;\n\n\t// This is a positional argument\n"
            + "\tpublic $name$Attribute(string positionalString)\n\t{\n\t\tthis.positionalString = positionalString;\n\n\t\t// TODO: Implement code here\n"
            + "\t\t$end$throw new System.NotImplementedException();\n\t}\n\n\tpublic string PositionalString\n\t{\n\t\tget { return positionalString; }\n\t}\n\n"
            + "\t// This is a named argument\n\tpublic int NamedInt { get; set; }\n}"),
        new("checked", "Code snippet for checked block", "checked\n{\n\t$end$\n}"),
        new("class", "Code snippet for class", "class $name=MyClass$\n{\n\t$end$\n}"),
        new("ctor", "Code snippet for constructor", "public $classname$()\n{\n\t$end$\n}"),
        new("cw", "Code snippet for Console.WriteLine", "Console.WriteLine($end$);"),
        new("do", "Code snippet for do...while loop", "do\n{\n\t$end$\n} while ($expression=true$);"),
        new("else", "Code snippet for else statement", "else\n{\n\t$end$\n}"),
        new("enum", "Code snippet for enum", "enum $name=MyEnum$\n{\n\t$end$\n}"),
        new("equals", "Code snippet for implementing Equals() according to guidelines",
            "// override object.Equals\npublic override bool Equals(object obj)\n{\n\tif (obj == null || GetType() != obj.GetType())\n\t{\n\t\treturn false;\n\t}\n\n"
            + "\t// TODO: write your implementation of Equals() here\n\tthrow new System.NotImplementedException();$end$\n}\n\n"
            + "// override object.GetHashCode\npublic override int GetHashCode()\n{\n\t// TODO: write your implementation of GetHashCode() here\n"
            + "\tthrow new System.NotImplementedException();\n}"),
        new("exception", "Code snippet for exception",
            "[System.Serializable]\npublic class $newException=My$Exception : System.Exception\n{\n\tpublic $newException$Exception() { }\n"
            + "\tpublic $newException$Exception(string message) : base(message) { }\n"
            + "\tpublic $newException$Exception(string message, System.Exception inner) : base(message, inner) { }\n}$end$"),
        new("for", "Code snippet for 'for' loop", "for (int $index=i$ = 0; $index$ < $max=length$; $index$++)\n{\n\t$end$\n}"),
        new("foreach", "Code snippet for foreach statement", "foreach ($type=var$ $identifier=item$ in $collection=collection$)\n{\n\t$end$\n}"),
        new("forr", "Code snippet for reverse 'for' loop", "for (int $index=i$ = $max=length$ - 1; $index$ >= 0; $index$--)\n{\n\t$end$\n}"),
        new("if", "Code snippet for if statement", "if ($expression=true$)\n{\n\t$end$\n}"),
        new("indexer", "Code snippet for indexer",
            "$access=public$ $type=object$ this[$indextype=int$ index]\n{\n\tget { $end$/* return the specified index here */ }\n\tset { /* set the specified index to value here */ }\n}"),
        new("interface", "Code snippet for interface", "interface $name=IInterface$\n{\n\t$end$\n}"),
        new("lock", "Code snippet for lock statement", "lock ($expression=this$)\n{\n\t$end$\n}"),
        new("namespace", "Code snippet for namespace", "namespace $name=MyNamespace$\n{\n\t$end$\n}"),
        new("prop", "Code snippet for an automatically implemented property", "public $type=int$ $property=MyProperty$ { get; set; }$end$"),
        new("propfull", "Code snippet for property and backing field",
            "private $type=int$ $field=myVar$;\n\npublic $type$ $property=MyProperty$\n{\n\tget { return $field$; }\n\tset { $field$ = value; }\n}$end$"),
        new("propg", "Code snippet for an automatically implemented property with a 'get' accessor and a private 'set' accessor",
            "public $type=int$ $property=MyProperty$ { get; private set; }$end$"),
        new("sim", "Code snippet for int Main()", "static int Main(string[] args)\n{\n\t$end$\n\treturn 0;\n}"),
        new("struct", "Code snippet for struct", "struct $name=MyStruct$\n{\n\t$end$\n}"),
        new("svm", "Code snippet for 'void Main' method", "static void Main(string[] args)\n{\n\t$end$\n}"),
        new("switch", "Code snippet for switch statement", "switch ($expression=switch_on$)\n{\n\t$end$\n\tdefault:\n\t\tbreak;\n}"),
        new("try", "Code snippet for try catch", "try\n{\n\t\n}\ncatch ($expression=Exception$)\n{\n\t$end$\n\tthrow;\n}"),
        new("tryf", "Code snippet for try finally", "try\n{\n\t\n}\nfinally\n{\n\t$end$\n}"),
        new("unchecked", "Code snippet for unchecked block", "unchecked\n{\n\t$end$\n}"),
        new("unsafe", "Code snippet for unsafe statement", "unsafe\n{\n\t$end$\n}"),
        new("using", "Code snippet for using statement", "using ($resource=resource$)\n{\n\t$end$\n}"),
        new("while", "Code snippet for while loop", "while ($expression=true$)\n{\n\t$end$\n}"),
    ];

    static readonly JsonElement NoData = JsonDocument.Parse("{}").RootElement;

    bool IsCSharp => FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || FilePath.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);

    /// <summary>The snippet whose fields are being filled in.</summary>
    InsertionContext? _snippet;

    /// <summary>Whether a snippet's fields are being filled in.</summary>
    internal bool IsInSnippet => _snippet is not null;

    /// <summary>IntelliSense's snippet entries, as Visual Studio lists them beside the language's (not after a ".").</summary>
    IEnumerable<LspCompletionItem> SnippetCompletions(int start) =>
        IsCSharp && (start == 0 || Document.GetCharAt(start - 1) != '.')
            ? CSharpSnippets.Where(s => char.IsLetter(s.Shortcut[0])).Select(s => new LspCompletionItem(s.Shortcut, null, s.Shortcut, s.Shortcut, false, 15,
                $"{s.Shortcut}\n{s.Title}\nNote: Tab twice to insert the '{s.Shortcut}' snippet.", null, [], NoData))
            : [];

    /// <summary>Tab after a snippet's shortcut: the snippet in its place, its first field selected; false if there is none.</summary>
    bool ExpandSnippet()
    {
        if (!IsCSharp || _completionWindow is not null || _carets.Count > 0 || Editor.SelectionLength > 0 || IsInSnippet)
            return false;
        var caret = Editor.CaretOffset;
        if (caret < Document.TextLength && IsIdentifierChar(Document.GetCharAt(caret)))
            return false;
        var start = FindWordStart(caret);
        if (start > 0 && Document.GetCharAt(start - 1) is '#' or '~')
            start--;
        var shortcut = Document.GetText(start, caret - start);
        if (CSharpSnippets.FirstOrDefault(s => s.Shortcut == shortcut) is not { } snippet || start > 0 && Document.GetCharAt(start - 1) == '.')
            return false;
        _completionCts?.Cancel();
        DismissInlineSuggestion();
        var built = Build(snippet, EnclosingTypeName(start));
        using (Document.RunUpdate())
        {
            Document.Remove(start, caret - start);
            var context = _snippet = new InsertionContext(Editor.TextArea, start);
            context.Deactivated += (_, _) =>
            {
                if (_snippet == context)
                    _snippet = null;
            };
            built.Insert(context);
            context.RaiseInsertionCompleted(EventArgs.Empty);
        }
        return true;
    }

    /// <summary>The snippet as AvaloniaEdit's: its text, fields (their copies bound to them) and caret.</summary>
    static EditSnippet Build(CodeSnippet snippet, string? className)
    {
        var built = new EditSnippet();
        var fields = new Dictionary<string, SnippetReplaceableTextElement>();
        var parts = snippet.Body.Split('$');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0)
            {
                if (parts[i].Length > 0)
                    built.Elements.Add(new SnippetTextElement { Text = parts[i] });
                continue;
            }
            var (name, text) = parts[i].IndexOf('=') is var equals and >= 0 ? (parts[i][..equals], parts[i][(equals + 1)..]) : (parts[i], "");
            if (name == "end")
                built.Elements.Add(new SnippetCaretElement());
            else if (name == "classname" && className is not null)
                built.Elements.Add(new SnippetTextElement { Text = className });
            else if (fields.TryGetValue(name, out var field))
                built.Elements.Add(new SnippetBoundElement { TargetElement = field });
            else
                built.Elements.Add(fields[name] = new SnippetReplaceableTextElement { Text = name == "classname" ? "ClassNamePlaceholder" : text });
        }
        return built;
    }

    [GeneratedRegex(@"\b(?:class|struct|record)\s+(@?\w+)")]
    private static partial Regex TypeDeclaration();

    /// <summary>The name of the class (struct, record) whose braces the offset is in; null outside one.</summary>
    string? EnclosingTypeName(int offset)
    {
        var text = Document.Text;
        foreach (var declaration in TypeDeclaration().Matches(text[..offset]).Reverse())
        {
            var open = text.IndexOf('{', declaration.Index + declaration.Length);
            if (open < 0 || open >= offset)
                continue;
            if (MatchingBrace(text, open, OpeningBraces.IndexOf('{'), forward: true) is not { } close || close >= offset)
                return declaration.Groups[1].Value;
        }
        return null;
    }
}
