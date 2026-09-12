using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class DeviceTraversalService(Database db)
{
    private static readonly HashSet<string> Traceable=["RUNS_ON","CONNECTED_TO","UPLINKS_TO","DEPENDS_ON","ROUTES_THROUGH","GATEWAY_FOR","MANAGED_FROM","ACCESSIBLE_FROM","BELONGS_TO_SUBNET","BELONGS_TO_VLAN"];
    public IReadOnlyList<DiagnosticHop> Trace(long siteId,long deviceId,int maxDepth=4,int maxHops=24)
    {
        var devices=db.Devices(siteId);var start=devices.FirstOrDefault(x=>x.Id==deviceId);if(start is null)return [];var byId=devices.ToDictionary(x=>x.Id);var relationships=db.Relationships(siteId).Where(x=>x.Status!="superseded"&&Traceable.Contains(x.RelationshipType)).ToList();var result=new List<DiagnosticHop>();var used=new HashSet<long>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Key("device",deviceId,start.Name)};var queue=new Queue<(string Type,long? Id,string Name,int Depth)>();queue.Enqueue(("device",start.Id,start.Name,0));
        while(queue.Count>0&&result.Count<maxHops)
        {
            var node=queue.Dequeue();if(node.Depth>=maxDepth)continue;var candidates=relationships.Select(r=>Orient(r,node)).Where(x=>x is not null).Select(x=>x!.Value).OrderBy(x=>Priority(x.Relationship.RelationshipType)).ThenByDescending(x=>x.Relationship.Confidence);
            foreach(var item in candidates)
            {
                var r=item.Relationship;if(!used.Add(r.Id))continue;var other=item.Other;if(r.RelationshipType=="CONNECTED_TO"&&node.Depth>0&&other.Type=="device"&&other.Id.HasValue&&byId.TryGetValue(other.Id.Value,out var device)&&!Infrastructure(device))continue;
                var guidance=Guidance(r.RelationshipType,node.Name,other.Name);if(r.Status=="conflicting")guidance="Resolve this conflicting relationship before relying on this route. "+guidance;result.Add(new(result.Count+1,node.Depth,node.Name,r.RelationshipType,other.Name,r.Source,r.Confidence,r.Status,r.Id,guidance));if(result.Count>=maxHops)break;
                if(r.RelationshipType is "BELONGS_TO_SUBNET" or "BELONGS_TO_VLAN" or "MANAGED_FROM" or "ACCESSIBLE_FROM")continue;var key=Key(other.Type,other.Id,other.Name);if(visited.Add(key))queue.Enqueue((other.Type,other.Id,other.Name,node.Depth+1));
            }
        }
        return result;
    }
    private static (Relationship Relationship,(string Type,long? Id,string Name) Other)? Orient(Relationship r,(string Type,long? Id,string Name,int Depth) node)
    {
        var from=Matches(r.FromEntityType,r.FromEntityId,r.FromEntityName,node.Type,node.Id,node.Name);var to=Matches(r.ToEntityType,r.ToEntityId,r.ToEntityName,node.Type,node.Id,node.Name);if(from)return(r,(r.ToEntityType,r.ToEntityId,r.ToEntityName));if(to&&r.RelationshipType=="CONNECTED_TO")return(r,(r.FromEntityType,r.FromEntityId,r.FromEntityName));return null;
    }
    private static bool Matches(string type,long? id,string name,string nodeType,long? nodeId,string nodeName)=>type==nodeType&&(id.HasValue&&nodeId.HasValue?id==nodeId:name.Equals(nodeName,StringComparison.OrdinalIgnoreCase));
    private static bool Infrastructure(Device d)=>d.DeviceType?.Contains("Switch",StringComparison.OrdinalIgnoreCase)==true||d.DeviceType?.Contains("Router",StringComparison.OrdinalIgnoreCase)==true||d.DeviceType?.Contains("Firewall",StringComparison.OrdinalIgnoreCase)==true||d.DeviceType?.Contains("Hypervisor",StringComparison.OrdinalIgnoreCase)==true||d.DeviceType?.Contains("Network",StringComparison.OrdinalIgnoreCase)==true;
    private static int Priority(string type)=>type switch{"RUNS_ON"=>0,"DEPENDS_ON"=>1,"CONNECTED_TO"=>2,"UPLINKS_TO"=>3,"ROUTES_THROUGH"=>4,"GATEWAY_FOR"=>5,"BELONGS_TO_SUBNET"=>6,"BELONGS_TO_VLAN"=>7,"MANAGED_FROM"=>8,_=>9};
    private static string Guidance(string type,string from,string to)=>type switch
    {
        "RUNS_ON"=>$"Check {to} first: host reachability, VM power/registration, datastore, vSwitch and port-group state.",
        "CONNECTED_TO"=>$"At {to}, verify the link/port state, learned MAC, VLAN assignment and interface errors for {from}.",
        "UPLINKS_TO"=>$"Verify the uplink from {from} to {to}, including trunk VLANs, errors and spanning-tree state.",
        "DEPENDS_ON"=>$"Validate {to} before treating {from} as the failed component.",
        "ROUTES_THROUGH"=>$"Confirm {to} has the expected route and return path.",
        "GATEWAY_FOR"=>$"Check gateway interface state, ARP and routing on {to}.",
        "BELONGS_TO_SUBNET"=>$"Use {to} as the Layer-3 fault boundary and compare another device in that subnet.",
        "BELONGS_TO_VLAN"=>$"Verify VLAN {to} exists and is allowed end-to-end.",
        "MANAGED_FROM"=>$"Confirm the management source {to} is available before testing {from}.",
        _=>$"Validate {from} to {to} using the stored relationship evidence."
    };
    private static string Key(string type,long? id,string name)=>$"{type}:{id?.ToString()??name}";
}
