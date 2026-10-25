using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Http.ViewModels;

/// <summary>What the Endpoints Explorer needs of Karvon.</summary>
public interface IHttpHost
{
    /// <summary>The open solution's (or folder's) directory; null when none is open.</summary>
    string? Workspace { get; }

    void OpenFile(string path, int line);

    void Status(string text);
}

public sealed record EndpointRow(HttpEndpoint Endpoint, string Root)
{
    public string Method => Endpoint.Method;

    public string Path => Endpoint.Path;

    public string Where => $"{System.IO.Path.GetRelativePath(Root, Endpoint.File).Replace('\\', '/')}:{Endpoint.Line}";
}

public sealed partial class EndpointGroup(string project) : ObservableObject
{
    public string Project => project;

    public string Title => Path.GetFileNameWithoutExtension(project);

    public ObservableCollection<EndpointRow> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;
}

/// <summary>
/// Visual Studio's Endpoints Explorer: the solution's ASP.NET Core endpoints by project (controllers', minimal APIs',
/// pages'), read from the sources (again when a source is saved); one opens where it is written, or becomes a request in
/// the project's .http file (its host address from launchSettings.json).
/// </summary>
public sealed partial class EndpointsViewModel(IHttpHost host) : ObservableObject
{
    IReadOnlyList<HttpEndpoint> _all = [];

    public ObservableCollection<EndpointGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => Show();

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public IReadOnlyList<HttpEndpoint> Endpoints => _all;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (host.Workspace is not { } root)
        {
            _all = [];
            Show();
            Message = "Open a solution or a folder to see its endpoints.";
            return;
        }
        IsBusy = true;
        try
        {
            _all = await Task.Run(() => EndpointScanner.Scan(root));
            Show();
            Message = _all.Count == 0 ? "No ASP.NET Core endpoints in the solution: controllers' actions, MapGet and the like, and @page are listed." : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A source saved: the endpoints read again.</summary>
    public void FileSaved(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml")
            _ = RefreshAsync();
    }

    void Show()
    {
        var root = host.Workspace ?? "";
        var filter = Filter.Trim();
        Groups.Clear();
        foreach (var project in _all.Where(e => filter.Length == 0 || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || e.Method.Equals(filter, StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.Project))
        {
            var group = new EndpointGroup(project.Key);
            foreach (var e in project)
                group.Items.Add(new EndpointRow(e, root));
            Groups.Add(group);
        }
    }

    [RelayCommand]
    void Open(EndpointRow? row)
    {
        if (row is not null)
            host.OpenFile(row.Endpoint.File, row.Endpoint.Line);
    }

    [RelayCommand]
    void GenerateRequest(EndpointRow? row)
    {
        if (row is not null)
            AddRequest(row);
    }

    /// <summary>The endpoint as a request at the end of its project's .http file (made with the host address first, when new), opened there.</summary>
    public string AddRequest(EndpointRow row)
    {
        var e = row.Endpoint;
        var name = Path.GetFileNameWithoutExtension(e.Project);
        var folder = File.Exists(e.Project) ? Path.GetDirectoryName(e.Project)! : e.Project;
        var file = Path.Combine(folder, name + ".http");
        var variable = Regex.Replace(name, @"\W", "_") + "_HostAddress";
        var text = new StringBuilder();
        if (!File.Exists(file))
            text.Append($"@{variable} = {EndpointScanner.HostAddress(e.Project) ?? "http://localhost:5000"}\n");
        else if (!File.ReadAllText(file).EndsWith('\n'))
            text.Append('\n');
        text.Append($"\n### {e.Method} {e.Path}\n\n");
        var line = (File.Exists(file) ? File.ReadAllLines(file).Length : 0) + text.ToString().Count(c => c == '\n') + 1;
        text.Append($"{e.Method} {{{{{variable}}}}}{e.Path}\nAccept: application/json\n");
        if (e.Method is "POST" or "PUT" or "PATCH")
            text.Append("Content-Type: application/json\n\n{\n}\n");
        File.AppendAllText(file, text.ToString());
        host.OpenFile(file, line);
        host.Status($"{e.Method} {e.Path} added to {Path.GetFileName(file)}.");
        return file;
    }
}
