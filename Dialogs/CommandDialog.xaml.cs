using System.Windows;
using NetworkHelper.Domain;

namespace NetworkHelper.Dialogs;

public partial class CommandDialog:Window
{
    public string Platform=>PlatformBox.Text.Trim();public string Category=>CategoryBox.Text.Trim();public string CommandText=>CommandBox.Text.Trim();public string Purpose=>PurposeBox.Text.Trim();public string RiskLevel=>(string)RiskBox.SelectedItem;public string? Notes=>string.IsNullOrWhiteSpace(NotesBox.Text)?null:NotesBox.Text.Trim();
    public CommandDialog(CommandKnowledge? command=null){InitializeComponent();RiskBox.ItemsSource=new[]{"READ_ONLY","ACTIVE_TEST"};RiskBox.SelectedIndex=0;if(command is null)return;Title="Edit command memory";PlatformBox.Text=command.Platform;CategoryBox.Text=command.Category;CommandBox.Text=command.CommandText;PurposeBox.Text=command.Purpose;RiskBox.SelectedItem=command.RiskLevel;NotesBox.Text=command.Notes;}
    private void Save_Click(object sender,RoutedEventArgs e){if(new[]{Platform,Category,CommandText,Purpose}.Any(string.IsNullOrWhiteSpace)){MessageBox.Show(this,"Platform, category, command and purpose are required.");return;}DialogResult=true;}
}
