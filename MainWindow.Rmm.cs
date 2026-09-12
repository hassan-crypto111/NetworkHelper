using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NetworkHelper.Services;

namespace NetworkHelper;

public partial class MainWindow
{
    private RmmImportService? _rmmImport;
    private RmmVerificationService? _rmmVerification;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(typeof(Button),Button.ClickEvent,new RoutedEventHandler(RmmButtonClassHandler),true);
        EventManager.RegisterClassHandler(typeof(MainWindow),FrameworkElement.LoadedEvent,new RoutedEventHandler(RmmWindowLoaded),true);
    }

    private static void RmmWindowLoaded(object sender,RoutedEventArgs e)
    {
        if(sender is not MainWindow w)return;
        w._rmmImport??=new RmmImportService(w._db);w._rmmVerification??=new RmmVerificationService(w._db);
        if(!w.DevicesGrid.Columns.Any(x=>string.Equals(x.Header?.ToString(),"RMM",StringComparison.OrdinalIgnoreCase)))
            w.DevicesGrid.Columns.Insert(0,new DataGridTextColumn{Header="RMM",Binding=new Binding("RmmStatus"),Width=new DataGridLength(58),IsReadOnly=true});
        w.ApplyRmmVerification();
    }

    private static void RmmButtonClassHandler(object sender,RoutedEventArgs e)
    {
        if(sender is not Button button||Window.GetWindow(button) is not MainWindow w)return;
        var content=button.Content?.ToString()??"";
        if(ReferenceEquals(button,w.ImportButton)&&w._preview.Count>0&&w._preview.All(x=>x.IsRmm))
        {
            e.Handled=true;w.ImportRmmPreview();return;
        }
        if(content.Contains("Upload",StringComparison.OrdinalIgnoreCase)||content.Equals("Import company data",StringComparison.OrdinalIgnoreCase))
        {
            w.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(w.ClassifyRmmPreview));return;
        }
        w.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(w.ApplyRmmVerification));
    }

    private void ClassifyRmmPreview()
    {
        if(Company is null||_preview.Count==0||!_preview.All(x=>x.IsRmm))return;
        _rmmImport??=new RmmImportService(_db);_rmmImport.ClassifyForCompany(Company.Id,_preview);
        PreviewGrid.ItemsSource=null;PreviewGrid.ItemsSource=_preview;
        var verified=_preview.Count(x=>x.RmmStatus=="✓");var warnings=_preview.Count(x=>x.RmmStatus=="⚠");var unassigned=_preview.Count(x=>x.DestinationSite=="Unassigned Devices");
        ImportButton.IsEnabled=_preview.Any(x=>x.Import);
        ImportStatus.Text=$"RMM preview: {verified} verified, {warnings} review, {unassigned} unassigned. Existing devices are matched company-wide by hostname; ✓ requires hostname + IP match.";
    }

    private void ImportRmmPreview()
    {
        if(Company is null||_pendingPath is null||_preview.Count==0)return;
        try
        {
            _rmmImport??=new RmmImportService(_db);var touched=_rmmImport.ImportForCompany(Company.Id,_pendingPath,_preview);
            var imported=_preview.Count(x=>x.Import);_preview=[];_pendingPath=null;PreviewGrid.ItemsSource=null;ImportButton.IsEnabled=false;
            ReloadSites();SitesList.SelectedItem=null;RefreshSite();
            ImportStatus.Text=$"RMM import complete: {imported} row(s) processed across {touched.Count} site(s). Select a site and open Devices to see ✓ RMM verification.";
        }
        catch(Exception ex){MessageBox.Show(this,$"RMM import failed.\n\n{ex.Message}","RMM import",MessageBoxButton.OK,MessageBoxImage.Error);}
    }

    private void ApplyRmmVerification()
    {
        if(_db is null||Site is not{ } site||DevicesGrid is null)return;
        _rmmVerification??=new RmmVerificationService(_db);
        var selected=DevicesGrid.SelectedItem as NetworkHelper.Domain.Device;
        _siteDevices=_rmmVerification.Apply(_db.Devices(site.Id));
        ApplyDeviceFilters();
        if(selected is not null)DevicesGrid.SelectedItem=_siteDevices.FirstOrDefault(x=>x.Id==selected.Id);
    }
}
