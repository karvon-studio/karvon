using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Karvon.Browser;

/// <summary>
/// A step Claude took in Karvon's browser, as a test takes it again: a control by its role and accessible name (what
/// the outline shows), as Playwright's locators find it.
/// </summary>
/// <param name="Kind">goto, back, forward, reload, click, dblclick, fill, select, press, expect, login.</param>
/// <param name="Text">The address (goto), the text (fill), the key (press), the text shown (expect), the name signed in as (login).</param>
public sealed record BrowserStep(string Kind, string? Role = null, string? Name = null, string? Text = null, IReadOnlyList<string>? Values = null)
{
    /// <summary>For login: the site, whose password the test reads from an environment variable.</summary>
    public string? Site { get; init; }
}

/// <summary>
/// The steps as a Playwright test: TypeScript or JavaScript (@playwright/test), Python (pytest-playwright) or C#
/// (Microsoft.Playwright.NUnit), by the file's extension; a sign-in's password from an environment variable, never in
/// the test. A TypeScript, JavaScript or Python file that is there gets the test after what it has.
/// </summary>
public static class BrowserTestWriter
{
    static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Quote(string? text) => JsonSerializer.Serialize(text ?? "", Strings);

    /// <summary>The language of a test file, by its extension; null for one Karvon does not write.</summary>
    public static string? Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".mts" or ".cts" => "typescript",
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".py" => "python",
        ".cs" => "csharp",
        _ => null,
    };

    /// <summary>The environment variable a test reads a site's password from (GITHUB_COM_PASSWORD; HOST_127_0_0_1_5000_PASSWORD, a shell's names begin with a letter).</summary>
    public static string PasswordVariable(string? site)
    {
        var host = Uri.TryCreate(site, UriKind.Absolute, out var uri) ? uri.Host + (uri.IsDefaultPort ? "" : "_" + uri.Port) : site ?? "site";
        var name = string.Concat(host.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')) + "_PASSWORD";
        return char.IsAsciiLetter(name[0]) ? name : "HOST_" + name;
    }

    /// <summary>The file as it is to be: the test alone (with what it needs to import), or after what the file has.</summary>
    public static string Write(string path, string name, IReadOnlyList<BrowserStep> steps, string? existing)
    {
        var language = Language(path) ?? throw new InvalidOperationException($"Karvon writes Playwright tests as .ts, .js, .py or .cs files, not {Path.GetExtension(path)}.");
        if (language == "csharp" && existing is not null)
            throw new InvalidOperationException($"{Path.GetFileName(path)} exists: give a new .cs file for the test (a class of its own).");
        var test = language switch
        {
            "python" => Python(name, steps),
            "csharp" => CSharp(path, name, steps),
            _ => Script(name, steps),
        };
        if (existing is null)
            return Header(language) + test;
        return existing.TrimEnd() + "\n\n" + test;
    }

    static string Header(string language) => language switch
    {
        "python" => "import os\n\nfrom playwright.sync_api import Page, expect\n\n\n",
        "csharp" => "",
        _ => "import { test, expect } from '@playwright/test';\n\n",
    };

    static string Script(string name, IReadOnlyList<BrowserStep> steps)
    {
        var text = new StringBuilder($"test({Quote(name)}, async ({{ page }}) => {{\n");
        foreach (var step in steps)
        {
            var control = step.Name is { Length: > 0 } n ? $"page.getByRole({Quote(step.Role)}, {{ name: {Quote(n)} }})" : $"page.getByRole({Quote(step.Role)}).first()";
            text.Append("  ").AppendLine(step.Kind switch
            {
                "goto" => $"await page.goto({Quote(step.Text)});",
                "back" => "await page.goBack();",
                "forward" => "await page.goForward();",
                "reload" => "await page.reload();",
                "click" => $"await {control}.click();",
                "dblclick" => $"await {control}.dblclick();",
                "fill" => $"await {control}.fill({Quote(step.Text)});",
                "select" => $"await {control}.selectOption([{string.Join(", ", (step.Values ?? []).Select(Quote))}]);",
                "press" => $"await page.keyboard.press({Quote(step.Text)});",
                "expect" => $"await expect(page.getByText({Quote(step.Text)}).first()).toBeVisible();",
                "login" => Login(step, control, $"process.env[{Quote(PasswordVariable(step.Site))}] ?? ''", "page.locator('input[type=\"password\"]').first()", "fill", "press"),
                _ => $"// {step.Kind}",
            });
        }
        return text.Append("});\n").ToString();
    }

    /// <summary>A sign-in: the name typed, the password from the environment, sent with Enter.</summary>
    static string Login(BrowserStep step, string control, string password, string passwordField, string fill, string press) =>
        $"// The sign-in Karvon keeps for {step.Site}: its password from {PasswordVariable(step.Site)}, never in the test.\n  "
        + (step.Role is not null ? $"await {control}.{fill}({Quote(step.Text)});\n  " : "")
        + $"await {passwordField}.{fill}({password});\n  await {passwordField}.{press}('Enter');";

    static string Python(string name, IReadOnlyList<BrowserStep> steps)
    {
        var function = "test_" + string.Concat(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')).Trim('_');
        var text = new StringBuilder($"def {function}(page: Page):\n");
        foreach (var step in steps)
        {
            var control = step.Name is { Length: > 0 } n ? $"page.get_by_role({Quote(step.Role)}, name={Quote(n)})" : $"page.get_by_role({Quote(step.Role)}).first";
            var password = "page.locator('input[type=\"password\"]').first";
            text.Append("    ").AppendLine(step.Kind switch
            {
                "goto" => $"page.goto({Quote(step.Text)})",
                "back" => "page.go_back()",
                "forward" => "page.go_forward()",
                "reload" => "page.reload()",
                "click" => $"{control}.click()",
                "dblclick" => $"{control}.dblclick()",
                "fill" => $"{control}.fill({Quote(step.Text)})",
                "select" => $"{control}.select_option([{string.Join(", ", (step.Values ?? []).Select(Quote))}])",
                "press" => $"page.keyboard.press({Quote(step.Text)})",
                "expect" => $"expect(page.get_by_text({Quote(step.Text)}).first).to_be_visible()",
                "login" => $"# The sign-in Karvon keeps for {step.Site}: its password from {PasswordVariable(step.Site)}, never in the test.\n    "
                    + (step.Role is not null ? $"{control}.fill({Quote(step.Text)})\n    " : "")
                    + $"{password}.fill(os.environ.get({Quote(PasswordVariable(step.Site))}, \"\"))\n    {password}.press(\"Enter\")",
                _ => $"# {step.Kind}",
            });
        }
        return text.ToString();
    }

    static string Pascal(string text) =>
        string.Concat(text.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsAsciiLetterOrDigit)]))
            .Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    static string CSharp(string path, string name, IReadOnlyList<BrowserStep> steps)
    {
        var className = Pascal(Path.GetFileNameWithoutExtension(path)) is { Length: > 0 } c && !char.IsDigit(c[0]) ? c : "BrowserTests";
        var method = Pascal(name) is { Length: > 0 } m && !char.IsDigit(m[0]) ? m : "Flow";
        var text = new StringBuilder($$"""
            using Microsoft.Playwright;
            using Microsoft.Playwright.NUnit;
            using NUnit.Framework;

            [Parallelizable(ParallelScope.Self)]
            [TestFixture]
            public class {{className}} : PageTest
            {
                [Test]
                public async Task {{method}}()
                {

            """);
        foreach (var step in steps)
        {
            var role = step.Role is { Length: > 0 } r ? "AriaRole." + char.ToUpperInvariant(r[0]) + r[1..] : "AriaRole.Generic";
            var control = step.Name is { Length: > 0 } n ? $"Page.GetByRole({role}, new() {{ Name = {Quote(n)} }})" : $"Page.GetByRole({role}).First";
            var password = "Page.Locator(\"input[type=\\\"password\\\"]\").First";
            text.Append("        ").AppendLine(step.Kind switch
            {
                "goto" => $"await Page.GotoAsync({Quote(step.Text)});",
                "back" => "await Page.GoBackAsync();",
                "forward" => "await Page.GoForwardAsync();",
                "reload" => "await Page.ReloadAsync();",
                "click" => $"await {control}.ClickAsync();",
                "dblclick" => $"await {control}.DblClickAsync();",
                "fill" => $"await {control}.FillAsync({Quote(step.Text)});",
                "select" => $"await {control}.SelectOptionAsync(new[] {{ {string.Join(", ", (step.Values ?? []).Select(Quote))} }});",
                "press" => $"await Page.Keyboard.PressAsync({Quote(step.Text)});",
                "expect" => $"await Expect(Page.GetByText({Quote(step.Text)}).First).ToBeVisibleAsync();",
                "login" => $"// The sign-in Karvon keeps for {step.Site}: its password from {PasswordVariable(step.Site)}, never in the test.\n        "
                    + (step.Role is not null ? $"await {control}.FillAsync({Quote(step.Text)});\n        " : "")
                    + $"await {password}.FillAsync(Environment.GetEnvironmentVariable({Quote(PasswordVariable(step.Site))}) ?? \"\");\n        await {password}.PressAsync(\"Enter\");",
                _ => $"// {step.Kind}",
            });
        }
        return text.Append("    }\n}\n").ToString();
    }
}
