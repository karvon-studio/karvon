using Karvon.Templates;

namespace Karvon.Java;

/// <summary>Java's new project: Maven's, with a JUnit test (Maven is not needed to make it, only to build it).</summary>
public static class JavaTemplates
{
    public static IReadOnlyList<ProjectTemplate> All =>
    [
        ProjectTemplate.OfModule("karvon.java.maven", "Java project (Maven)", "Java", "A Java 21 program built by Maven, with a JUnit 5 test.", "javaapp", ["Console"],
            (request, log, ct) =>
            {
                var dir = request.ProjectDirectory;
                var artifact = TemplateSteps.PackageName(request.ProjectName, '-');
                var package = "com.example." + TemplateSteps.PackageName(request.ProjectName, '_');
                var folder = package.Replace('.', '/');
                TemplateSteps.Write(dir, "pom.xml", $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <project xmlns="http://maven.apache.org/POM/4.0.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                             xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
                      <modelVersion>4.0.0</modelVersion>
                      <groupId>com.example</groupId>
                      <artifactId>{artifact}</artifactId>
                      <version>0.1.0</version>
                      <properties>
                        <maven.compiler.release>21</maven.compiler.release>
                        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
                      </properties>
                      <dependencies>
                        <dependency>
                          <groupId>org.junit.jupiter</groupId>
                          <artifactId>junit-jupiter</artifactId>
                          <version>5.11.4</version>
                          <scope>test</scope>
                        </dependency>
                      </dependencies>
                      <build>
                        <plugins>
                          <plugin>
                            <groupId>org.apache.maven.plugins</groupId>
                            <artifactId>maven-surefire-plugin</artifactId>
                            <version>3.5.2</version>
                          </plugin>
                        </plugins>
                      </build>
                    </project>

                    """);
                TemplateSteps.Write(dir, $"src/main/java/{folder}/App.java", $$"""
                    package {{package}};

                    public class App {
                        static String greeting() {
                            return "Hello, world!";
                        }

                        public static void main(String[] args) {
                            System.out.println(greeting());
                        }
                    }

                    """);
                TemplateSteps.Write(dir, $"src/test/java/{folder}/AppTest.java", $$"""
                    package {{package}};

                    import static org.junit.jupiter.api.Assertions.assertEquals;

                    import org.junit.jupiter.api.Test;

                    class AppTest {
                        @Test
                        void greets() {
                            assertEquals("Hello, world!", App.greeting());
                        }
                    }

                    """);
                TemplateSteps.Write(dir, ".gitignore", "target/\n");
                log("Java project (Maven): pom.xml, App.java and its test.");
                return Task.FromResult(dir);
            }),
    ];
}
