using System.Windows;
using System.Windows.Controls;
using NetworkHelper.Domain;
namespace NetworkHelper.Dialogs;
public partial class DeviceDialog:Window
{
 public string DeviceName=>NameBox.Text.Trim();public string? Ip=>V(IpBox.Text);public string? DeviceType=>V(TypeBox.Text);public string? Zone=>V(ZoneBox.Text);public string? Mac=>V(MacBox.Text);public string? Serial=>V(SerialBox.Text);public string? Vendor=>V(VendorBox.Text);public string? Model=>V(ModelBox.Text);public string? Description=>V(DescriptionBox.Text);
 public DeviceDialog(Device? d=null){InitializeComponent();ZoneBox.ItemsSource=new[]{"IT","OT","DMZ"};if(d is null)return;NameBox.Text=d.Name;IpBox.Text=d.IpAddress;TypeBox.Text=d.DeviceType;ZoneBox.SelectedItem=d.Zone;MacBox.Text=d.MacAddress;SerialBox.Text=d.SerialNumber;VendorBox.Text=d.Vendor;ModelBox.Text=d.Model;DescriptionBox.Text=d.Description;}
 private static string? V(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
 private void Save_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(DeviceName)){MessageBox.Show(this,"Device name is required.");return;}if(Ip is not null&&!System.Net.IPAddress.TryParse(Ip,out _)){MessageBox.Show(this,"Enter a valid IP address.");return;}DialogResult=true;}
}
