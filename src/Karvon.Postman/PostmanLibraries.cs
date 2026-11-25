using System.Collections.Concurrent;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Json;

namespace Karvon.Postman;

/// <summary>What a script's require gives: the libraries in Sandbox, each read and prepared once and run in the script's own interpreter.</summary>
static class PostmanLibraries
{
    const string Root = "Karvon.Postman.Sandbox/";

    static readonly Dictionary<string, string> Files = typeof(PostmanLibraries).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Root, StringComparison.Ordinal)).ToDictionary(n => n[Root.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    static readonly ConcurrentDictionary<string, Prepared<Script>> Scripts = new(StringComparer.Ordinal);

    static string Read(string path)
    {
        using var stream = typeof(PostmanLibraries).Assembly.GetManifestResourceStream(Files[path])!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>A module's function (CommonJS's: exports, require, module, global, process), or a JSON file's value; null when there is none.</summary>
    public static JsValue Load(Engine engine, string path)
    {
        if (!Files.ContainsKey(path))
            return JsValue.Null;
        if (path.EndsWith(".json", StringComparison.Ordinal))
            return new JsonParser(engine).Parse(Read(path));
        return engine.Evaluate(Scripts.GetOrAdd(path, p => Engine.PrepareScript("(function (exports, require, module, global, process) {" + Read(p) + "\n})", p)));
    }
}
