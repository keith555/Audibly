// Author: rstewa · https://github.com/rstewa
// Created: 04/18/2026

using Microsoft.UI.Xaml.Controls;

namespace Audibly.App.Views.ContentDialogs;

public enum BookmarkConflictChoice
{
    KeepExisting,
    Replace,
    KeepBoth
}

public sealed partial class BookmarkConflictDialog : ContentDialog
{
    public BookmarkConflictDialog(string locationText, string existingNote, string newNote)
    {
        InitializeComponent();
        LocationText = locationText;
        ExistingNote = existingNote;
        NewNote = newNote;
    }

    public string LocationText { get; }
    public string ExistingNote { get; }
    public string NewNote { get; }

    public bool ApplyToAll => ApplyToAllCheckBox.IsChecked == true;
}
