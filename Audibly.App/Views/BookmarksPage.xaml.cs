// Author: rstewa · https://github.com/rstewa
// Created: 04/19/2026

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Audibly.App.Services;
using Audibly.App.ViewModels;
using Audibly.App.Views.ContentDialogs;
using Audibly.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Audibly.App.Views;

public sealed partial class BookmarksPage : Page
{
    private readonly List<BookmarkBrowseItem> _allItems = new();

    public BookmarksPage()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     Bookmarks shown in the list — filtered subset of <see cref="_allItems" />.
    /// </summary>
    public ObservableCollection<BookmarkBrowseItem> FilteredItems { get; } = new();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _allItems.Clear();

        var audiobooks = (await App.Repository.Audiobooks.GetAsync()).ToList();
        var bookmarks = (await App.Repository.Bookmarks.GetAllAsync()).ToList();
        var booksById = audiobooks.ToDictionary(a => a.Id);

        foreach (var bookmark in bookmarks)
        {
            booksById.TryGetValue(bookmark.AudiobookId, out var audiobook);
            _allItems.Add(new BookmarkBrowseItem(bookmark, audiobook));
        }

        // sort by book title, then chapter/position
        _allItems.Sort((a, b) =>
        {
            var byBook = string.Compare(a.BookTitle, b.BookTitle, StringComparison.OrdinalIgnoreCase);
            if (byBook != 0) return byBook;
            return a.Bookmark.PositionMs.CompareTo(b.Bookmark.PositionMs);
        });

        ApplyFilter(FilterBox.Text);
    }

    private void ApplyFilter(string query)
    {
        FilteredItems.Clear();

        IEnumerable<BookmarkBrowseItem> source = _allItems;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            source = source.Where(i =>
                Contains(i.BookTitle, q) ||
                Contains(i.BookAuthor, q) ||
                Contains(i.Location, q) ||
                Contains(i.Note, q));
        }

        foreach (var item in source) FilteredItems.Add(item);

        UpdateEmptyState();
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void UpdateEmptyState()
    {
        var empty = FilteredItems.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BookmarksList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = $"{FilteredItems.Count} of {_allItems.Count}";
    }

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ApplyFilter(sender.Text);
    }

    private async void Refresh_OnClick(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async void EditBookmark_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BookmarkBrowseItem item) return;

        var title = $"Edit bookmark — {item.BookTitle}";
        var (result, note) = await DialogService.ShowBookmarkEditDialogAsync(title, item.Location, item.Note);
        if (result != ContentDialogResult.Primary) return;

        item.Note = note;
        await App.Repository.Bookmarks.UpsertAsync(item.Bookmark);

        // if this bookmark belongs to the currently playing book, keep the in-player list in sync
        if (App.PlayerViewModel.NowPlaying != null &&
            App.PlayerViewModel.NowPlaying.Id == item.Bookmark.AudiobookId)
            await App.PlayerViewModel.LoadBookmarksAsync();
    }

    private async void DeleteBookmark_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not BookmarkBrowseItem item) return;

        var result = await DialogService.ShowConfirmationDialogAsync(
            "Delete bookmark",
            $"Delete this bookmark from \"{item.BookTitle}\"? This cannot be undone.",
            "Delete",
            "Cancel");
        if (result != ContentDialogResult.Primary) return;

        await App.Repository.Bookmarks.DeleteAsync(item.Bookmark.Id);
        _allItems.Remove(item);
        FilteredItems.Remove(item);
        UpdateEmptyState();

        if (App.PlayerViewModel.NowPlaying != null &&
            App.PlayerViewModel.NowPlaying.Id == item.Bookmark.AudiobookId)
            await App.PlayerViewModel.LoadBookmarksAsync();
    }
}
