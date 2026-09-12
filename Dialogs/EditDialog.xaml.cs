using System.Windows;
namespace NetworkHelper.Dialogs;
public partial class EditDialog : Window
{
    public string Value => NameBox.Text.Trim(); public string? Address => string.IsNullOrWhiteSpace(AddressBox.Text)?null:AddressBox.Text.Trim();
    public EditDialog(string title,string value="",string? address=null,bool showAddress=false) { InitializeComponent(); Title=title; NameBox.Text=value; AddressBox.Text=address; AddressLabel.Visibility=AddressBox.Visibility=showAddress?Visibility.Visible:Visibility.Collapsed; Loaded+=(_,_)=>{NameBox.Focus();NameBox.SelectAll();}; }
    private void Save_Click(object sender,RoutedEventArgs e) { if(string.IsNullOrWhiteSpace(Value)){MessageBox.Show(this,"A name is required.");return;} DialogResult=true; }
}
