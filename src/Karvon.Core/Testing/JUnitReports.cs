using System.Globalization;
using System.Xml.Linq;

namespace Karvon.Testing;

/// <summary>
/// JUnit's XML reports, the legacy format JUnit's console launcher, Gradle and Maven's Surefire write (TEST-*.xml): each
/// test's result by its id, "class#method"; a test a parameterized method runs as several rows failed when one of them did.
/// </summary>
public static class JUnitReports
{
    /// <summary>The results in the report files, with each test's class (its binary name) and method.</summary>
    public static IEnumerable<(string Class, string Method, TestItemResult Result)> Read(IEnumerable<string> reports)
    {
        var rows = new List<(string Id, string Class, string Method, TestState State, double Seconds, string? Message, string? Stack, string? Output)>();
        foreach (var report in reports)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(report);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }
            foreach (var testcase in doc.Descendants("testcase"))
            {
                var @class = (string?)testcase.Attribute("classname") ?? "";
                var name = (string?)testcase.Attribute("name") ?? "";
                // greets(), greets(int)[1]: the method's name.
                var method = name.IndexOf('(') is var paren and > 0 ? name[..paren] : name;
                var failure = testcase.Element("failure") ?? testcase.Element("error");
                var state = failure is not null ? TestState.Failed : testcase.Element("skipped") is not null ? TestState.Skipped : TestState.Passed;
                var seconds = double.TryParse((string?)testcase.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
                // The console launcher's own lines (unique-id, display-name) are no output of the test's.
                var output = string.Join('\n', ((string?)testcase.Element("system-out") ?? "").Split('\n')
                    .Where(l => !l.StartsWith("unique-id: ", StringComparison.Ordinal) && !l.StartsWith("display-name: ", StringComparison.Ordinal))).Trim();
                rows.Add(($"{@class}#{method}", @class, method, state, seconds, (string?)failure?.Attribute("message"), failure?.Value.Trim(), output.Length > 0 ? output : null));
            }
        }
        foreach (var test in rows.GroupBy(r => r.Id))
        {
            var failed = test.FirstOrDefault(r => r.State == TestState.Failed);
            var state = failed.Id is not null ? TestState.Failed : test.All(r => r.State == TestState.Skipped) ? TestState.Skipped : TestState.Passed;
            var output = string.Join('\n', test.Select(r => r.Output).OfType<string>());
            var first = test.First();
            yield return (first.Class, first.Method,
                new TestItemResult(test.Key, state, TimeSpan.FromSeconds(test.Sum(r => r.Seconds)), failed.Message, failed.Stack, output.Length > 0 ? output : null));
        }
    }
}
