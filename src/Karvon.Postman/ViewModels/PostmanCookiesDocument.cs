using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Postman.ViewModels;

/// <summary>The cookies servers set (the user's): each can be taken away, and one added.</summary>
public sealed partial class PostmanCookiesDocument : PostmanDocument
{
    public PostmanCookiesDocument(IPostmanHost host) : base(host)
    {
        host.Cookies.Changed += OnChanged;
        Load();
    }

    public override string Key => "postman-cookies";

    public override string Title => "Cookies";

    public override string ToolTip => "Postman's cookies";

    protected override string SavedName => Title;

    public ObservableCollection<PostmanCookie> Cookies { get; } = [];

    public bool IsEmpty => Cookies.Count == 0;

    void OnChanged() => Dispatcher.UIThread.Post(Load);

    void Load()
    {
        Cookies.Clear();
        foreach (var cookie in Host.Cookies.All)
            Cookies.Add(cookie);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    void Remove(PostmanCookie? cookie)
    {
        if (cookie is not null)
            Host.Cookies.Remove(c => c == cookie);
    }

    [RelayCommand]
    void Clear() => Host.Cookies.Remove(_ => true);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial string NewDomain { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string NewValue { get; set; } = "";

    public bool CanAdd => NewDomain.Trim().Length > 0 && NewName.Trim().Length > 0;

    [RelayCommand]
    void Add()
    {
        if (!CanAdd)
            return;
        Host.Cookies.Set(new PostmanCookie(NewName.Trim(), NewValue.Trim(), NewDomain.Trim(), "/"));
        (NewName, NewValue) = ("", "");
    }

    public override void Dispose() => Host.Cookies.Changed -= OnChanged;
}
