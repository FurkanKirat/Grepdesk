using Avalonia.Controls;
using Grepdesk.Core.Jobs;

namespace Grepdesk.UI.Jobs;

public sealed record ConflictAnswer(ConflictChoice Choice, bool ApplyToAll);

/// <summary>"This file already exists" — shows both versions side by side.</summary>
public partial class ConflictDialog : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    public ConflictDialog()
    {
        InitializeComponent();
    }

    public ConflictDialog(ConflictInfo conflict) : this()
    {
        Title = Loc.Get("ConflictTitle");
        MessageText.Text = Loc.Get("ConflictMessage", Path.GetFileName(conflict.DestinationPath));

        SourceLabel.Text = Loc.Get("ConflictSource");
        SourceSize.Text = Format.Size(conflict.SourceSize);
        SourceDate.Text = conflict.SourceLastWrite.ToString("g");

        DestinationLabel.Text = Loc.Get("ConflictDestination");
        DestinationSize.Text = Format.Size(conflict.DestinationSize);
        DestinationDate.Text = conflict.DestinationLastWrite.ToString("g");

        // Point out which one is newer; that's usually what decides it.
        if (conflict.SourceLastWrite > conflict.DestinationLastWrite)
            SourceDate.Text += "  " + Loc.Get("ConflictNewer");
        else if (conflict.DestinationLastWrite > conflict.SourceLastWrite)
            DestinationDate.Text += "  " + Loc.Get("ConflictNewer");

        ApplyToAllCheckBox.Content = Loc.Get("ConflictApplyToAll");
        OverwriteButton.Content = Loc.Get("ConflictOverwrite");
        SkipButton.Content = Loc.Get("ConflictSkip");
        KeepBothButton.Content = Loc.Get("ConflictKeepBoth");

        OverwriteButton.Click += (_, _) => Answer(ConflictChoice.Overwrite);
        SkipButton.Click += (_, _) => Answer(ConflictChoice.Skip);
        KeepBothButton.Click += (_, _) => Answer(ConflictChoice.KeepBoth);
    }

    private void Answer(ConflictChoice choice) =>
        Close(new ConflictAnswer(choice, ApplyToAllCheckBox.IsChecked == true));
}
