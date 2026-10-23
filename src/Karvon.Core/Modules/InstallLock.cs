using System.Collections.Concurrent;

namespace Karvon.Modules;

/// <summary>
/// One install of a component at a time, into its folder: in this Karvon Studio (two solutions, two tests starting the
/// same language server), and between them (a lock file beside the folder, which the next one waits for). The one that
/// waited finds it installed.
/// </summary>
public static class InstallLock
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcess = new(StringComparer.Ordinal);

    public static async Task<IAsyncDisposable> AcquireAsync(string folder, CancellationToken ct)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var gate = InProcess.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var path = full + ".lock";
            while (true)
            {
                try
                {
                    var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                    return new Held(gate, file);
                }
                catch (IOException)
                {
                    // Another Karvon Studio installs it now.
                    await Task.Delay(500, ct);
                }
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    sealed class Held(SemaphoreSlim gate, FileStream file) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await file.DisposeAsync();
            gate.Release();
        }
    }
}
