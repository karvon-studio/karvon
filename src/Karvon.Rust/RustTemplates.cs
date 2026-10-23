using Karvon.Templates;

namespace Karvon.Rust;

/// <summary>Rust's new projects: a Cargo package with a binary or a library, made by cargo init.</summary>
public static class RustTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        Cargo("karvon.rust.bin", "Cargo package", "A Rust program: its Cargo.toml and src/main.rs (cargo init).", "rustapp", "--bin", ["Console"]),
        Cargo("karvon.rust.lib", "Cargo library", "A Rust library: its Cargo.toml and src/lib.rs, with a test (cargo init --lib).", "rustlib", "--lib", ["Library"]),
    ];

    static ProjectTemplate Cargo(string identity, string name, string description, string defaultName, string kind, IReadOnlyList<string> tags) =>
        ProjectTemplate.OfModule(identity, name, "Rust", description, defaultName, tags, async (request, log, ct) =>
        {
            var dir = request.ProjectDirectory;
            await TemplateSteps.RunAsync("cargo", ["init", kind, "--vcs", "none", "--name", TemplateSteps.PackageName(request.ProjectName, '_')], dir,
                RustTools.PathWithCargo(Environment.GetEnvironmentVariable("PATH") ?? ""), log, ct);
            return dir;
        });
}
