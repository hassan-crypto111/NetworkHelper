using System.Windows;
using NetworkHelper.Domain;
namespace NetworkHelper.Dialogs;
public partial class NetworkDialog:Window
{
 public string NetworkName=>NameBox.Text.Trim();public string Cidr=>CidrBox.Text.Trim();public int? VlanId=>int.TryParse(VlanBox.Text,out var value)?value:null;public string? Zone=>string.IsNullOrWhiteSpace(ZoneBox.Text)?null:ZoneBox.Text.Trim().ToUpperInvariant();
 public NetworkDialog(NetworkRecord? n=null){InitializeComponent();ZoneBox.ItemsSource=new[]{"IT","OT","DMZ"};if(n is null)return;NameBox.Text=n.Name;CidrBox.Text=n.Cidr;VlanBox.Text=n.VlanId?.ToString();ZoneBox.SelectedItem=n.Zone;}
 private void Save_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(NetworkName)||string.IsNullOrWhiteSpace(Cidr)){MessageBox.Show(this,"Name and CIDR are required.");return;}var parts=Cidr.Split('/');if(parts.Length!=2||!System.Net.IPAddress.TryParse(parts[0],out _)||!int.TryParse(parts[1],out var prefix)||prefix is <0 or >32){MessageBox.Show(this,"Enter valid IPv4 CIDR, for example 10.0.0.0/24.");return;}if(!string.IsNullOrWhiteSpace(VlanBox.Text)&&(VlanId is null or <1 or >4094)){MessageBox.Show(this,"VLAN ID must be between 1 and 4094.");return;}DialogResult=true;}
}
