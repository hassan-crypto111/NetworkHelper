using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class VisioImportService(Database db)
{
    public void ClassifyForCompany(long companyId,IReadOnlyList<VisioProposal> proposals)
    {
        var sites=db.Sites(companyId);
        foreach(var page in proposals.GroupBy(x=>x.SourcePage))
        {
            var site=FindSite(page.Key,sites);
            foreach(var p in page){p.DestinationSite=site?.Name;if(site is null){p.Status="Unresolved site";p.Import=false;}}
            if(site is not null)Classify(site.Id,page.ToList());
            else foreach(var hintGroup in page.Where(x=>!string.IsNullOrWhiteSpace(x.DetectedSite)).GroupBy(x=>x.DetectedSite!,StringComparer.OrdinalIgnoreCase))
            {
                var hintedSite=FindSite(hintGroup.Key,sites);
                foreach(var p in hintGroup){p.DestinationSite=hintedSite?.Name??hintGroup.Key;p.Import=p.Kind!=VisioProposalKind.Device||p.Confidence>=.75;p.Status=hintedSite is null?$"New site (review before import)":$"Detected site: {hintedSite.Name}";}
                if(hintedSite is not null)Classify(hintedSite.Id,hintGroup.ToList());
            }
            var byShape=page.Where(x=>x.Kind==VisioProposalKind.Device&&x.SourceShapeId is not null).GroupBy(x=>x.SourceShapeId!).ToDictionary(x=>x.Key,x=>x.First());
            foreach(var rel in page.Where(x=>x.Kind==VisioProposalKind.Relationship&&x.SourceShapeId is not null&&x.RelatedShapeId is not null))
                if(byShape.TryGetValue(rel.SourceShapeId!,out var a)&&byShape.TryGetValue(rel.RelatedShapeId!,out var b)&&a.DestinationSite is not null&&a.DestinationSite.Equals(b.DestinationSite,StringComparison.OrdinalIgnoreCase)){rel.DestinationSite=a.DestinationSite;rel.Status="Proposed relationship";rel.Import=a.Import&&b.Import;}
        }
    }
    public IReadOnlyList<Site> ImportForCompany(long companyId,string path,IReadOnlyList<VisioProposal> proposals)
    {
        var sites=db.Sites(companyId);var touched=new List<Site>();
        foreach(var group in proposals.Where(x=>x.Import&&!string.IsNullOrWhiteSpace(x.DestinationSite)).GroupBy(x=>x.DestinationSite!,StringComparer.OrdinalIgnoreCase))
        {var site=sites.FirstOrDefault(x=>x.Name.Equals(group.Key,StringComparison.OrdinalIgnoreCase));if(site is null){var id=db.AddSite(companyId,group.Key,null);site=new Site(id,companyId,group.Key,null);sites.Add(site);}Import(site.Id,path,group.ToList());touched.Add(site);}
        return touched;
    }
    public void Classify(long siteId,IReadOnlyList<VisioProposal> proposals)
    {
        var devices=db.Devices(siteId);
        foreach(var proposal in proposals.Where(x=>x.Kind==VisioProposalKind.Device))
        {
            var ip=proposals.FirstOrDefault(x=>x.Kind==VisioProposalKind.IpAddress&&x.ParentShapeId==proposal.SourceShapeId)?.ProposedValue;
            var row=new ImportRow(proposal.ProposedValue,ip,null,null,null,null,null,null);
            var match=EntityMatcher.BestMatch(row,devices); var conflicts=match is null?[]:EntityMatcher.Conflicts(row,match);
            proposal.MatchedExistingEntity=match?.Name;
            proposal.Status=match is null?"New":conflicts.Count>0?"Conflict":"Existing Match / Supporting Evidence";
            foreach(var child in proposals.Where(x=>x.ParentShapeId==proposal.SourceShapeId&&x.Kind==VisioProposalKind.IpAddress)){child.MatchedExistingEntity=match?.Name;child.Status=proposal.Status;}
        }
        foreach(var p in proposals.Where(x=>x.Kind is VisioProposalKind.Vlan or VisioProposalKind.Subnet))p.Status="New or supporting evidence";
        foreach(var p in proposals.Where(x=>x.Kind==VisioProposalKind.Relationship))p.Status="Proposed relationship";
    }
    private static Site? FindSite(string page,IReadOnlyList<Site> sites)
    {
        static string N(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var pn=N(page);var scored=sites.Select(s=>{var sn=N(s.Name);var initials=new string(s.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(x=>char.ToLowerInvariant(x[0])).ToArray());var score=pn==sn?100:pn.Length>=5&&(pn.Contains(sn)||sn.Contains(pn))?90:initials.Length>=2&&(pn==initials||pn.StartsWith(initials))?85:page.Split(new[]{' ','(',')','-','&'},StringSplitOptions.RemoveEmptyEntries).Any(x=>x.Length>=5&&(sn.StartsWith(N(x))||N(x).StartsWith(sn)))?80:0;return(Site:s,Score:score);}).OrderByDescending(x=>x.Score).ToList();
        return scored.Count>0&&scored[0].Score>=80&&(scored.Count==1||scored[0].Score>scored[1].Score)?scored[0].Site:null;
    }

    public void Import(long siteId,string path,IReadOnlyList<VisioProposal> proposals)
    {
        using var c=db.BeginConnection(); using var tx=c.BeginTransaction();
        var source=Scalar(c,"INSERT INTO evidence_sources(site_id,file_name,source_type,imported_at,content_hash) VALUES($s,$f,'VSDX',$at,$h); SELECT last_insert_rowid();",("$s",siteId),("$f",Path.GetFileName(path)),("$at",DateTimeOffset.Now.ToString("O")),("$h",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        var devices=db.Devices(siteId); var shapeDevices=new Dictionary<string,long>();
        foreach(var proposal in proposals.Where(x=>x.Import&&x.Kind==VisioProposalKind.Device))
        {
            var ip=proposals.FirstOrDefault(x=>x.Import&&x.Kind==VisioProposalKind.IpAddress&&x.ParentShapeId==proposal.SourceShapeId)?.ProposedValue;
            var row=new ImportRow(proposal.ProposedValue,ip,null,null,null,null,null,null); var match=EntityMatcher.BestMatch(row,devices); long deviceId;
            if(match is null)
            {
                var type=proposal.DeviceType??DeviceClassifier.Type(row.Name);var zone=proposal.NetworkZone??DeviceClassifier.Zone(row.Name);deviceId=Scalar(c,"INSERT INTO devices(site_id,name,ip_address,device_type,zone) VALUES($s,$n,$ip,$t,$z); SELECT last_insert_rowid();",("$s",siteId),("$n",row.Name),("$ip",row.IpAddress),("$t",type),("$z",zone));
                match=new Device(deviceId,siteId,row.Name,row.IpAddress,null,null,null,null,type,zone,null); devices.Add(match);
                AddFact(c,siteId,deviceId,"name",row.Name,source,"inferred",proposal.Confidence);
                if(ip is not null)AddFact(c,siteId,deviceId,"ip_address",ip,source,"inferred",proposal.Confidence);
            }
            else
            {
                deviceId=match.Id;var type=proposal.DeviceType??DeviceClassifier.Type(row.Name);var zone=proposal.NetworkZone??DeviceClassifier.Zone(row.Name);Exec(c,"UPDATE devices SET ip_address=COALESCE(ip_address,$ip),device_type=COALESCE(device_type,$t),zone=COALESCE(zone,$z) WHERE id=$id",("$ip",row.IpAddress),("$t",type),("$z",zone),("$id",deviceId)); var conflicts=EntityMatcher.Conflicts(row,match);
                if(conflicts.Count==0){AddFact(c,siteId,deviceId,"name",row.Name,source,"confirmed",Math.Min(.95,proposal.Confidence+.05));if(ip is not null)AddFact(c,siteId,deviceId,"ip_address",ip,source,"confirmed",Math.Min(.95,proposal.Confidence+.05));}
                foreach(var conflict in conflicts){Exec(c,"INSERT INTO conflicts(site_id,device_id,entity_name,field_name,existing_value,new_value,source_id) VALUES($s,$d,$n,$f,$old,$new,$src)",("$s",siteId),("$d",deviceId),("$n",match.Name),("$f",conflict.Field),("$old",conflict.Existing),("$new",conflict.Proposed),("$src",source));AddFact(c,siteId,deviceId,conflict.Field,conflict.Proposed!,source,"conflicting",proposal.Confidence);}
            }
            if(!string.IsNullOrWhiteSpace(proposal.Details))AddFact(c,siteId,deviceId,"visio_shape_metadata",proposal.Details,source,"inferred",proposal.Confidence);
            if(proposal.SourceShapeId is not null)shapeDevices[proposal.SourceShapeId]=deviceId;
        }
        foreach(var p in proposals.Where(x=>x.Import&&x.Kind==VisioProposalKind.Vlan&&int.TryParse(x.ProposedValue,out _))){var vlan=int.Parse(p.ProposedValue);var label=p.Details?.Split(" — inferred",StringSplitOptions.None)[0];if(string.IsNullOrWhiteSpace(label)||label.StartsWith("VLAN",StringComparison.OrdinalIgnoreCase))label=$"VLAN {vlan}";Exec(c,"INSERT OR IGNORE INTO vlans(site_id,vlan_number,name,zone) VALUES($s,$v,$n,$z)",("$s",siteId),("$v",vlan),("$n",label),("$z",p.NetworkZone));Exec(c,"UPDATE vlans SET zone=COALESCE(zone,$z),name=CASE WHEN name LIKE 'VLAN %' THEN $n ELSE name END WHERE site_id=$s AND vlan_number=$v",("$z",p.NetworkZone),("$n",label),("$s",siteId),("$v",vlan));}
        foreach(var p in proposals.Where(x=>x.Import&&x.Kind==VisioProposalKind.Subnet)){var third=p.ProposedValue.Split('/')[0].Split('.').ElementAtOrDefault(2);var linked=proposals.FirstOrDefault(x=>x.Import&&x.Kind==VisioProposalKind.Vlan&&x.SourceShapeId==p.SourceShapeId&&(x.Details?.Contains(p.ProposedValue,StringComparison.OrdinalIgnoreCase)==true||p.ProposedValue.EndsWith("/24")&&x.ProposedValue==third));int? vlanId=linked is not null&&int.TryParse(linked.ProposedValue,out var parsed)?parsed:null;Exec(c,"INSERT INTO networks(site_id,name,cidr,vlan_id,zone) SELECT $s,$n,$c,$v,$z WHERE NOT EXISTS(SELECT 1 FROM networks WHERE site_id=$s AND cidr=$c)",("$s",siteId),("$n",p.ProposedValue),("$c",p.ProposedValue),("$v",vlanId),("$z",p.NetworkZone));Exec(c,"UPDATE networks SET zone=COALESCE(zone,$z),vlan_id=COALESCE(vlan_id,$v) WHERE site_id=$s AND cidr=$c",("$z",p.NetworkZone),("$v",vlanId),("$s",siteId),("$c",p.ProposedValue));}
        foreach(var p in proposals.Where(x=>x.Import&&x.Kind==VisioProposalKind.Relationship))
        {
            if(p.SourceShapeId is null||p.RelatedShapeId is null||!shapeDevices.TryGetValue(p.SourceShapeId,out var from)||!shapeDevices.TryGetValue(p.RelatedShapeId,out var to)||from==to)continue;
            var existing=OptionalScalar(c,"SELECT id FROM topology_relationships WHERE site_id=$s AND relationship_type='CONNECTED_TO' AND ((from_device_id=$a AND to_device_id=$b) OR (from_device_id=$b AND to_device_id=$a)) LIMIT 1",("$s",siteId),("$a",from),("$b",to)); long relationship;
            if(existing.HasValue){relationship=existing.Value;Exec(c,"UPDATE topology_relationships SET confidence=MIN(0.99,confidence+0.1),status='confirmed' WHERE id=$id",("$id",relationship));}
            else relationship=Scalar(c,"INSERT INTO topology_relationships(site_id,from_device_id,to_device_id,source_id,source_page,from_shape_id,to_shape_id,confidence,status) VALUES($s,$a,$b,$src,$page,$as,$bs,$c,'inferred'); SELECT last_insert_rowid();",("$s",siteId),("$a",from),("$b",to),("$src",source),("$page",p.SourcePage),("$as",p.SourceShapeId),("$bs",p.RelatedShapeId),("$c",p.Confidence));
            Exec(c,"INSERT OR IGNORE INTO topology_relationship_evidence(relationship_id,source_id,source_page,from_shape_id,to_shape_id) VALUES($r,$s,$p,$a,$b)",("$r",relationship),("$s",source),("$p",p.SourcePage),("$a",p.SourceShapeId),("$b",p.RelatedShapeId));
            var fromName=devices.First(x=>x.Id==from).Name;var toName=devices.First(x=>x.Id==to).Name;var generic=OptionalScalar(c,"SELECT id FROM entity_relationships WHERE site_id=$s AND relationship_type='CONNECTED_TO' AND status<>'superseded' AND ((from_entity_id=$a AND to_entity_id=$b) OR (from_entity_id=$b AND to_entity_id=$a)) LIMIT 1",("$s",siteId),("$a",from),("$b",to));
            if(!generic.HasValue)generic=Scalar(c,"INSERT INTO entity_relationships(site_id,from_entity_type,from_entity_id,from_entity_name,to_entity_type,to_entity_id,to_entity_name,relationship_type,source_id,source_page,confidence,status,basis,created_at,last_verified_at) VALUES($s,'device',$a,$an,'device',$b,$bn,'CONNECTED_TO',$src,$page,$c,'inferred',$basis,$at,$at); SELECT last_insert_rowid();",("$s",siteId),("$a",from),("$an",fromName),("$b",to),("$bn",toName),("$src",source),("$page",p.SourcePage),("$c",p.Confidence),("$basis",$"Visio connector shapes {p.SourceShapeId} and {p.RelatedShapeId}"),("$at",DateTimeOffset.Now.ToString("O")));
            Exec(c,"INSERT OR IGNORE INTO relationship_evidence(relationship_id,source_id,basis) VALUES($r,$s,$b)",("$r",generic.Value),("$s",source),("$b",$"Page {p.SourcePage}; shapes {p.SourceShapeId}, {p.RelatedShapeId}"));
        }
        tx.Commit();
    }
    private static void AddFact(SqliteConnection c,long site,long device,string field,string value,long source,string status,double confidence)
    {
        var fact=OptionalScalar(c,"SELECT id FROM facts WHERE site_id=$s AND entity_type='device' AND entity_id=$d AND field_name=$f AND value=$v LIMIT 1",("$s",site),("$d",device),("$f",field),("$v",value));
        if(fact.HasValue)Exec(c,"UPDATE facts SET confidence=MIN(0.99,confidence+0.1),status=CASE WHEN status='conflicting' THEN status ELSE 'confirmed' END WHERE id=$id",("$id",fact.Value));
        else fact=Scalar(c,"INSERT INTO facts(site_id,entity_type,entity_id,field_name,value,confidence,status) VALUES($s,'device',$d,$f,$v,$c,$st); SELECT last_insert_rowid();",("$s",site),("$d",device),("$f",field),("$v",value),("$c",confidence),("$st",status));
        Exec(c,"INSERT OR IGNORE INTO fact_evidence(fact_id,source_id) VALUES($f,$s)",("$f",fact!.Value),("$s",source));
    }
    private static void Exec(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=Command(c,sql,ps);x.ExecuteNonQuery();}
    private static long Scalar(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=Command(c,sql,ps);return(long)x.ExecuteScalar()!;}
    private static long? OptionalScalar(SqliteConnection c,string sql,params (string,object?)[] ps){using var x=Command(c,sql,ps);var value=x.ExecuteScalar();return value is long id?id:null;}
    private static SqliteCommand Command(SqliteConnection c,string sql,(string,object?)[] ps){var x=c.CreateCommand();x.CommandText=sql;foreach(var p in ps)x.Parameters.AddWithValue(p.Item1,p.Item2??DBNull.Value);return x;}
}
