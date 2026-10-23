using Karvon.Templates;

namespace Karvon.Cpp;

/// <summary>C's and C++'s new projects: CMake's, with a program and a test of it (CTest).</summary>
public static class CppTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        CMake("karvon.cpp.cmake", "CMake project (C++)", "C++", "CXX", "main.cpp", """
            #include <iostream>

            int main() {
                std::cout << "Hello, world!" << std::endl;
                return 0;
            }

            """),
        CMake("karvon.c.cmake", "CMake project (C)", "C", "C", "main.c", """
            #include <stdio.h>

            int main(void) {
                printf("Hello, world!\n");
                return 0;
            }

            """),
    ];

    static ProjectTemplate CMake(string identity, string name, string language, string cmakeLanguage, string main, string source) =>
        ProjectTemplate.OfModule(identity, name, language, $"A {language} program built by CMake (with its compile commands for clangd), and a test of it.",
            cmakeLanguage == "CXX" ? "cppapp" : "capp", ["Console"], (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                var target = TemplateSteps.PackageName(request.ProjectName, '_');
                TemplateSteps.Write(dir, "CMakeLists.txt", $"""
                    cmake_minimum_required(VERSION 3.16)
                    project({target} {cmakeLanguage})

                    set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
                    {(cmakeLanguage == "CXX" ? "set(CMAKE_CXX_STANDARD 20)\n" : "")}
                    add_executable({target} src/{main})

                    enable_testing()
                    add_test(NAME runs COMMAND {target})

                    """);
                TemplateSteps.Write(dir, Path.Combine("src", main), source);
                TemplateSteps.Write(dir, ".gitignore", "build/\n.cache/\n");
                log($"{name}: CMakeLists.txt and src/{main}.");
                return Task.FromResult(dir);
            });
}
