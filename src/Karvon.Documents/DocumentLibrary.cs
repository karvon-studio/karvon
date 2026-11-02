using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Karvon.Documents;

/// <summary>One engine's MCP server: a JSON-RPC message in, its answer out (none for a notification).</summary>
public interface IDocumentServer : IDisposable
{
    Task<string?> HandleAsync(string message, CancellationToken cancellationToken);
}

/// <summary>Opens a server of a kind (pdf, word, excel, slides).</summary>
public interface IDocumentEngines
{
    Task<IDocumentServer> OpenAsync(string kind, CancellationToken cancellationToken);
}

/// <summary>
/// The document engines' library (karvon-documents: PdfCraft, WordCraft, GridCraft and DeckCraft's MCP servers, headless)
/// loaded into Karvon's process: each server opened in it and called from one thread of its own, one call at a time, as
/// the library asks.
/// </summary>
public sealed class DocumentLibrary : IDocumentEngines, IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr OpenFn([MarshalAs(UnmanagedType.LPUTF8Str)] string kind);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr HandleFn(IntPtr server, [MarshalAs(UnmanagedType.LPUTF8Str)] string line);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void FreeFn(IntPtr pointer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr VersionFn();

    readonly IntPtr _library;
    readonly OpenFn _open;
    readonly HandleFn _handle;
    readonly FreeFn _close;
    readonly FreeFn _free;
    readonly BlockingCollection<Action> _calls = [];
    readonly Thread _thread;

    /// <summary>The library's version, as it says it.</summary>
    public string Version { get; }

    public DocumentLibrary(string path)
    {
        _library = NativeLibrary.Load(path);
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        _open = Export<OpenFn>("karvon_documents_open");
        _handle = Export<HandleFn>("karvon_documents_handle");
        _close = Export<FreeFn>("karvon_documents_close");
        _free = Export<FreeFn>("karvon_documents_free");
        Version = Marshal.PtrToStringUTF8(Export<VersionFn>("karvon_documents_version")()) ?? "";
        // The engines' work (a large PDF's text, a page drawn) wants a stack bigger than a thread pool thread's.
        _thread = new Thread(() =>
        {
            foreach (var call in _calls.GetConsumingEnumerable())
                call();
        }, 64 << 20) { IsBackground = true, Name = "Karvon documents" };
        _thread.Start();
    }

    /// <summary>The library's file name on this platform.</summary>
    public static string FileName =>
        OperatingSystem.IsWindows() ? "karvon_documents.dll" : OperatingSystem.IsMacOS() ? "libkarvon_documents.dylib" : "libkarvon_documents.so";

    Task<T> Run<T>(Func<T> call)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls.Add(() =>
        {
            try
            {
                done.SetResult(call());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    public async Task<IDocumentServer> OpenAsync(string kind, CancellationToken cancellationToken)
    {
        var server = await Run(() => _open(kind));
        return server == IntPtr.Zero ? throw new InvalidOperationException($"The document engines have no {kind} documents.") : new Server(this, server);
    }

    sealed class Server(DocumentLibrary library, IntPtr handle) : IDocumentServer
    {
        IntPtr _handle = handle;

        public async Task<string?> HandleAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A call that began runs to its end (the engine cannot be stopped half way); the caller stops waiting for it.
            return await library.Run(() =>
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                var answer = library._handle(_handle, message);
                if (answer == IntPtr.Zero)
                    return null;
                try
                {
                    return Marshal.PtrToStringUTF8(answer);
                }
                finally
                {
                    library._free(answer);
                }
            }).WaitAsync(cancellationToken);
        }

        public void Dispose() => library._calls.Add(() =>
        {
            if (_handle != IntPtr.Zero)
                library._close(_handle);
            _handle = IntPtr.Zero;
        });
    }

    /// <summary>The thread ends; the library stays loaded (a server's memory may still be in use as it ends).</summary>
    public void Dispose() => _calls.CompleteAdding();
}
