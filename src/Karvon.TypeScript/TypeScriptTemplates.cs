using Karvon.Templates;

namespace Karvon.TypeScript;

/// <summary>TypeScript's new project: a Node.js program, built by tsc, tested by Vitest (npm installs them).</summary>
public static class TypeScriptTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.typescript.node", "TypeScript (Node.js)", "TypeScript", "A Node.js program in TypeScript: built by tsc, tested by Vitest.", "tsapp", ["Console"],
            async (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                TemplateSteps.Write(dir, "package.json", $$"""
                    {
                      "name": "{{TemplateSteps.PackageName(request.ProjectName, '-')}}",
                      "version": "0.1.0",
                      "private": true,
                      "type": "module",
                      "scripts": {
                        "build": "tsc",
                        "start": "node dist/main.js",
                        "test": "vitest run"
                      }
                    }

                    """);
                TemplateSteps.Write(dir, "tsconfig.json", """
                    {
                      "compilerOptions": {
                        "target": "es2022",
                        "module": "nodenext",
                        "strict": true,
                        "outDir": "dist",
                        "rootDir": "src",
                        "sourceMap": true
                      },
                      "include": ["src"],
                      "exclude": ["src/**/*.test.ts"]
                    }

                    """);
                TemplateSteps.Write(dir, "src/main.ts", """
                    export function greeting(): string {
                      return "Hello, world!";
                    }

                    console.log(greeting());

                    """);
                TemplateSteps.Write(dir, "src/main.test.ts", """
                    import { expect, it } from "vitest";
                    import { greeting } from "./main.js";

                    it("greets", () => {
                      expect(greeting()).toBe("Hello, world!");
                    });

                    """);
                TemplateSteps.Write(dir, ".gitignore", "node_modules/\ndist/\n");
                var path = string.Join(System.IO.Path.PathSeparator, Build.NodeLocator.DirectoryOffPath() is { } node ? [node, Environment.GetEnvironmentVariable("PATH") ?? ""] : new[] { Environment.GetEnvironmentVariable("PATH") ?? "" });
                await TemplateSteps.RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["install", "--save-dev", "--no-audit", "--no-fund", "typescript", "@types/node", "vitest"], dir, path, log, ct, required: false);
                return dir;
            }),
    ];
}
