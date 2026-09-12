using System.Windows;
using NetworkHelper.Domain;
namespace NetworkHelper.Dialogs;
public partial class MergeSiteDialog:Window
{
    public Site? Target=>Targets.SelectedItem as Site;
    public MergeSiteDialog(Site source,IReadOnlyList<Site> targets){InitializeComponent();Prompt.Text=$"Merge '{source.Name}' into another site?";Targets.ItemsSource=targets;if(targets.Count>0)Targets.SelectedIndex=0;}
    private void Merge_Click(object sender,RoutedEventArgs e){if(Target is null){MessageBox.Show(this,"Select a destination site.");return;}DialogResult=true;}
}
