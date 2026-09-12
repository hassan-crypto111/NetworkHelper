using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NetworkHelper.Domain;
using NetworkHelper.Services;

namespace NetworkHelper.Ingestion;

public interface IVisioImporter { bool Supports(string path); VisioImportResult Read(string path); }

public sealed partial class VisioImporter : IVisioImporter
{
    public bool Supports(string path)=>Path.GetExtension(path).Equals(".vsdx",StringComparison.OrdinalIgnoreCase);

    public VisioImportResult Read(string path)
    {
        if(!Supports(path))throw new NotSupportedException("Only Visio .vsdx files are supported.");
        using var zip=ZipFile.OpenRead(path);
        var pages=Xml(zip,"visio/pages/pages.xml")??throw new InvalidDataException("The Visio package has no pages document.");
        var rels=Relationships(zip,"visio/pages/_rels/pages.xml.rels");
        var proposals=new List<VisioProposal>();
        foreach(var page in pages.Descendants().Where(x=>x.Name.LocalName=="Page"))
        {
            var pageName=(string?)page.Attribute("Name")??(string?)page.Attribute("NameU")??$"Page {(string?)page.Attribute("ID")}";
            var relId=page.Attributes().FirstOrDefault(x=>x.Name.LocalName=="id")?.Value
                ??page.Elements().FirstOrDefault(x=>x.Name.LocalName=="Rel")?.Attributes().FirstOrDefault(x=>x.Name.LocalName=="id")?.Value;
            if(relId is null||!rels.TryGetValue(relId,out var target))continue;
            var pagePath=NormalizePagePath(target); var doc=Xml(zip,pagePath); if(doc is null)continue;
            ParsePage(doc,pageName,proposals);
        }
        return new VisioImportResult(proposals);
    }

    private static void ParsePage(XDocument doc,string page,List<VisioProposal> output)
    {
        var shapes=doc.Descendants().Where(x=>x.Name.LocalName=="Shape").Select(x=>ReadShape(x)).Where(x=>x.Id is not null&&x.Id!="4294967295").GroupBy(x=>x.Id!).ToDictionary(x=>x.Key,x=>x.First());
        var headings=shapes.Values.Where(IsSiteHeading).ToList();
        var recognised=new Dictionary<string,VisioProposal>();
        foreach(var shape in shapes.Values)
        {
            var ips=ExtractIpAddresses(shape.Text);
            var subnets=Cidr().Matches(shape.Text).Select(x=>x.Value).Distinct().ToList();
            var zone=ExtractZone(shape.Text);
            var name=DeviceName(shape,ips,subnets); var deviceLike=name is not null&&IsDeviceLike(shape,name,ips);
            if(deviceLike)
            {
                var strongName=StrongHostname().IsMatch(name!); var hasDeviceKeyword=DeviceKeyword().IsMatch(shape.Text+" "+shape.Name);
                var confidence=strongName&&hasDeviceKeyword?.92:strongName?.82:ips.Count>0&&name!.Length>=5?.76:.6;
                var metadata=string.IsNullOrWhiteSpace(shape.Metadata)?$"Shape={shape.Name}":$"Shape={shape.Name}; {shape.Metadata}";
                var hint=NearestSiteHeading(shape,headings);
                var deviceZone=zone??DeviceClassifier.Zone(name!);var deviceType=DeviceClassifier.Type(name!,null,shape.Name+" "+shape.Text);
                var device=new VisioProposal{Kind=VisioProposalKind.Device,ProposedValue=name!,Details=metadata,SourcePage=page,DetectedSite=hint,NetworkZone=deviceZone,DeviceType=deviceType,SourceShapeId=shape.Id,Confidence=confidence,Status="New",Import=confidence>=.75};
                output.Add(device); recognised[shape.Id!]=device;
                foreach(var ip in ips.Where(x=>!x.Contains('/')))output.Add(new VisioProposal{Kind=VisioProposalKind.IpAddress,ProposedValue=ip,ParentShapeId=shape.Id,SourceShapeId=shape.Id,SourcePage=page,DetectedSite=hint,NetworkZone=zone,Confidence=.88,Status="Proposed",Import=device.Import});
            }
            var siteHint=NearestSiteHeading(shape,headings);
            foreach(var subnet in subnets){var canonical=NormalizeCidr(subnet);output.Add(new VisioProposal{Kind=VisioProposalKind.Subnet,ProposedValue=canonical,Details=canonical==subnet?null:$"Observed as {subnet}",SourceShapeId=shape.Id,SourcePage=page,DetectedSite=siteHint,NetworkZone=zone,Confidence=.92,Status="New"});}
            foreach(Match vlan in Vlan().Matches(shape.Text))output.Add(new VisioProposal{Kind=VisioProposalKind.Vlan,ProposedValue=vlan.Groups[1].Value,Details=vlan.Value,SourceShapeId=shape.Id,SourcePage=page,DetectedSite=siteHint,NetworkZone=zone,Confidence=.88,Status="New"});
            if(VlanListHeading().IsMatch(shape.Text))
                foreach(Match item in NamedSubnet().Matches(shape.Text))
                {
                    var cidr=item.Groups[1].Value;var octets=cidr.Split('/')[0].Split('.');if(octets.Length!=4||!cidr.EndsWith("/24",StringComparison.Ordinal)||!int.TryParse(octets[2],out var inferredVlan)||inferredVlan is <1 or >4094)continue;
                    var vlanName=item.Groups[2].Value.Trim();output.Add(new VisioProposal{Kind=VisioProposalKind.Vlan,ProposedValue=inferredVlan.ToString(),Details=$"{vlanName} — inferred from {cidr}",SourceShapeId=shape.Id,SourcePage=page,DetectedSite=siteHint,NetworkZone=zone,Confidence=.78,Status="Inferred VLAN"});
                }
        }
        var connects=doc.Descendants().Where(x=>x.Name.LocalName=="Connect").Select(x=>new{From=(string?)x.Attribute("FromSheet"),To=(string?)x.Attribute("ToSheet")}).Where(x=>x.From is not null&&x.To is not null).ToList();
        var rawEdges=connects.GroupBy(x=>x.From!).Select(g=>new ConnectorEdge(g.Key,g.Select(x=>x.To!).Distinct().ToArray())).Where(x=>x.Endpoints.Length==2&&shapes.ContainsKey(x.Endpoints[0])&&shapes.ContainsKey(x.Endpoints[1])).ToList();
        var endpointIds=rawEdges.SelectMany(x=>x.Endpoints).Distinct().ToHashSet();var anchors=MapConnectorAnchors(endpointIds,recognised,shapes);var adjacency=new Dictionary<string,List<(string Other,string Connector)>>();
        foreach(var edge in rawEdges){Add(edge.Endpoints[0],edge.Endpoints[1],edge.ConnectorId);Add(edge.Endpoints[1],edge.Endpoints[0],edge.ConnectorId);}void Add(string from,string to,string connector){if(!adjacency.TryGetValue(from,out var list))adjacency[from]=list=[];list.Add((to,connector));}
        var proposed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Propose(string a,string b,string connector,int segments)
        {
            if(a==b||!recognised.TryGetValue(a,out var from)||!recognised.TryGetValue(b,out var to)||!from.Import||!to.Import)return;var key=string.Compare(a,b,StringComparison.OrdinalIgnoreCase)<0?$"{a}|{b}":$"{b}|{a}";if(!proposed.Add(key))return;var confidence=Math.Max(.68,.92-(segments-1)*.04);output.Add(new VisioProposal{Kind=VisioProposalKind.Relationship,ProposedValue=$"{from.ProposedValue} CONNECTED_TO {to.ProposedValue}",Details=segments==1?"Direct Visio connector":$"Visio connector path ({segments} segments)",SourcePage=page,DetectedSite=from.DetectedSite??to.DetectedSite,NetworkZone=from.NetworkZone??to.NetworkZone,SourceShapeId=a,RelatedShapeId=b,ParentShapeId=connector,Confidence=confidence,Status="New",Import=true});
        }
        foreach(var start in anchors)
        {
            var queue=new Queue<(string Node,int Depth,string Connector)>();var seen=new HashSet<string>{start.Key};if(adjacency.TryGetValue(start.Key,out var first))foreach(var next in first)queue.Enqueue((next.Other,1,next.Connector));
            while(queue.Count>0){var current=queue.Dequeue();if(!seen.Add(current.Node)||current.Depth>8)continue;if(anchors.TryGetValue(current.Node,out var device)){Propose(start.Value,device,current.Connector,current.Depth);continue;}if(adjacency.TryGetValue(current.Node,out var next)&&next.Select(x=>x.Other).Distinct().Count()<=3)foreach(var hop in next)queue.Enqueue((hop.Other,current.Depth+1,current.Connector));}
        }
        foreach(var direct in connects.Where(x=>recognised.ContainsKey(x.From!)&&recognised.ContainsKey(x.To!)))Propose(direct.From!,direct.To!,direct.From!,1);
    }

    private static Dictionary<string,string> MapConnectorAnchors(HashSet<string> endpointIds,Dictionary<string,VisioProposal> recognised,Dictionary<string,ShapeData> shapes)
    {
        var mapped=new Dictionary<string,string>();var usedDevices=new HashSet<string>();foreach(var id in endpointIds.Where(recognised.ContainsKey)){mapped[id]=id;usedDevices.Add(id);}var candidates=new List<(string Endpoint,string Device,double Distance)>();foreach(var device in recognised.Where(x=>x.Value.Import&&!usedDevices.Contains(x.Key)&&shapes.TryGetValue(x.Key,out _)))foreach(var endpoint in endpointIds.Where(shapes.ContainsKey)){var label=shapes[device.Key];var target=shapes[endpoint];if(label.X==0&&label.Y==0||target.X==0&&target.Y==0)continue;var dx=Math.Max(Math.Abs(label.X-target.X)-target.Width/2,0);var dy=Math.Max(Math.Abs(label.Y-target.Y)-target.Height/2,0);var distance=Math.Sqrt(dx*dx+dy*dy);if(distance<=1.15)candidates.Add((endpoint,device.Key,distance));}foreach(var item in candidates.OrderBy(x=>x.Distance).ThenByDescending(x=>recognised[x.Device].Confidence)){if(mapped.ContainsKey(item.Endpoint)||!usedDevices.Add(item.Device))continue;mapped[item.Endpoint]=item.Device;}return mapped;
    }

    private static ShapeData ReadShape(XElement shape)
    {
        var text=string.Join(" ",shape.Elements().Where(x=>x.Name.LocalName=="Text").SelectMany(x=>x.DescendantNodes().OfType<XText>()).Select(x=>x.Value)).Replace('\u00a0',' ').Trim();
        var props=new List<string>();
        foreach(var row in shape.Elements().Where(x=>x.Name.LocalName=="Section"&&(string?)x.Attribute("N")=="Property").Elements().Where(x=>x.Name.LocalName=="Row"))
        { var key=(string?)row.Attribute("N")??"Property"; var value=row.Elements().FirstOrDefault(x=>x.Name.LocalName=="Cell"&&(string?)x.Attribute("N")=="Value")?.Attribute("V")?.Value; if(!string.IsNullOrWhiteSpace(value))props.Add($"{key}={value}"); }
        double Cell(string name)=>double.TryParse(shape.Elements().FirstOrDefault(x=>x.Name.LocalName=="Cell"&&(string?)x.Attribute("N")==name)?.Attribute("V")?.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)?value:0;
        return new ShapeData((string?)shape.Attribute("ID"),(string?)shape.Attribute("NameU")??(string?)shape.Attribute("Name")??"",Whitespace().Replace(text," "),string.Join("; ",props),Cell("PinX"),Cell("PinY"),Cell("Width"),Cell("Height"));
    }
    private static string? DeviceName(ShapeData shape,List<string> ips,List<string> subnets)
    {
        var cleaned=shape.Text; foreach(var value in ips.Concat(subnets))cleaned=cleaned.Replace(value," ",StringComparison.OrdinalIgnoreCase); cleaned=Vlan().Replace(cleaned," ");
        var tokens=Whitespace().Split(cleaned.Trim()).Where(x=>x.Length is >=2 and <=50).ToList();
        return tokens.FirstOrDefault(x=>Hostname().IsMatch(x)&&!GenericWords.Contains(x))??tokens.FirstOrDefault();
    }
    private static bool IsDeviceLike(ShapeData shape,string name,List<string> ips)=>DeviceKeyword().IsMatch(shape.Text+" "+shape.Name)||ips.Count>0&&Hostname().IsMatch(name)||StrongHostname().IsMatch(name);
    private static string? ExtractZone(string text){var match=ZoneHeading().Match(text);if(!match.Success)return null;var value=match.Groups[1].Value.ToUpperInvariant();return value=="MGMT"?"MANAGEMENT":value;}
    private static List<string> ExtractIpAddresses(string text){var values=Ipv4().Matches(text).Select(x=>x.Value).ToList();for(var i=1;i<text.Length;i++)if(char.IsDigit(text[i])&&char.IsLetterOrDigit(text[i-1])){var match=Ipv4AtStart().Match(text[i..]);if(match.Success)values.Add(match.Value);}var distinct=values.Distinct().ToList();return distinct.Where(v=>!distinct.Any(other=>other.Length>v.Length&&other.EndsWith(v,StringComparison.Ordinal))).ToList();}
    private static string NormalizeCidr(string cidr){var parts=cidr.Split('/');if(parts.Length!=2||!System.Net.IPAddress.TryParse(parts[0],out var ip)||!int.TryParse(parts[1],out var prefix)||prefix is <0 or >32)return cidr;var bytes=ip.GetAddressBytes();if(bytes.Length!=4)return cidr;var value=((uint)bytes[0]<<24)|((uint)bytes[1]<<16)|((uint)bytes[2]<<8)|bytes[3];var mask=prefix==0?0u:uint.MaxValue<<(32-prefix);var network=value&mask;return $"{network>>24}.{network>>16&255}.{network>>8&255}.{network&255}/{prefix}";}
    private static bool IsSiteHeading(ShapeData shape)=>shape.Text.Length is >=3 and <=80&&!Ipv4().IsMatch(shape.Text)&&!Vlan().IsMatch(shape.Text)&&LocationHeading().IsMatch(shape.Text);
    private static string? NearestSiteHeading(ShapeData shape,List<ShapeData> headings)
    {
        var candidates=headings.Where(x=>x.Id!=shape.Id).Select(x=>(Shape:x,Distance:Math.Sqrt(Math.Pow(x.X-shape.X,2)+Math.Pow(x.Y-shape.Y,2)))).Where(x=>x.Distance<=6).ToList();
        var above=candidates.Where(x=>x.Shape.Y>=shape.Y-.15).ToList();var nearest=(above.Count>0?above:candidates).OrderBy(x=>x.Distance).FirstOrDefault();
        if(nearest.Shape is null)return null;var first=nearest.Shape.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??nearest.Shape.Text;return first.Length<=40?first:null;
    }
    private static readonly HashSet<string> GenericWords=new(StringComparer.OrdinalIgnoreCase){"switch","router","firewall","server","host","device","network","internet","cloud","vlan","subnet","gateway","it","ot","dmz","voice","management","production"};
    private static string NormalizePagePath(string target) { target=target.Replace('\\','/'); if(target.StartsWith("../"))target=target[3..]; if(target.StartsWith("pages/"))return "visio/"+target; return "visio/pages/"+target.TrimStart('/'); }
    private static Dictionary<string,string> Relationships(ZipArchive zip,string path)=>Xml(zip,path)?.Root?.Elements().Where(x=>x.Name.LocalName=="Relationship").Where(x=>x.Attribute("Id") is not null&&x.Attribute("Target") is not null).ToDictionary(x=>x.Attribute("Id")!.Value,x=>x.Attribute("Target")!.Value)??[];
    private static XDocument? Xml(ZipArchive zip,string path) { var entry=zip.GetEntry(path); if(entry is null)return null; using var stream=entry.Open(); return XDocument.Load(stream,LoadOptions.None); }
    private record ConnectorEdge(string ConnectorId,string[] Endpoints);
    private record ShapeData(string? Id,string Name,string Text,string Metadata,double X,double Y,double Width,double Height);
    [GeneratedRegex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d./])")] private static partial Regex Ipv4();
    [GeneratedRegex(@"^(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d./])")] private static partial Regex Ipv4AtStart();
    [GeneratedRegex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}/(?:[0-9]|[12][0-9]|3[0-2])(?!\d)")] private static partial Regex Cidr();
    [GeneratedRegex(@"\bVLAN\s*[:#-]?\s*(\d{1,4})\b",RegexOptions.IgnoreCase)] private static partial Regex Vlan();
    [GeneratedRegex(@"\bVLANs?\s*:",RegexOptions.IgnoreCase)] private static partial Regex VlanListHeading();
    [GeneratedRegex(@"\b(IT|OT|DMZ|GUEST|VOICE|MGMT|MANAGEMENT)\s+VLANs?\s*:",RegexOptions.IgnoreCase)] private static partial Regex ZoneHeading();
    [GeneratedRegex(@"((?:\d{1,3}\.){3}\d{1,3}/(?:[0-9]|[12][0-9]|3[0-2]))\s*\(([^)]+)\)")] private static partial Regex NamedSubnet();
    [GeneratedRegex(@"\b(switch|router|firewall|server|host|appliance|gateway|access point|forti(?:gate|net)?|cisco|aruba|palo alto|idrac|ilo|nas|san)\b",RegexOptions.IgnoreCase)] private static partial Regex DeviceKeyword();
    [GeneratedRegex(@"(?:,\s*)?\b(NSW|VIC|QLD|SA|WA|TAS|NT|ACT|NZ)\b|\bsite\b",RegexOptions.IgnoreCase)] private static partial Regex LocationHeading();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]*$")] private static partial Regex Hostname();
    [GeneratedRegex(@"^(?=.*[A-Za-z])(?=.*\d)[A-Za-z0-9_.-]{3,40}$")] private static partial Regex StrongHostname();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
