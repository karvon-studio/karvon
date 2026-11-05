using Karvon.Templates;

namespace Karvon.Jwc;

/// <summary>JWC's new projects: jwc new's templates.</summary>
public static class JwcTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        New("empty", "JWC service", "One route, one schema and no tables: the smallest thing that runs (jwc new).", "jwcapp"),
        New("api", "JWC REST API", "CRUD over one table: its DTOs, a service, five routes and keyset paging (jwc new --template api).", "jwcapi"),
        New("auth", "JWC service with accounts", "Accounts, Argon2id passwords and JWT sessions (jwc new --template auth).", "jwcauth"),
        New("jobs", "JWC service with background jobs", "A background job, where it is dispatched and its durable queue (jwc new --template jobs).", "jwcjobs"),
    ];

    static ProjectTemplate New(string template, string name, string description, string defaultName) =>
        ProjectTemplate.OfModule("karvon.jwc." + template, name, "JWC", description, defaultName, ["Web", "API"], async (request, log, ct) =>
        {
            var dir = request.ProjectDirectory;
            await TemplateSteps.RunAsync("jwc", ["new", TemplateSteps.PackageName(request.ProjectName, '-'), "--template", template, "--path", dir], dir,
                JwcTools.PathWithJwc(Environment.GetEnvironmentVariable("PATH") ?? ""), log, ct);
            return dir;
        });
}
