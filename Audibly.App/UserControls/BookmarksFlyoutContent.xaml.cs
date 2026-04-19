// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using System.Collections.Specialized;
using System.Threading.Tasks;
using Audibly.App.Services;
using Audibly.App.ViewModels;
using Audibly.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Audibly.App.UserControls;

public sealed partial class BookmarksFlyoutContent : UserControl
{
    public BookmarksFlyoutContent()
    {
        InitializeComponent();
        PlayerViewModel.CurrentBookmarks.CollectionChanged += Bookmarks_CollectionChanged;
        Loaded += OnLoaded;
        UpdateEmptyState();
    }

    public PlayerViewModel PlayerViewModel => App.PlayerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void Bookmarks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var empty = PlayerViewModel.CurrentBookmarks.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BookmarksList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LocationTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is Bookmark b)
            tb.Text = PlayerViewModel.FormatBookmarkLocation(b);
    }

    private async void AddBookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        DismissFlyout(sender as FrameworkElement);
        await SaveNewBookmarkAsync();
    }

    private async void PlayBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Bookmark b) return;
        DismissFlyout(fe);
        await PlayerViewModel.SeekToBookmarkAsync(b);
    }

    private async void EditBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Bookmark b) return;
        DismissFlyout(fe);

        var location = PlayerViewModel.FormatBookmarkLocation(b);
        var (result, note) = await DialogService.ShowBookmarkEditDialogAsync("Edit bookmark", location, b.Note);
        if (result != ContentDialogResult.Primary) return;

        b.Note = note;
        await PlayerViewModel.UpdateBookmarkAsync(b);
    }

    private async void DeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not Bookmark b) return;
        DismissFlyout(fe);

        var result = await DialogService.ShowConfirmationDialogAsync(
            "Delete bookmark",
            "Delete this bookmark? This cannot be undone.",
            "Delete",
            "Cancel");
        if (result != ContentDialogResult.Primary) return;

        await PlayerViewModel.DeleteBookmarkAsync(b);
    }

    private async Task SaveNewBookmarkAsync()
    {
        if (PlayerViewModel.NowPlaying == null) return;

        var positionMs = (long)PlayerViewModel.CurrentPosition.TotalMilliseconds;
        var sourceFileId = PlayerViewModel.NowPlaying.CurrentSourceFile.Id;
        var stub = new Bookmark { SourceFileId = sourceFileId, PositionMs = positionMs };
        var location = PlayerViewModel.FormatBookmarkLocation(stub);

        var (result, note) = await DialogService.ShowBookmarkEditDialogAsync("Save bookmark", location, string.Empty);
        if (result != ContentDialogResult.Primary) return;

        await PlayerViewModel.SaveBookmarkAsync(sourceFileId, positionMs, note);
    }

    public FlyoutBase? HostFlyout { get; set; }

    private void DismissFlyout(FrameworkElement? element)
    {
        HostFlyout?.Hide();
    }
}
