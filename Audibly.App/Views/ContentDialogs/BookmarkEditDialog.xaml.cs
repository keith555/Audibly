// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using Microsoft.UI.Xaml.Controls;

namespace Audibly.App.Views.ContentDialogs;

public sealed partial class BookmarkEditDialog : ContentDialog
{
    public BookmarkEditDialog(string title, string locationText, string initialNote)
    {
        InitializeComponent();
        Title = title;
        LocationTextBlock.Text = locationText ?? string.Empty;
        NoteTextBox.Text = initialNote ?? string.Empty;
    }

    public string NoteText => NoteTextBox.Text ?? string.Empty;
}
