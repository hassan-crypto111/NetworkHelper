using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public static class EntityMatcher
{
    public static Device? BestMatch(ImportRow row,IEnumerable<Device> devices)=>devices.Select(d=>(Device:d,Score:Score(row,d))).Where(x=>x.Score>=70).OrderByDescending(x=>x.Score).Select(x=>x.Device).FirstOrDefault();
    public static IReadOnlyList<(string Field,string? Existing,string? Proposed)> Conflicts(ImportRow row,Device device)
    {
        var values=new[]{("name",device.Name,row.Name),("ip_address",device.IpAddress,row.IpAddress),("mac_address",device.MacAddress,row.MacAddress),("serial_number",device.SerialNumber,row.SerialNumber),("vendor",device.Vendor,row.Vendor),("model",device.Model,row.Model)};
        return values.Where(x=>!string.IsNullOrWhiteSpace(x.Item3)&&!string.IsNullOrWhiteSpace(x.Item2)&&!Equal(x.Item2,x.Item3)).Select(x=>(x.Item1,x.Item2,x.Item3)).ToList();
    }
    private static int Score(ImportRow r,Device d) { var s=0;if(Equal(r.SerialNumber,d.SerialNumber))s+=100;if(Equal(r.MacAddress,d.MacAddress))s+=100;if(Equal(r.IpAddress,d.IpAddress))s+=75;if(Equal(r.Name,d.Name))s+=70;if(!string.IsNullOrWhiteSpace(r.IpAddress)&&string.Equals(d.Name,r.Name+r.IpAddress,StringComparison.OrdinalIgnoreCase))s+=90;if(Equal(r.Vendor,d.Vendor)&&Equal(r.Model,d.Model))s+=20;return s; }
    private static bool Equal(string? a,string? b)=>!string.IsNullOrWhiteSpace(a)&&string.Equals(a.Trim(),b?.Trim(),StringComparison.OrdinalIgnoreCase);
}
