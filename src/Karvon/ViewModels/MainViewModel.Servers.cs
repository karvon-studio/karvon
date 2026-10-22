using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Servers.ViewModels;

namespace Karvon.ViewModels;

/// <summary>
/// View > Servers: the machines of ~/.ssh/config and those added in Karvon (see <see cref="ServersExplorerViewModel"/>),
/// made the first time the tool window shows. A machine's page is a document; its files open in the editor, and saving
/// one puts it back on the machine.
/// </summary>
public partial class MainViewModel : IServersHost
{
    /// <summary>The explorer, once the tool window has shown (no ~/.ssh/config is read before).</summary>
    [ObservableProperty]
    public partial ServersExplorerViewModel? Servers { get; private set; }

    public ServersExplorerViewModel EnsureServers() => Servers ??= new ServersExplorerViewModel(this, CredentialStores.System);

    [RelayCommand]
    void ShowServers()
    {
        EnsureServers();
        ShowToolWindow(ToolWindows.Servers);
    }

    /// <summary>Karvon closes: the connections end.</summary>
    public void ShutdownServers()
    {
        Servers?.Dispose();
        Servers = null;
    }

    Task OnServerFileSavedAsync(string path) => Servers?.OnFileSavedAsync(path) ?? Task.CompletedTask;

    string IServersHost.ConfigDirectory => Karvon.Services.Settings.ConfigDirectory;

    IEnumerable<ServerDocument> IServersHost.Documents => Documents.OfType<ServerDocument>();

    void IServersHost.ShowDocument(ServerDocument document)
    {
        if (!Documents.Contains(document))
            Documents.Add(document);
        ActiveDocument = document;
    }

    void IServersHost.CloseDocument(ServerDocument document)
    {
        if (Documents.Contains(document))
            _ = CloseDocument(document);
    }

    void IServersHost.OpenFile(string localPath) => OpenDocument(localPath);

    Task<IReadOnlyList<string>> IServersHost.PickFilesAsync(string title) => Dialogs?.PickFilesAsync(title) ?? Task.FromResult<IReadOnlyList<string>>([]);

    void IServersHost.AskClaude(string prompt) => ((Docker.ViewModels.IDockerHost)this).AskClaude(prompt);

    void IServersHost.Status(string text) => SetStatus(text);
}
