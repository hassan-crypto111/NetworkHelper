namespace NetworkHelper.Services;

public static class DeviceClassifier
{
    public static string Type(string name,string? explicitType=null,string? description=null)
    {
        var supplied=explicitType?.Trim();if(!string.IsNullOrWhiteSpace(supplied))return supplied.ToUpperInvariant() switch{"VM"=>"Virtual Machine","HOST"=>"Hypervisor Host","IDRAC" or "ILO"=>"Management Controller","NAS" or "SAN"=>"Storage Appliance",_=>supplied};
        var value=(name+" "+description).ToUpperInvariant();
        if(value.Contains("FIREWALL")||Token(value,"FW"))return "Firewall";
        if(value.Contains("SWITCH")||value.Contains("STRATIX")||Token(value,"SW"))return "Switch";
        if(value.Contains("ROUTER")||Token(value,"RTR"))return "Router";
        if(value.Contains("IDRAC")||value.Contains("ILO"))return "Management Controller";
        if(value.Contains("ESX")||value.Contains("HYPER-V")||Token(value,"VH"))return "Hypervisor Host";
        if(value.Contains("ACCESS POINT")||Token(value,"AP"))return "Wireless Access Point";
        if(value.Contains("NAS")||value.Contains("SAN"))return "Storage Appliance";
        if(value.Contains("SERVER")||System.Text.RegularExpressions.Regex.IsMatch(value,@"(?:SRV|FS|DC|SQL|RDS|WSUS|WEB|HIS|TS)\d{0,2}$"))return "Server";
        if(value.Contains("PLC")||value.Contains("ACCULOAD")||value.Contains("BOILER"))return "Industrial Device";
        return "Network Appliance";
    }
    public static string? Zone(string name,string? explicitZone=null)
    {
        var supplied=explicitZone?.Trim().ToUpperInvariant();if(supplied is "IT" or "OT" or "DMZ")return supplied;
        var value=name.ToUpperInvariant();if(value.Contains("DMZ"))return "DMZ";if(System.Text.RegularExpressions.Regex.IsMatch(value,@"(?:^|[-_])OT|AUPIOT"))return "OT";if(System.Text.RegularExpressions.Regex.IsMatch(value,@"(?:^|[-_])IT|AUPIIT"))return "IT";return null;
    }
    private static bool Token(string value,string token)=>System.Text.RegularExpressions.Regex.IsMatch(value,$@"{token}\d{{0,2}}$")||value.Contains(token+"0")||value.Contains(token+"1");
}
