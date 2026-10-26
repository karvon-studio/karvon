using Karvon.Editor;
using Karvon.Kotlin;

namespace Karvon.Tests;

/// <summary>The grammars modules bring for the languages TextMateSharp has none for: Kotlin's.</summary>
public class SyntaxGrammarTests
{
    [Fact]
    public void Kotlin_files_are_colored_by_the_kotlin_modules_grammar()
    {
        foreach (var grammar in new KotlinModule().Grammars)
            TextMateSupport.Add(grammar);
        Assert.Equal("source.kotlin", TextMateSupport.ScopeFor("/src/DeviceKey.kt"));
        Assert.Equal("source.kotlin", TextMateSupport.ScopeFor("/build.gradle.kts"));

        // Each word's innermost scope, as the theme colors it: keywords, modifiers, comments, strings, numbers.
        var kotlin = TextMateSupport.LoadGrammar("source.kotlin")!;
        string ScopeOf(string line, string word)
        {
            var at = line.IndexOf(word, StringComparison.Ordinal);
            var token = kotlin.TokenizeLine(line, null, TimeSpan.FromSeconds(5)).Tokens.First(t => t.StartIndex <= at && at < t.EndIndex);
            return token.Scopes[^1];
        }
        Assert.StartsWith("comment.line", ScopeOf("// a key of its own, made once", "key"));
        Assert.StartsWith("keyword", ScopeOf("    suspend fun register(id: String) {", "fun"));
        Assert.StartsWith("storage.modifier", ScopeOf("    private val gate = Mutex()", "private"));
        Assert.StartsWith("string", ScopeOf("    val name = \"device\"", "device"));
        Assert.StartsWith("constant.numeric", ScopeOf("    val tries = 42", "42"));
        Assert.StartsWith("keyword", ScopeOf("import kotlinx.coroutines.sync.Mutex", "import"));
    }
}
