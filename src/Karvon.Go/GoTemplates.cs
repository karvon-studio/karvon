using Karvon.Templates;

namespace Karvon.Go;

/// <summary>Go's new project: a module with its main package, made by go mod init.</summary>
public static class GoTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.go.module", "Go module", "Go", "A Go program: its module (go mod init) and its main package.", "goapp", ["Console"],
            async (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                await TemplateSteps.RunAsync("go", ["mod", "init", TemplateSteps.PackageName(request.ProjectName, '-')], dir,
                    GoTools.PathWithGo(Environment.GetEnvironmentVariable("PATH") ?? ""), log, ct);
                TemplateSteps.Write(dir, "main.go", """
                    package main

                    import "fmt"

                    func main() {
                    	fmt.Println("Hello, world!")
                    }

                    """);
                return dir;
            }),
    ];
}
