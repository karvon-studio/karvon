using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Karvon.Claude;
using Karvon.ViewModels;

namespace Karvon.Views;

/// <summary>The Claude Code tool window: keyboard handling for the prompt and keeping the conversation scrolled.</summary>
public partial class ClaudeView : UserControl
{
    ClaudeViewModel? _vm;
    bool _stickToBottom = true;

    /// <summary>How many messages a conversation shows at first, and how many more each Show earlier messages adds.</summary>
    public const int ShownStep = 100;

    /// <summary>
    /// The messages the conversation shows: its last ones, the earlier ones a click away. They are laid out whole (a plain
    /// stack panel): a virtualizing one guessed the heights of those out of view, and at the end of a conversation that
    /// streamed, its guesses and the end chased each other (the conversation jumped up, and Avalonia ended Karvon with
    /// "Infinite layout loop detected"). Showing only the last ones keeps a long conversation as quick to open and as small.
    /// </summary>
    internal readonly System.Collections.ObjectModel.ObservableCollection<ChatItem> ShownItems = [];

    int _wanted = ShownStep;
    bool _syncPending;

    public ClaudeView()
    {
        InitializeComponent();
        ChatItems.ItemsSource = ShownItems;
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        PromptBox.GotFocus += (_, _) => _vm?.RefreshContext();
        SuggestionList.Tapped += (_, _) => ApplySelectedSuggestion();
        HistoryList.Tapped += (_, _) =>
        {
            if (HistoryList.SelectedItem is ClaudeConversation conversation)
                _vm?.ResumeConversationCommand.Execute(conversation);
        };
        ChatScroll.ScrollChanged += OnChatScrollChanged;
        // The user's own scrolling: the wheel, the scroll bar (pressed until let go), the keys that scroll.
        ChatScroll.AddHandler(PointerWheelChangedEvent, (_, _) => UserScrolls(), RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatScroll.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Avalonia.Visual source && (source as Avalonia.Controls.Primitives.ScrollBar ?? Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Avalonia.Controls.Primitives.ScrollBar>(source)) is { } bar
                && ReferenceEquals(Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ScrollViewer>(bar), ChatScroll))
                _draggingBar = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatScroll.AddHandler(PointerReleasedEvent, (_, _) => _draggingBar = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatScroll.AddHandler(PointerCaptureLostEvent, (_, _) => _draggingBar = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatScroll.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Home or Key.End)
                UserScrolls();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            Attach(DataContext as ClaudeViewModel);
            if (IsAttachedToVisualTree())
                _vm?.WarmUp();
        };
        AttachedToVisualTree += (_, _) => _vm?.WarmUp();
        // Another tab, then this one again: the conversation lays out anew; it comes back where it was.
        ChatScroll.DetachedFromVisualTree += (_, _) => _offsetAway = ChatScroll.Offset;
        ChatScroll.AttachedToVisualTree += (_, _) => Settle(_offsetAway);

        // Images and files can be dropped anywhere on the panel.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    static bool CanAttach(IDataTransfer data) =>
        data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap);

    void OnDragOver(object? sender, DragEventArgs e)
    {
        var ok = _vm is not null && CanAttach(e.DataTransfer);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = ok;
        e.Handled = true;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            vm.AddFiles(ImageFit.AttachLargeImages(vm, files.Select(f => f.TryGetLocalPath()).OfType<string>()));
            return;
        }
        if (e.DataTransfer.TryGetBitmap() is { } bitmap)
        {
            var (bytes, extension) = ImageFit.ForClaude(ToPng(bitmap));
            vm.AddImage("dropped image" + extension, bytes);
        }
    }

    static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>Ctrl+V: an image or files on the clipboard are attached; text is pasted as usual.</summary>
    async Task PasteAsync()
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                vm.AddFiles(ImageFit.AttachLargeImages(vm, files.Select(f => f.TryGetLocalPath()).OfType<string>()));
                return;
            }
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                // A screenshot of a large screen goes as a smaller JPEG when it is over 5 MB as PNG.
                var (bytes, extension) = ImageFit.ForClaude(ToPng(bitmap));
                vm.AddImage($"pasted image {vm.Attachments.Count + 1}{extension}", bytes);
                return;
            }
        }
        catch (Exception ex)
        {
            vm.AttachmentError = "Could not paste: " + ex.Message;
            return;
        }
        PromptBox.Paste();
    }

    async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach to the prompt",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        vm.AddFiles(ImageFit.AttachLargeImages(vm, files.Select(f => f.TryGetLocalPath()).OfType<string>()));
    }

    /// <summary>English / O'zbekcha: the panel's language, remembered in the settings.</summary>
    void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        var language = ClaudeLanguage.Instance.Language == "uz" ? "en" : "uz";
        ClaudeLanguage.Instance.Language = language;
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.Settings.UiLanguage = language;
            main.Settings.Save();
        }
    }

    /// <summary>A model chosen in the model menu closes it.</summary>
    void OnModelChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.RemovedItems.Count > 0)
            ModelButton.Flyout?.Hide();
    }

    bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;

    void Attach(ClaudeViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.Items.CollectionChanged -= OnItemsChanged;
            _vm.FocusInputRequested -= FocusPrompt;
            _vm.OpenUrlRequested -= OpenUrl;
        }
        _vm = vm;
        _wanted = ShownStep;
        SyncShown();
        if (vm is null)
            return;
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.FocusInputRequested += FocusPrompt;
        vm.OpenUrlRequested += OpenUrl;
    }

    /// <summary>A click outside a menu or dialog closes it.</summary>
    void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseDialogsCommand.Execute(null);
        e.Handled = true;
    }

    void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>Puts the caret in the prompt (Ctrl+Esc, Ask Claude, new conversation).</summary>
    public void FocusPrompt() => Dispatcher.UIThread.Post(() =>
    {
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<TextChatItem>())
                item.PropertyChanged += OnStreamingTextChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _stickToBottom = true;
            _wanted = ShownStep;
        }
        if (!_syncPending)
        {
            // Once for a burst of changes (a resumed conversation's hundreds of messages come at once).
            _syncPending = true;
            Dispatcher.UIThread.Post(SyncShown, DispatcherPriority.Normal);
        }
        ScrollToEndIfFollowing();
    }

    /// <summary>
    /// The messages shown made the conversation's again: its last ones (as many as asked for), or, while the user reads
    /// further up, everything from the first one shown (one going off the top would move what they read).
    /// </summary>
    void SyncShown()
    {
        _syncPending = false;
        var items = _vm?.Items;
        if (items is null || items.Count == 0)
        {
            ShownItems.Clear();
            ShowEarlierButton.IsVisible = false;
            return;
        }
        var from = Math.Max(0, items.Count - _wanted);
        if (!_stickToBottom && ShownItems.Count > 0 && items.IndexOf(ShownItems[0]) is >= 0 and var first && first < from)
        {
            from = first;
            _wanted = items.Count - first;
        }
        var want = new List<ChatItem>(items.Count - from);
        for (var i = from; i < items.Count; i++)
            want.Add(items[i]);
        // The fewest changes: those no longer shown go, then the missing ones go in their places (both keep the order).
        var keep = new HashSet<ChatItem>(want, ReferenceEqualityComparer.Instance);
        for (var i = ShownItems.Count - 1; i >= 0; i--)
            if (!keep.Contains(ShownItems[i]))
                ShownItems.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
            if (i >= ShownItems.Count || !ReferenceEquals(ShownItems[i], want[i]))
                ShownItems.Insert(i, want[i]);
        ShowEarlierButton.IsVisible = from > 0;
    }

    /// <summary>Show earlier messages: a hundred more above, the one read before staying where it was on screen.</summary>
    void OnShowEarlier(object? sender, RoutedEventArgs e)
    {
        var before = ChatScroll.Extent.Height;
        _stickToBottom = false;
        _wanted = ShownItems.Count + ShownStep;
        SyncShown();
        Dispatcher.UIThread.Post(() => ChatScroll.Offset = ChatScroll.Offset.WithY(ChatScroll.Offset.Y + ChatScroll.Extent.Height - before),
            DispatcherPriority.Background);
    }

    void OnStreamingTextChanged(object? sender, PropertyChangedEventArgs e) => ScrollToEndIfFollowing();

    /// <summary>Within a few lines of the conversation's end (the user, scrolling there, follows it again).</summary>
    bool IsAtBottom => ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - 48;

    /// <summary>At its very end (where a conversation that follows it is kept).</summary>
    bool IsAtEnd => ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - 0.5;

    /// <summary>
    /// Only the user's scrolling decides whether the conversation follows its end: when it grows (an answer streams,
    /// an old conversation lays out, the panel shows or is resized) one that followed still does, so a resumed
    /// conversation opens at its last message. Its messages are made as they come into view and its panel guesses the
    /// heights of the others, so the offset moves by itself as they become known: that is not the user's scrolling (it
    /// made a following conversation stop following and stay up). Going to the end again waits for the layout under way:
    /// done inside it, the guesses and the end chased each other until Avalonia gave up ("Infinite layout loop").
    /// </summary>
    void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // A code block's own (sideways) scroll viewer says so too, as the conversation is laid out: not the conversation's.
        if (!ReferenceEquals(e.Source, ChatScroll))
            return;
        if (e.OffsetDelta.Y != 0 && IsUserScrolling)
            _stickToBottom = IsAtBottom;
        else if (_stickToBottom && !IsAtEnd)
            ScrollToEndSoon();
        JumpToBottomButton.IsVisible = !_stickToBottom && !IsAtBottom;
    }

    bool _draggingBar;
    long _userScrolledAt = -10_000;
    bool _scrollPending;
    Avalonia.Vector _offsetAway;

    void UserScrolls() => _userScrolledAt = Environment.TickCount64;

    /// <summary>The scroll bar held, or the wheel or a key a moment ago (its scrolling may take a frame or two).</summary>
    bool IsUserScrolling => _draggingBar || Environment.TickCount64 - _userScrolledAt < 300;

    /// <summary>Back in view: the end (when it followed it) or where it was, once it is laid out again.</summary>
    void Settle(Avalonia.Vector offset) => Dispatcher.UIThread.Post(() =>
    {
        if (_stickToBottom)
            ScrollToEnd();
        else
            ChatScroll.Offset = offset;
    }, DispatcherPriority.Background);

    /// <summary>To the end now; once more after the layout it causes, if the last messages' heights moved the end.</summary>
    void ScrollToEnd()
    {
        ChatScroll.ScrollToEnd();
        ScrollToEndSoon();
    }

    /// <summary>To the end after the layout under way (once, however many changes ask for it meanwhile).</summary>
    void ScrollToEndSoon()
    {
        if (_scrollPending)
            return;
        _scrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollPending = false;
            if (_stickToBottom && !IsAtEnd)
                ChatScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    void OnJumpToBottom(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        JumpToBottomButton.IsVisible = false;
        ScrollToEnd();
    }

    void ScrollToEndIfFollowing()
    {
        if (!_stickToBottom)
            return;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        var suggesting = vm.Suggestions.Count > 0;
        switch (e.Key)
        {
            case Key.V when e.KeyModifiers == Karvon.Views.PlatformKeys.Command:
                e.Handled = true;
                _ = PasteAsync();
                return;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // Shift+Enter: a new line (the TextBox inserts it).
                return;
            case Key.Enter or Key.Tab when suggesting && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                ApplySelectedSuggestion();
                e.Handled = true;
                return;
            case Key.Enter:
                if (vm.SendCommand.CanExecute(null))
                    vm.SendCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                vm.CycleModeCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Down or Key.Up when suggesting:
                var count = vm.Suggestions.Count;
                var index = SuggestionList.SelectedIndex < 0 ? (e.Key == Key.Down ? 0 : count - 1) : (SuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + count) % count;
                SuggestionList.SelectedIndex = index;
                e.Handled = true;
                return;
            case Key.Escape when suggesting:
                vm.Suggestions.Clear();
                e.Handled = true;
                return;
            case Key.Escape when vm.IsRunning:
                vm.StopCommand.Execute(null);
                e.Handled = true;
                return;
        }
    }

    void ApplySelectedSuggestion()
    {
        if (_vm is not { } vm || vm.Suggestions.Count == 0)
            return;
        var suggestion = SuggestionList.SelectedItem as ClaudeSuggestion ?? vm.Suggestions[0];
        vm.ApplySuggestionCommand.Execute(suggestion);
    }
}
