using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Karvon.Postman.ViewModels;

/// <summary>A value of a sign-in, as its row: typed, or one of a few.</summary>
public sealed partial class PostmanAuthField : ObservableObject
{
    readonly Action _edited;
    readonly IReadOnlyList<(string Value, string Name)> _choices;

    internal PostmanAuthField(string key, string label, string hint, IReadOnlyList<(string Value, string Name)>? choices, Action edited)
    {
        (Key, Label, Hint, _choices, _edited) = (key, label, hint, choices ?? [], edited);
        Choices = [.. _choices.Select(c => c.Name)];
    }

    public string Key { get; }

    public string Label { get; }

    public string Hint { get; }

    public IReadOnlyList<string> Choices { get; }

    public bool IsChoice => _choices.Count > 0;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial int Index { get; set; }

    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>The value as a collection keeps it; none when it is as the first choice.</summary>
    public string Value => !IsChoice ? Text : Index > 0 && Index < _choices.Count ? _choices[Index].Value : "";

    /// <summary>The value as it is used (the first choice's too).</summary>
    public string Effective => IsChoice ? _choices[Math.Clamp(Index, 0, _choices.Count - 1)].Value : Text;

    internal void Load(string value)
    {
        Text = value;
        Index = Math.Max(0, _choices.ToList().FindIndex(c => c.Value == value));
    }

    partial void OnTextChanged(string value) => _edited();

    partial void OnIndexChanged(int value) => _edited();
}

/// <summary>The values of the sign-ins that are rows of fields: AWS's, a digest's, NTLM's, OAuth 2.0's.</summary>
public sealed class PostmanAuthFields
{
    readonly Action _edited;
    bool _loading;

    public PostmanAuthFields(Action edited)
    {
        _edited = edited;
        PostmanAuthField Field(string key, string label, string hint = "", IReadOnlyList<(string, string)>? choices = null) => new(key, label, hint, choices, OnEdited);
        All = new Dictionary<string, IReadOnlyList<PostmanAuthField>>
        {
            [PostmanAuth.Aws] =
            [
                Field("accessKey", "Access key", "{{awsAccessKey}}"), Field("secretKey", "Secret key", "{{awsSecretKey}}"), Field("region", "Region", "Found in the host, or us-east-1"),
                Field("service", "Service", "Found in the host, or execute-api"), Field("sessionToken", "Session token", "{{awsSessionToken}}"),
                Field("addAuthDataToQuery", "Add to", choices: [("false", "Headers"), ("true", "Query parameters")]),
            ],
            [PostmanAuth.Digest] =
            [
                Field("username", "Username"), Field("password", "Password", "{{password}}"),
                Field("algorithm", "Algorithm, when it is not the server's", choices: [("", "As the server says"), ("MD5", "MD5"), ("MD5-sess", "MD5-sess"), ("SHA-256", "SHA-256"), ("SHA-256-sess", "SHA-256-sess")]),
                Field("realm", "Realm", "The server's"), Field("nonce", "Nonce", "The server's: with it and the realm, signed at once"), Field("qop", "qop", "The server's"), Field("opaque", "Opaque", "The server's"),
            ],
            [PostmanAuth.Ntlm] = [Field("username", "Username"), Field("password", "Password", "{{password}}"), Field("domain", "Domain"), Field("workstation", "Workstation")],
            [PostmanAuth.OAuth2] =
            [
                Field("grant_type", "Grant type", choices: [.. PostmanOAuth.Grants]),
                Field("redirect_uri", "Callback URL", "Karvon's own, on this machine (http://127.0.0.1:…/callback)"),
                Field("authUrl", "Authorization URL", "https://id.example.com/authorize"), Field("accessTokenUrl", "Access token URL", "https://id.example.com/token"),
                Field("refreshTokenUrl", "Refresh token URL", "As the access token's"),
                Field("clientId", "Client ID", "{{clientId}}"), Field("clientSecret", "Client secret", "{{clientSecret}}"),
                Field("username", "Username"), Field("password", "Password", "{{password}}"),
                Field("challengeAlgorithm", "Code challenge", choices: [("S256", "SHA-256"), ("plain", "Plain")]),
                Field("scope", "Scope", "openid profile"), Field("state", "State", "Made for each sign-in"), Field("audience", "Audience"),
                Field("client_authentication", "Client authentication", choices: [("header", "As a Basic Auth header"), ("body", "In the body")]),
                Field("addTokenTo", "Add the token to", choices: [("header", "The request's headers"), ("queryParams", "Its URL")]), Field("headerPrefix", "Header prefix", "Bearer"),
                Field("accessToken", "Access token, instead of one asked for", "{{accessToken}}"),
            ],
        };
    }

    public IReadOnlyDictionary<string, IReadOnlyList<PostmanAuthField>> All { get; }

    public ObservableCollection<PostmanAuthField> Shown { get; } = [];

    void OnEdited()
    {
        if (_loading)
            return;
        Fit();
        _edited();
    }

    /// <summary>The fields a grant uses.</summary>
    void Fit()
    {
        var oauth = All[PostmanAuth.OAuth2].ToDictionary(f => f.Key);
        var grant = oauth["grant_type"].Effective;
        var browser = grant is PostmanOAuth.Code or PostmanOAuth.Pkce or PostmanOAuth.Implicit;
        foreach (var key in new[] { "authUrl", "redirect_uri", "state" })
            oauth[key].IsShown = browser;
        foreach (var key in new[] { "accessTokenUrl", "refreshTokenUrl", "clientSecret", "client_authentication" })
            oauth[key].IsShown = grant != PostmanOAuth.Implicit;
        oauth["challengeAlgorithm"].IsShown = grant == PostmanOAuth.Pkce;
        oauth["username"].IsShown = oauth["password"].IsShown = grant == PostmanOAuth.PasswordGrant;
    }

    /// <summary>A sign-in's values in their rows; none for one of another type.</summary>
    public void Load(PostmanAuth? auth)
    {
        _loading = true;
        foreach (var (type, fields) in All)
            foreach (var field in fields)
                field.Load(auth?.Type == type ? auth[field.Key] : "");
        Fit();
        _loading = false;
    }

    /// <summary>The rows of a type, shown.</summary>
    public void Show(string? type)
    {
        var fields = type is not null && All.TryGetValue(type, out var own) ? own : [];
        if (Shown.SequenceEqual(fields))
            return;
        Shown.Clear();
        foreach (var field in fields)
            Shown.Add(field);
    }

    /// <summary>The rows' values put in a sign-in's, what it had and has no row kept.</summary>
    public void Put(string type, IDictionary<string, string> values)
    {
        foreach (var field in All[type])
            // A choice left as the first one is written only where the collection says it already.
            if (field.Value.Length > 0 || values.ContainsKey(field.Key))
                values[field.Key] = field.Effective;
    }
}
