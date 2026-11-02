using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;
using PAIC.ProjectSystem.Model;

namespace PAIC.App.Dialogs;

public sealed partial class NewProjectDialog : ContentDialog
{
    public NewProjectDialog()
    {
        InitializeComponent();
        LocationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PAIC Projects");
    }

    public string ProjectName => NameBox.Text.Trim();
    public string ParentDirectory => LocationBox.Text;

    private async void Browse_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder is not null) { LocationBox.Text = folder; Validate(); }
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (string.IsNullOrEmpty(NameBox.Text)) { ErrorText.Text = ""; IsPrimaryButtonEnabled = false; return; }
        var ok = ProjectNaming.IsValidName(NameBox.Text, out var reason);
        ErrorText.Text = ok ? "" : reason!;
        IsPrimaryButtonEnabled = ok && !string.IsNullOrWhiteSpace(LocationBox.Text);
    }
}
