using Karvon.Templates;

namespace Karvon.Kotlin;

/// <summary>
/// Kotlin's new project: a Gradle application (Kotlin's JVM plugin, a JUnit 5 test) with Gradle's wrapper, which
/// kotlin-lsp imports and runs it with (Gradle itself is not needed to make it: the wrapper downloads its distribution).
/// </summary>
public static class KotlinTemplates
{
    public const string GradleVersion = "9.1.0";

    /// <summary>The wrapper's jar of Gradle 9.1.0, as Gradle publishes its checksum (gradle-9.1.0-wrapper.jar.sha256).</summary>
    public const string WrapperJarSha256 = "76805e32c009c0cf0dd5d206bddc9fb22ea42e84db904b764f3047de095493f3";

    /// <summary>Gradle's wrapper, as Gradle makes it: gradlew (runnable), gradlew.bat and its jar.</summary>
    internal static void WriteWrapper(string dir)
    {
        foreach (var (name, into) in new[] { ("gradlew", "gradlew"), ("gradlew.bat", "gradlew.bat"), ("gradle-wrapper.jar", "gradle/wrapper/gradle-wrapper.jar") })
        {
            using var resource = typeof(KotlinTemplates).Assembly.GetManifestResourceStream("GradleWrapper/" + name)!;
            var path = Path.Combine(dir, into);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = File.Create(path))
                resource.CopyTo(file);
            if (name == "gradlew" && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    public const string KotlinVersion = "2.2.20";

    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.kotlin.gradle", "Kotlin application (Gradle)", "Kotlin", "A Kotlin program built by Gradle, with a JUnit 5 test.", "kotlinapp", ["Console"],
            (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                TemplateSteps.Write(dir, "settings.gradle.kts", $"""
                    rootProject.name = "{TemplateSteps.PackageName(request.ProjectName, '-')}"

                    """);
                TemplateSteps.Write(dir, "build.gradle.kts", $$"""
                    plugins {
                        kotlin("jvm") version "{{KotlinVersion}}"
                        application
                    }

                    repositories {
                        mavenCentral()
                    }

                    dependencies {
                        testImplementation(kotlin("test"))
                    }

                    application {
                        mainClass = "MainKt"
                    }

                    tasks.test {
                        useJUnitPlatform()
                    }

                    """);
                TemplateSteps.Write(dir, "gradle/wrapper/gradle-wrapper.properties", $"""
                    distributionBase=GRADLE_USER_HOME
                    distributionPath=wrapper/dists
                    distributionUrl=https\://services.gradle.org/distributions/gradle-{GradleVersion}-bin.zip
                    networkTimeout=10000
                    validateDistributionUrl=true
                    zipStoreBase=GRADLE_USER_HOME
                    zipStorePath=wrapper/dists

                    """);
                WriteWrapper(dir);
                TemplateSteps.Write(dir, "src/main/kotlin/Main.kt", """
                    fun greeting(): String = "Hello, world!"

                    fun main() {
                        println(greeting())
                    }

                    """);
                TemplateSteps.Write(dir, "src/test/kotlin/MainTest.kt", """
                    import kotlin.test.Test
                    import kotlin.test.assertEquals

                    class MainTest {
                        @Test
                        fun greets() {
                            assertEquals("Hello, world!", greeting())
                        }
                    }

                    """);
                TemplateSteps.Write(dir, ".gitignore", ".gradle/\nbuild/\n.kotlin/\n");
                log("Kotlin application (Gradle): build.gradle.kts, Gradle's wrapper, Main.kt and its test.");
                return Task.FromResult(dir);
            }),
    ];
}
