using Karvon.Templates;

namespace Karvon.Python;

/// <summary>Python's new project: its main.py and a pytest test, in a virtual environment of its own (.venv) with pytest in it.</summary>
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
    ];
}
