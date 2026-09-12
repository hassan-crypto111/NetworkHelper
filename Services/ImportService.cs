using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class ImportService(Database db)
{
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
        // A second pass is intentional: a VM can appear before its host or switch in the workbook.
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
        tx.Commit();
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
        if(existing is long id){fact=id;Exec(c,"UPDATE facts SET confidence=MIN(0.99,confidence+0.1),status=CASE WHEN status='conflicting' THEN status ELSE 'confirmed' END WHERE id=$id",("$id",id));}
        else fact=Scalar(c,"INSERT INTO facts(site_id,entity_type,entity_id,field_name,value,confidence,status) VALUES($s,'device',$d,$f,$v,$c,$st); SELECT last_insert_rowid();",("$s",site),("$d",device),("$f",field),("$v",value),("$c",confidence),("$st",status));
        Exec(c,"INSERT OR IGNORE INTO fact_evidence(fact_id,source_id) VALUES($f,$s)",("$f",fact),("$s",source));
    }
    private static string Column(string f)=>f is "name" or "ip_address" or "mac_address" or "serial_number" or "vendor" or "model"?f:throw new InvalidOperationException("Unsupported field");
    private static void Exec(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);x.ExecuteNonQuery();}
    private static long Scalar(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);return (long)x.ExecuteScalar()!;}
}
