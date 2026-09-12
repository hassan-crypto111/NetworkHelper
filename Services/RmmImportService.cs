using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class RmmImportService(Database db)
{
    public bool IsRmm(IReadOnlyCollection<ImportRow> rows)=>rows.Count>0&&rows.All(x=>x.IsRmm);

    public void ClassifyForCompany(long companyId,IEnumerable<ImportRow> rows)
    {
        var sites=db.Sites(companyId);
        var devices=sites.SelectMany(s=>db.Devices(s.Id).Select(d=>(Site:s,Device:d))).ToList();
        foreach(var row in rows)
        {
            row.RmmStatus="";row.DestinationSite=null;
            var byName=devices.Where(x=>Same(x.Device.Name,row.Name)).ToList();
            if(byName.Count==1)
            {
                var match=byName[0];row.DestinationSite=match.Site.Name;
                if(Same(match.Device.IpAddress,row.IpAddress))
                {
                    row.RmmStatus="✓";
                    row.Match=$"✓ RMM verified · hostname + IP match · {match.Site.Name}";
                }
                else
                {
                    row.RmmStatus="⚠";
                    row.Match=$"⚠ Hostname match · IP differs · current {match.Device.IpAddress??"(blank)"} · RMM {row.IpAddress??"(blank)"} · {match.Site.Name}";
                }
                continue;
            }
            if(byName.Count>1)
            {
                row.RmmStatus="⚠";row.Match="⚠ Duplicate hostname exists across sites · review before import";row.Import=false;continue;
            }
            var ipMatch=devices.Where(x=>Same(x.Device.IpAddress,row.IpAddress)).ToList();
            if(ipMatch.Count==1)
            {
                row.RmmStatus="⚠";row.DestinationSite=ipMatch[0].Site.Name;
                row.Match=$"⚠ IP matches {ipMatch[0].Device.Name} but hostname differs · review";row.Import=false;continue;
            }
            var inferred=InferSite(row.IpAddress,sites);
            row.DestinationSite=inferred?.Name??"Unassigned Devices";
            row.Match=inferred is null?$"New RMM device · Unassigned Devices":$"New RMM device · site inferred from subnet: {inferred.Name}";
        }
    }

    public IReadOnlyList<Site> ImportForCompany(long companyId,string path,IEnumerable<ImportRow> rows)
    {
        var selected=rows.Where(x=>x.Import).ToList();
        var sites=db.Sites(companyId);
        var touched=new Dictionary<long,Site>();
        var all=sites.SelectMany(s=>db.Devices(s.Id).Select(d=>(Site:s,Device:d))).ToList();
        Site? unassigned=sites.FirstOrDefault(x=>x.Name.Equals("Unassigned Devices",StringComparison.OrdinalIgnoreCase));

        foreach(var row in selected)
        {
            var byName=all.Where(x=>Same(x.Device.Name,row.Name)).ToList();
            if(byName.Count>1)continue;
            if(byName.Count==1)
            {
                var match=byName[0];ImportExisting(match.Site,match.Device,path,row);touched[match.Site.Id]=match.Site;continue;
            }
            var target=InferSite(row.IpAddress,sites);
            if(target is null)
            {
                if(unassigned is null)
                {
                    var id=db.AddSite(companyId,"Unassigned Devices",null);unassigned=new Site(id,companyId,"Unassigned Devices",null);sites.Add(unassigned);
                }
                target=unassigned;
            }
            ImportNew(target,path,row);touched[target.Id]=target;
            all.Add((target,db.Devices(target.Id).OrderByDescending(x=>x.Id).First(x=>Same(x.Name,row.Name))));
        }
        return touched.Values.ToList();
    }

    private void ImportExisting(Site site,Device device,string path,ImportRow row)
    {
        using var c=db.BeginConnection();using var tx=c.BeginTransaction();var source=AddSource(c,site.Id,path);
        if(!string.IsNullOrWhiteSpace(row.IpAddress)&&!Same(device.IpAddress,row.IpAddress))
        {
            Exec(c,"UPDATE facts SET status='superseded' WHERE site_id=$s AND entity_type='device' AND entity_id=$d AND field_name='ip_address' AND value<>$v AND status<>'superseded'",("$s",site.Id),("$d",device.Id),("$v",row.IpAddress));
            if(!string.IsNullOrWhiteSpace(device.IpAddress))AddFact(c,site.Id,device.Id,"ip_address",device.IpAddress!,source,"superseded",.99);
            Exec(c,"UPDATE devices SET ip_address=$ip WHERE id=$id",("$ip",row.IpAddress),("$id",device.Id));
        }
        AddFact(c,site.Id,device.Id,"name",row.Name,source,"confirmed",.99);
        if(!string.IsNullOrWhiteSpace(row.IpAddress))AddFact(c,site.Id,device.Id,"ip_address",row.IpAddress!,source,"confirmed",.99);
        AddFact(c,site.Id,device.Id,"rmm_identity_verified","true",source,"confirmed",1.0);
        Exec(c,"UPDATE devices SET device_type=COALESCE(device_type,$t) WHERE id=$id",("$t",row.DeviceType),("$id",device.Id));
        tx.Commit();
    }

    private void ImportNew(Site site,string path,ImportRow row)
    {
        using var c=db.BeginConnection();using var tx=c.BeginTransaction();var source=AddSource(c,site.Id,path);
        var id=Scalar(c,"INSERT INTO devices(site_id,name,ip_address,mac_address,serial_number,vendor,model,device_type,zone,description) VALUES($s,$n,$ip,$mac,$ser,$v,$m,$t,$z,$desc); SELECT last_insert_rowid();",("$s",site.Id),("$n",row.Name),("$ip",row.IpAddress),("$mac",row.MacAddress),("$ser",row.SerialNumber),("$v",row.Vendor),("$m",row.Model),("$t",row.DeviceType),("$z",row.Zone),("$desc",row.Description));
        AddFact(c,site.Id,id,"name",row.Name,source,"confirmed",.95);if(!string.IsNullOrWhiteSpace(row.IpAddress))AddFact(c,site.Id,id,"ip_address",row.IpAddress!,source,"confirmed",.95);
        tx.Commit();
    }

    private long AddSource(SqliteConnection c,long siteId,string path)=>Scalar(c,"INSERT INTO evidence_sources(site_id,file_name,source_type,imported_at,content_hash) VALUES($s,$f,'RMM',$at,$h); SELECT last_insert_rowid();",("$s",siteId),("$f",Path.GetFileName(path)),("$at",DateTimeOffset.Now.ToString("O")),("$h",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private Site? InferSite(string? ip,List<Site> sites)
    {
        if(!IPAddress.TryParse(ip,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return null;var matches=new List<Site>();
        foreach(var site in sites.Where(x=>!x.Name.Equals("Unassigned Devices",StringComparison.OrdinalIgnoreCase)))if(db.Networks(site.Id).Any(n=>Contains(n.Cidr,address)))matches.Add(site);
        return matches.Count==1?matches[0]:null;
    }
    private static bool Contains(string cidr,IPAddress ip){var p=cidr.Split('/');if(p.Length!=2||!IPAddress.TryParse(p[0],out var n)||!int.TryParse(p[1],out var prefix)||prefix<0||prefix>32)return false;var a=n.GetAddressBytes();var b=ip.GetAddressBytes();var bits=prefix;for(var i=0;i<4;i++){var mask=bits>=8?255:bits<=0?0:256-(1<<(8-bits));if((a[i]&mask)!=(b[i]&mask))return false;bits-=8;}return true;}
    private static bool Same(string? a,string? b)=>!string.IsNullOrWhiteSpace(a)&&string.Equals(a.Trim(),b?.Trim(),StringComparison.OrdinalIgnoreCase);
    private static void AddFact(SqliteConnection c,long site,long device,string field,string value,long source,string status,double confidence){var id=Scalar(c,"INSERT INTO facts(site_id,entity_type,entity_id,field_name,value,confidence,status) VALUES($s,'device',$d,$f,$v,$c,$st); SELECT last_insert_rowid();",("$s",site),("$d",device),("$f",field),("$v",value),("$c",confidence),("$st",status));Exec(c,"INSERT OR IGNORE INTO fact_evidence(fact_id,source_id) VALUES($f,$s)",("$f",id),("$s",source));}
    private static void Exec(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);x.ExecuteNonQuery();}
    private static long Scalar(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);return (long)x.ExecuteScalar()!;}
}
