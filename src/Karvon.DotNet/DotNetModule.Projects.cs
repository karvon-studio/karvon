using System.Xml.Linq;
using Karvon.Build;
using Karvon.Services;
using Karvon.Workspace;

namespace Karvon.DotNet;

/// <summary>
/// What a .NET project needs to run on this machine: the ASP.NET Core HTTPS development certificate trusted, as Visual
/// Studio offers on the first HTTPS run, and its user secrets (secrets.json, outside the solution).
/// </summary>
public sealed partial class DotNetModule
{
    bool _devCertificateOffered;

    /// <summary>The certificate check and trust commands (replaced in tests so they never touch the real trust store).</summary>
    public Func<CancellationToken, Task<DevCertificateTrust>> CheckDevCertificate { get; set; } = DevCertificates.CheckAsync;

    public Func<Action<string>, CancellationToken, Task<bool>> TrustDevCertificateTool { get; set; } = DevCertificates.TrustAsync;

    Karvon.ViewModels.OutputChannel DebugOutput => Workbench.Output("Debug");

    /// <summary>
    /// Before running a profile that listens on https: if the development certificate is not trusted, ask once per
    /// session whether to trust it (VS: "Trust the ASP.NET Core SSL certificate?"). Trusted for .NET but not by every
    /// browser (Partial) is as far as trusting goes on many Linux desktops, so that is not asked again on every start.
    /// </summary>
    internal async Task OfferDevCertificateTrustAsync(LaunchProfile? profile)
    {
        if (_devCertificateOffered || profile?.ApplicationUrl?.Contains("https://", StringComparison.OrdinalIgnoreCase) != true)
            return;
        _devCertificateOffered = true;
        var trust = await CheckDevCertificate(CancellationToken.None);
        if (trust is DevCertificateTrust.Trusted or DevCertificateTrust.Partial or DevCertificateTrust.Unknown)
            return;
        var answer = await Workbench.ConfirmAsync("Trust the ASP.NET Core SSL certificate?",
            "This project is configured to use SSL. To avoid certificate warnings in the browser and from Kestrel, you can trust "
            + "the self-signed development certificate that ASP.NET Core created.\n\nWould you like to trust the ASP.NET Core SSL certificate?\n\n"
            + "(Karvon runs: dotnet dev-certs https --trust)");
        if (answer == true)
            await TrustDevCertificateAsync();
        else
            DebugOutput.Append("The ASP.NET Core development certificate is not trusted. Tools > Trust ASP.NET Core Development Certificate trusts it later.");
    }

    /// <summary>Tools > Trust ASP.NET Core Development Certificate.</summary>
    internal async Task TrustDevCertificateAsync()
    {
        var output = DebugOutput;
        Workbench.ShowOutput(output);
        Workbench.Status("Trusting the ASP.NET Core development certificate...");
        var ok = await TrustDevCertificateTool(output.Append, CancellationToken.None);
        var trust = ok ? await CheckDevCertificate(CancellationToken.None) : DevCertificateTrust.Unknown;
        var message = trust switch
        {
            DevCertificateTrust.Trusted => "The ASP.NET Core development certificate is trusted. Restart browsers that were open to pick it up.",
            DevCertificateTrust.Partial => "The development certificate is trusted for .NET; a browser may still warn (install libnss3-tools so certutil can add it to Chrome and Firefox).",
            _ => "Trusting the development certificate did not complete; see the Output window.",
        };
        output.Append(message);
        Workbench.Status(message);
    }

    /// <summary>
    /// Manage User Secrets: the project's secrets.json (in ~/.microsoft/usersecrets, outside the solution), opened; a
    /// project without a UserSecretsId gets one first (dotnet user-secrets init), as VS does.
    /// </summary>
    internal async Task<string?> ManageUserSecretsAsync(ProjectInfo project)
    {
        var id = UserSecretsId(project.FilePath);
        if (id is null)
        {
            var output = Workbench.Output("Build");
            output.Append($"> dotnet user-secrets init --project {project.FilePath}");
            var last = "";
            using var runner = ProcessRunner.Start(DotnetCli.Executable, ["user-secrets", "init", "--project", project.FilePath], project.Directory, line =>
            {
                output.Append(line);
                if (line.Trim().Length > 0)
                    last = line.Trim();
            });
            if (await runner.WaitAsync() == 0)
                id = UserSecretsId(project.FilePath);
            else
                Workbench.Status(last.Length > 0 ? last : "dotnet user-secrets init failed.");
        }
        if (id is null)
            return null;
        var file = Path.Combine(UserHome.Path, ".microsoft", "usersecrets", id, "secrets.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file))
                File.WriteAllText(file, "{\n}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Status(ex.Message);
            return null;
        }
        Workbench.OpenFile(file, 0);
        return file;
    }

    static string? UserSecretsId(string projectFile)
    {
        try
        {
            return XDocument.Load(projectFile).Descendants().FirstOrDefault(e => e.Name.LocalName == "UserSecretsId")?.Value.Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }
}
