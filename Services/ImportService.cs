using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class ImportService(Database db)
{
    private readonly SourceAuthorityPolicy _authority = new();

    public void ClassifyForCompany(long companyId,IEnumerable<ImportRow> rows)
    {
        var sites=db.Sites(companyId);
        foreach(var group in rows.GroupBy(x=>x.SiteLocation?.Trim()??""))
        {
            var site=sites.FirstOrDefault(x=>x.Name.Equals(group.Key,StringComparison.OrdinalIgnoreCase));
            if(site is null){foreach(var row in group)row.Match=(string.IsNullOrWhiteSpace(group.Key)?"Missing site":$"New site: {group.Key}")+RelationshipPreview(row);}
            else Classify(site.Id,group);
        }
    }

    public IReadOnlyList<Site> ImportForCompany(long companyId,string path,IEnumerable<ImportRow> rows)
    {
        if(IsRmmSource(path)) return ImportRmmForCompany(companyId,path,rows);

        var sites=db.Sites(companyId); var touched=new List<Site>();
        foreach(var group in rows.Where(x=>x.Import).GroupBy(x=>x.SiteLocation?.Trim()??""))
        {
            if(string.IsNullOrWhiteSpace(group.Key)) throw new InvalidDataException($"Device '{group.First().Name}' has no Site Location.");
            var site=sites.FirstOrDefault(x=>x.Name.Equals(group.Key,StringComparison.OrdinalIgnoreCase));
            if(site is null){var id=db.AddSite(companyId,group.Key,null);site=new Site(id,companyId,group.Key,null);sites.Add(site);}
            Import(site.Id,path,group); touched.Add(site);
        }
        return touched;
    }

    public void Classify(long siteId,IEnumerable<ImportRow> rows)
    {
        var devices=db.Devices(siteId);
        foreach(var row in rows) { var match=EntityMatcher.BestMatch(row,devices); row.Match=(match is null?"New":EntityMatcher.Conflicts(row,match).Any()?$"Conflict with {match.Name}":$"Matches {match.Name}")+RelationshipPreview(row); }
    }

    public void Import(long siteId,string path,IEnumerable<ImportRow> rows)
    {
        using var c=db.BeginConnection(); using var tx=c.BeginTransaction();
        long source=Scalar(c,"INSERT INTO evidence_sources(site_id,file_name,source_type,imported_at,content_hash) VALUES($s,$f,$t,$at,$h); SELECT last_insert_rowid();",("$s",siteId),("$f",Path.GetFileName(path)),("$t",Path.GetExtension(path).TrimStart('.').ToUpperInvariant()),("$at",DateTimeOffset.Now.ToString("O")),("$h",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        var devices=db.Devices(siteId);var importedRows=rows.Where(x=>x.Import).ToList();
        foreach(var row in importedRows)
        {
            var device=EntityMatcher.BestMatch(row,devices);
            if(device is null) { var type=DeviceClassifier.Type(row.Name,row.DeviceType,row.Description);var zone=DeviceClassifier.Zone(row.Name,row.Zone);var id=Scalar(c,"INSERT INTO devices(site_id,name,ip_address,mac_address,serial_number,vendor,model,device_type,zone,description) VALUES($s,$n,$ip,$mac,$ser,$v,$m,$t,$z,$desc); SELECT last_insert_rowid();",("$s",siteId),("$n",row.Name),("$ip",row.IpAddress),("$mac",row.MacAddress),("$ser",row.SerialNumber),("$v",row.Vendor),("$m",row.Model),("$t",type),("$z",zone),("$desc",row.Description)); device=new Device(id,siteId,row.Name,row.IpAddress,row.MacAddress,row.SerialNumber,row.Vendor,row.Model,type,zone,row.Description); devices.Add(device); AddFacts(c,siteId,id,row,source,"inferred",.65); }
            else foreach(var conflict in EntityMatcher.Conflicts(row,device)) { Exec(c,"INSERT INTO conflicts(site_id,device_id,entity_name,field_name,existing_value,new_value,source_id) VALUES($s,$d,$n,$f,$old,$new,$src)",("$s",siteId),("$d",device.Id),("$n",device.Name),("$f",conflict.Field),("$old",conflict.Existing),("$new",conflict.Proposed),("$src",source)); AddFact(c,siteId,device.Id,conflict.Field,conflict.Proposed!,source,"conflicting",.5); }
            if(device is not null && !EntityMatcher.Conflicts(row,device).Any()){var type=DeviceClassifier.Type(row.Name,row.DeviceType,row.Description);var zone=DeviceClassifier.Zone(row.Name,row.Zone);Exec(c,"UPDATE devices SET device_type=COALESCE(device_type,$t),zone=COALESCE(zone,$z),description=COALESCE(description,$desc) WHERE id=$id",("$t",type),("$z",zone),("$desc",row.Description),("$id",device.Id));AddFacts(c,siteId,device.Id,row,source,"confirmed",.85);}
            if(!string.IsNullOrWhiteSpace(row.Network)) Exec(c,"INSERT INTO networks(site_id,name,cidr,vlan_id) SELECT $s,$n,$c,$v WHERE NOT EXISTS(SELECT 1 FROM networks WHERE site_id=$s AND cidr=$c AND (vlan_id=$v OR (vlan_id IS NULL AND $v IS NULL)))",("$s",siteId),("$n",row.Network),("$c",row.Network),("$v",row.VlanId));
            if(row.VlanId.HasValue) Exec(c,"INSERT OR IGNORE INTO vlans(site_id,vlan_number,name) VALUES($s,$v,$n)",("$s",siteId),("$v",row.VlanId),("$n",$"VLAN {row.VlanId}"));
        }
        AddDocumentedRelationships(c,siteId,path,importedRows,devices,source);
        tx.Commit();
    }

    private IReadOnlyList<Site> ImportRmmForCompany(long companyId,string path,IEnumerable<ImportRow> rows)
    {
        var selected=rows.Where(x=>x.Import).ToList();
        var sites=db.Sites(companyId);
        var devicesBySite=sites.ToDictionary(x=>x.Id,x=>db.Devices(x.Id));
        var allDevices=devicesBySite.Values.SelectMany(x=>x).ToList();
        var assignments=new Dictionary<long,List<ImportRow>>();
        var unassignedSite=sites.FirstOrDefault(x=>x.Name.Equals("Unassigned Devices",StringComparison.OrdinalIgnoreCase));

        foreach(var row in selected)
        {
            var matched=BestCompanyIdentityMatch(row,allDevices);
            Site? target=matched is null?InferSiteFromNetwork(row.IpAddress,sites):sites.FirstOrDefault(x=>x.Id==matched.SiteId);
            if(target is null)
            {
                if(unassignedSite is null)
                {
                    var id=db.AddSite(companyId,"Unassigned Devices",null);
                    unassignedSite=new Site(id,companyId,"Unassigned Devices",null);
                    sites.Add(unassignedSite);
                    devicesBySite[id]=[];
                }
                target=unassignedSite;
            }
            if(!assignments.TryGetValue(target.Id,out var list))assignments[target.Id]=list=[];
            list.Add(row);
        }

        var touched=new List<Site>();
        foreach(var assignment in assignments)
        {
            var site=sites.First(x=>x.Id==assignment.Key);
            ImportRmmSite(site,path,assignment.Value,devicesBySite[site.Id]);
            touched.Add(site);
        }
        return touched;
    }

    private void ImportRmmSite(Site site,string path,List<ImportRow> rows,List<Device> siteDevices)
    {
        using var c=db.BeginConnection(); using var tx=c.BeginTransaction();
        var now=DateTimeOffset.Now.ToString("O");
        var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var source=Scalar(c,"INSERT INTO evidence_sources(site_id,file_name,source_type,imported_at,content_hash) VALUES($s,$f,'RMM',$at,$h); SELECT last_insert_rowid();",("$s",site.Id),("$f",Path.GetFileName(path)),("$at",now),("$h",hash));

        foreach(var row in rows)
        {
            var device=BestCompanyIdentityMatch(row,siteDevices);
            if(device is null)
            {
                var type=DeviceClassifier.Type(row.Name,row.DeviceType,row.Description);var zone=DeviceClassifier.Zone(row.Name,row.Zone);
                var id=Scalar(c,"INSERT INTO devices(site_id,name,ip_address,mac_address,serial_number,vendor,model,device_type,zone,description) VALUES($s,$n,$ip,$mac,$ser,$v,$m,$t,$z,$desc); SELECT last_insert_rowid();",("$s",site.Id),("$n",row.Name),("$ip",row.IpAddress),("$mac",row.MacAddress),("$ser",row.SerialNumber),("$v",row.Vendor),("$m",row.Model),("$t",type),("$z",zone),("$desc",row.Description));
                device=new Device(id,site.Id,row.Name,row.IpAddress,row.MacAddress,row.SerialNumber,row.Vendor,row.Model,type,zone,row.Description);siteDevices.Add(device);
                AddFacts(c,site.Id,id,row,source,"confirmed",.95);
                continue;
            }

            if(!string.IsNullOrWhiteSpace(row.IpAddress) && !Same(device.IpAddress,row.IpAddress) && _authority.IsAuthoritative("RMM","ip_address"))
            {
                Exec(c,"UPDATE facts SET status='superseded' WHERE site_id=$s AND entity_type='device' AND entity_id=$d AND field_name='ip_address' AND value<>$v AND status<>'superseded'",("$s",site.Id),("$d",device.Id),("$v",row.IpAddress));
                if(!string.IsNullOrWhiteSpace(device.IpAddress)) AddFact(c,site.Id,device.Id,"ip_address",device.IpAddress!,source,"superseded",.99);
                Exec(c,"UPDATE devices SET ip_address=$ip WHERE id=$id",("$ip",row.IpAddress),("$id",device.Id));
                AddFact(c,site.Id,device.Id,"ip_address",row.IpAddress!,source,"confirmed",.99);
                device=device with{IpAddress=row.IpAddress};
                var index=siteDevices.FindIndex(x=>x.Id==device.Id);if(index>=0)siteDevices[index]=device;
            }

            foreach(var conflict in EntityMatcher.Conflicts(row,device).Where(x=>x.Field!="ip_address"))
            {
                Exec(c,"INSERT INTO conflicts(site_id,device_id,entity_name,field_name,existing_value,new_value,source_id) VALUES($s,$d,$n,$f,$old,$new,$src)",("$s",site.Id),("$d",device.Id),("$n",device.Name),("$f",conflict.Field),("$old",conflict.Existing),("$new",conflict.Proposed),("$src",source));
                AddFact(c,site.Id,device.Id,conflict.Field,conflict.Proposed!,source,"conflicting",.75);
            }

            Exec(c,"UPDATE devices SET mac_address=COALESCE(mac_address,$mac),serial_number=COALESCE(serial_number,$ser),vendor=COALESCE(vendor,$v),model=COALESCE(model,$m),device_type=COALESCE(device_type,$t),zone=COALESCE(zone,$z),description=COALESCE(description,$desc) WHERE id=$id",("$mac",row.MacAddress),("$ser",row.SerialNumber),("$v",row.Vendor),("$m",row.Model),("$t",DeviceClassifier.Type(row.Name,row.DeviceType,row.Description)),("$z",DeviceClassifier.Zone(row.Name,row.Zone)),("$desc",row.Description),("$id",device.Id));
            AddFacts(c,site.Id,device.Id,row,source,"confirmed",.95);
        }
        tx.Commit();
    }

    private Site? InferSiteFromNetwork(string? ipAddress,List<Site> sites)
    {
        if(!IPAddress.TryParse(ipAddress,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return null;
        var matches=new List<Site>();
        foreach(var site in sites.Where(x=>!x.Name.Equals("Unassigned Devices",StringComparison.OrdinalIgnoreCase)))
            if(db.Networks(site.Id).Any(n=>Contains(n.Cidr,ip)))matches.Add(site);
        return matches.Count==1?matches[0]:null;
    }

    private static bool Contains(string cidr,IPAddress ip)
    {
        var parts=cidr.Split('/');if(parts.Length!=2||!IPAddress.TryParse(parts[0],out var network)||!int.TryParse(parts[1],out var prefix)||prefix<0||prefix>32)return false;
        var a=network.GetAddressBytes();var b=ip.GetAddressBytes();if(a.Length!=4||b.Length!=4)return false;
        var bits=prefix;for(var i=0;i<4;i++){var mask=bits>=8?255:bits<=0?0:256-(1<<(8-bits));if((a[i]&mask)!=(b[i]&mask))return false;bits-=8;}return true;
    }

    private static Device? BestCompanyIdentityMatch(ImportRow row,IEnumerable<Device> devices)
        => devices.Select(d=>(Device:d,Score:RmmIdentityScore(row,d))).Where(x=>x.Score>=90).OrderByDescending(x=>x.Score).Select(x=>x.Device).FirstOrDefault();

    private static int RmmIdentityScore(ImportRow r,Device d)
    {
        var score=0;if(Same(r.SerialNumber,d.SerialNumber))score+=120;if(Same(r.MacAddress,d.MacAddress))score+=120;if(Same(r.Name,d.Name))score+=90;if(Same(r.Name,d.Name)&&Same(r.Vendor,d.Vendor)&&Same(r.Model,d.Model))score+=20;if(Same(r.IpAddress,d.IpAddress))score+=35;return score;
    }

    private static bool IsRmmSource(string path)
    {
        var name=Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("rmm")||name.Contains("datto")||name.Contains("kaseya")||name.Contains("ncentral")||name.Contains("n-central")||name.Contains("n-able")||name.Contains("nable");
    }

    private static bool Same(string? a,string? b)=>!string.IsNullOrWhiteSpace(a)&&string.Equals(a.Trim(),b?.Trim(),StringComparison.OrdinalIgnoreCase);

    private void AddDocumentedRelationships(SqliteConnection c,long siteId,string path,List<ImportRow> importedRows,List<Device> devices,long source)
    {
        foreach(var row in importedRows)
        {
            var device=devices.FirstOrDefault(x=>x.Name.Equals(row.Name,StringComparison.OrdinalIgnoreCase))??EntityMatcher.BestMatch(row,devices);if(device is null)continue;var type=DeviceClassifier.Type(row.Name,row.DeviceType,row.Description);
            if(type.Equals("Virtual Machine",StringComparison.OrdinalIgnoreCase)&&Meaningful(row.HostedLocation))
            {
                var host=devices.FirstOrDefault(x=>x.Name.Equals(row.HostedLocation!.Trim(),StringComparison.OrdinalIgnoreCase));var hostedInCloud=row.HostedLocation!.Trim().Equals("Azure",StringComparison.OrdinalIgnoreCase);var target=host is not null?new EntityRef("device",host.Id,host.Name):new EntityRef(hostedInCloud?"platform":"unresolved_device",null,row.HostedLocation.Trim());var confidence=host is not null||hostedInCloud?0.92:0.6;
                db.AddSourcedRelationship(c,siteId,new("device",device.Id,device.Name),target,"RUNS_ON",source,Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)?"All Sites":null,confidence,"inferred",$"Hosted location column: {row.HostedLocation}");
            }
            if(Meaningful(row.ConnectedSwitch))
            {
                var switchName=row.ConnectedSwitch!.Trim();var networkDevice=devices.FirstOrDefault(x=>x.Name.Equals(switchName,StringComparison.OrdinalIgnoreCase));var target=networkDevice is not null?new EntityRef("device",networkDevice.Id,networkDevice.Name):new EntityRef("unresolved_device",null,switchName);var port=Meaningful(row.SwitchPort)?row.SwitchPort!.Trim():null;db.AddSourcedRelationship(c,siteId,new("device",device.Id,device.Name),target,"CONNECTED_TO",source,Path.GetExtension(path).Equals(".xlsx",StringComparison.OrdinalIgnoreCase)?"All Sites":null,networkDevice is null?.6:.9,"inferred",$"Switch column: {switchName}"+(port is null?"":$"; switch port {port}"),port is null?null:$"{device.Name}:{port}");
            }
        }
    }

    public void Resolve(Conflict conflict,Resolution resolution,string? manual)
    {
        using var c=db.BeginConnection(); var value=resolution==Resolution.Manual?manual:conflict.NewValue;
        if(resolution is Resolution.AcceptNew or Resolution.Manual) Exec(c,$"UPDATE devices SET {Column(conflict.FieldName)}=$v WHERE id=$id",("$v",value),("$id",conflict.DeviceId));
        if(resolution==Resolution.SeparateDevice) Exec(c,"INSERT INTO devices(site_id,name,ip_address) VALUES($s,$n,$ip)",("$s",conflict.SiteId),("$n",conflict.EntityName+" (separate)"),("$ip",conflict.FieldName=="ip_address"?conflict.NewValue:null));
        Exec(c,"UPDATE conflicts SET status=$s,resolved_at=$at WHERE id=$id",("$s",resolution.ToString()),("$at",DateTimeOffset.Now.ToString("O")),("$id",conflict.Id));
    }
    private static void AddFacts(SqliteConnection c,long site,long device,ImportRow r,long source,string status,double confidence) { foreach(var x in new[]{("name",r.Name),("ip_address",r.IpAddress),("mac_address",r.MacAddress),("serial_number",r.SerialNumber),("vendor",r.Vendor),("model",r.Model),("device_type",DeviceClassifier.Type(r.Name,r.DeviceType,r.Description)),("zone",DeviceClassifier.Zone(r.Name,r.Zone)),("description",r.Description),("hosted_location",r.HostedLocation),("connected_switch",r.ConnectedSwitch),("switch_port",r.SwitchPort),("gateway",r.Gateway)}) if(!string.IsNullOrWhiteSpace(x.Item2))AddFact(c,site,device,x.Item1,x.Item2!,source,status,confidence); }
    private static bool Meaningful(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Trim() is not("n/a" or "N/A" or "NA" or "Physical" or "physical");
    private static string RelationshipPreview(ImportRow row){var values=new List<string>();if(DeviceClassifier.Type(row.Name,row.DeviceType,row.Description)=="Virtual Machine"&&Meaningful(row.HostedLocation))values.Add($"runs on {row.HostedLocation}");if(Meaningful(row.ConnectedSwitch))values.Add($"connects to {row.ConnectedSwitch}"+(Meaningful(row.SwitchPort)?$" port {row.SwitchPort}":""));return values.Count==0?"":" · "+string.Join(" · ",values);}
    private static void AddFact(SqliteConnection c,long site,long device,string field,string value,long source,string status,double confidence)
    {
        using var find=c.CreateCommand(); find.CommandText="SELECT id FROM facts WHERE site_id=$s AND entity_type='device' AND entity_id=$d AND field_name=$f AND value=$v LIMIT 1"; foreach(var p in new[]{("$s",(object)site),("$d",device),("$f",field),("$v",value)})find.Parameters.AddWithValue(p.Item1,p.Item2);
        var existing=find.ExecuteScalar(); long fact;
        if(existing is long id){fact=id;Exec(c,"UPDATE facts SET confidence=MIN(0.99,confidence+0.1),status=CASE WHEN status='conflicting' THEN status ELSE $st END WHERE id=$id",("$st",status=="superseded"?"superseded":"confirmed"),("$id",id));}
        else fact=Scalar(c,"INSERT INTO facts(site_id,entity_type,entity_id,field_name,value,confidence,status) VALUES($s,'device',$d,$f,$v,$c,$st); SELECT last_insert_rowid();",("$s",site),("$d",device),("$f",field),("$v",value),("$c",confidence),("$st",status));
        Exec(c,"INSERT OR IGNORE INTO fact_evidence(fact_id,source_id) VALUES($f,$s)",("$f",fact),("$s",source));
    }
    private static string Column(string f)=>f is "name" or "ip_address" or "mac_address" or "serial_number" or "vendor" or "model"?f:throw new InvalidOperationException("Unsupported field");
    private static void Exec(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);x.ExecuteNonQuery();}
    private static long Scalar(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);return (long)x.ExecuteScalar()!;}
}
