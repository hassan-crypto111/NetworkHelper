using System.Windows;
using NetworkHelper.Domain;
namespace NetworkHelper.Dialogs;
public partial class RelationshipDialog:Window
{
 public string FromName=>FromBox.Text.Trim();public string ToName=>ToBox.Text.Trim();public string RelationshipType=>(string)TypeBox.SelectedItem;public string? Basis=>string.IsNullOrWhiteSpace(BasisBox.Text)?null:BasisBox.Text.Trim();public string? ScopeKey=>string.IsNullOrWhiteSpace(ScopeBox.Text)?null:ScopeBox.Text.Trim();
 public RelationshipDialog(string? target=null){InitializeComponent();TypeBox.ItemsSource=RelationshipTypes.All;TypeBox.SelectedItem="MANAGED_FROM";FromBox.Text=target??"Current workstation";ToBox.Text=target is null?"":"Current workstation";}
 private void Save_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(FromName)||string.IsNullOrWhiteSpace(ToName)||TypeBox.SelectedItem is null){MessageBox.Show(this,"From, relationship type and to are required.");return;}if(FromName.Equals(ToName,StringComparison.OrdinalIgnoreCase)){MessageBox.Show(this,"The two entities must be different.");return;}DialogResult=true;}
}
