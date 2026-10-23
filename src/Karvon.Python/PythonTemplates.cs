using Karvon.Templates;

namespace Karvon.Python;

/// <summary>
/// Python's new projects: its main.py and a pytest test, in a virtual environment of its own (.venv) with pytest in it;
/// or a uv project (uv init), pytest a dev dependency of it, its .venv uv's.
/// </summary>
public static class PythonTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.python.project", "Python project", "Python", "A Python program with a pytest test, and its own .venv with pytest in it.", "pyapp", ["Console"],
            async (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                var name = TemplateSteps.PackageName(request.ProjectName, '_');
                TemplateSteps.Write(dir, "pyproject.toml", $"""
                    [project]
                    name = "{name}"
                    version = "0.1.0"
                    requires-python = ">=3.10"

                    [tool.pytest.ini_options]
                    testpaths = ["tests"]
                    pythonpath = ["."]

                    """);
                TemplateSteps.Write(dir, "main.py", """
                    def greeting() -> str:
                        return "Hello, world!"


                    if __name__ == "__main__":
                        print(greeting())

                    """);
                TemplateSteps.Write(dir, "tests/test_main.py", """
                    from main import greeting


                    def test_greets():
                        assert greeting() == "Hello, world!"

                    """);
                TemplateSteps.Write(dir, ".gitignore", ".venv/\n__pycache__/\n.pytest_cache/\n");
                // Its .venv, and pytest in it (it may be done by hand when there is no Python or no network).
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                var python = OperatingSystem.IsWindows() ? "python.exe" : "python3";
                if (await TemplateSteps.RunAsync(python, ["-m", "venv", ".venv"], dir, path, log, ct, required: false) && PythonEnvironment.Interpreter(dir, path) is { } venv)
                    await TemplateSteps.RunAsync(venv, ["-m", "pip", "install", "--quiet", "--disable-pip-version-check", "pytest"], dir, path, log, ct, required: false);
                return dir;
            }),
        ProjectTemplate.OfModule("karvon.python.uv", "Python project (uv)", "Python",
            "A uv project (uv init --package): its package with a script, a pytest test with pytest as a dev dependency, and the .venv uv makes. Needs uv.",
            "pyapp", ["Console"],
            async (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                var name = TemplateSteps.PackageName(request.ProjectName, '-');
                await TemplateSteps.RunAsync("uv", ["init", "--package", "--vcs", "none", "--name", name, "."], dir, path, log, ct);
                TemplateSteps.Write(dir, "tests/test_main.py", $$"""
                    from {{name.Replace('-', '_')}} import main


                    def test_main_greets(capsys):
                        main()
                        assert capsys.readouterr().out.startswith("Hello")

                    """);
                File.AppendAllText(Path.Combine(dir, "pyproject.toml"), """

                    [tool.pytest.ini_options]
                    testpaths = ["tests"]

                    """);
                // pytest, and the .venv with the package in it (it may be done by hand when there is no network: uv sync).
                await TemplateSteps.RunAsync("uv", ["add", "--dev", "pytest"], dir, path, log, ct, required: false);
                return dir;
            }),
    ];
}
