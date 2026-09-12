using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class TopologyGraphService(Database db)
{
    private static readonly HashSet<string> PhysicalTypes=["CONNECTED_TO","UPLINKS_TO","RUNS_ON","DEPENDS_ON","ROUTES_THROUGH"];
    public TopologyMap Build(long siteId,long rootDeviceId,long? targetDeviceId=null,bool onlySelectedPath=false)
    {
        var devices=db.Devices(siteId);var byId=devices.ToDictionary(x=>x.Id);if(!byId.TryGetValue(rootDeviceId,out var root))return Empty("The selected root device no longer exists.");
        var relationships=db.Relationships(siteId).Where(x=>PhysicalTypes.Contains(x.RelationshipType)&&x.FromEntityType=="device"&&x.ToEntityType=="device"&&x.FromEntityId.HasValue&&x.ToEntityId.HasValue&&x.FromEntityId!=x.ToEntityId&&(x.Status is "inferred" or "confirmed")).GroupBy(x=>Pair(x.FromEntityId!.Value,x.ToEntityId!.Value)).Select(g=>g.OrderByDescending(x=>x.Status=="confirmed").ThenByDescending(x=>x.Confidence).First()).ToList();
        var adjacency=new Dictionary<long,List<(long Other,Relationship Relationship)>>();foreach(var r in relationships){Add(r.FromEntityId!.Value,r.ToEntityId!.Value,r);Add(r.ToEntityId!.Value,r.FromEntityId!.Value,r);}void Add(long from,long to,Relationship relationship){if(!adjacency.TryGetValue(from,out var list))adjacency[from]=list=[];list.Add((to,relationship));}
        var parent=new Dictionary<long,(long Parent,long Relationship)>();var depth=new Dictionary<long,int>{{rootDeviceId,0}};var queue=new Queue<long>();queue.Enqueue(rootDeviceId);while(queue.Count>0){var current=queue.Dequeue();if(!adjacency.TryGetValue(current,out var next))continue;foreach(var edge in next.OrderByDescending(x=>x.Relationship.Confidence).ThenBy(x=>byId.TryGetValue(x.Other,out var d)?d.Name:"")){if(!byId.ContainsKey(edge.Other)||depth.ContainsKey(edge.Other))continue;depth[edge.Other]=depth[current]+1;parent[edge.Other]=(current,edge.Relationship.Id);queue.Enqueue(edge.Other);}}
        if(depth.Count==1&&!adjacency.ContainsKey(rootDeviceId))return Empty($"No active device connections are stored for {root.Name}. Re-import the VSDX diagram first.");
        var pathNodes=new HashSet<long>();var pathRelationships=new HashSet<long>();if(targetDeviceId.HasValue&&depth.ContainsKey(targetDeviceId.Value)){var current=targetDeviceId.Value;pathNodes.Add(current);while(current!=rootDeviceId&&parent.TryGetValue(current,out var p)){pathRelationships.Add(p.Relationship);current=p.Parent;pathNodes.Add(current);}}
        var included=onlySelectedPath&&targetDeviceId.HasValue&&pathNodes.Count>0?pathNodes:depth.Keys.ToHashSet();var layers=included.GroupBy(x=>depth[x]).OrderBy(x=>x.Key).ToList();const double nodeWidth=206,nodeHeight=82,xGap=86,yGap=34,margin=34;var nodes=new List<TopologyNode>();foreach(var layer in layers){var ordered=layer.Select(x=>byId[x]).OrderBy(x=>TypeOrder(x.DeviceType)).ThenBy(x=>x.Name).ToList();for(var i=0;i<ordered.Count;i++)nodes.Add(new(ordered[i],margin+layer.Key*(nodeWidth+xGap),margin+i*(nodeHeight+yGap),layer.Key,ordered[i].Id==rootDeviceId,ordered[i].Id==targetDeviceId,pathNodes.Contains(ordered[i].Id)));}
        var edges=relationships.Where(x=>included.Contains(x.FromEntityId!.Value)&&included.Contains(x.ToEntityId!.Value)).Select(x=>new TopologyEdge(x.Id,x.FromEntityId!.Value,x.ToEntityId!.Value,x.RelationshipType,x.Source,x.Confidence,x.Status,pathRelationships.Contains(x.Id))).ToList();var width=Math.Max(560,(layers.LastOrDefault()?.Key??0)*(nodeWidth+xGap)+nodeWidth+margin*2);var height=Math.Max(360,layers.Select(x=>x.Count()).DefaultIfEmpty(1).Max()*(nodeHeight+yGap)+margin*2);var targetText=targetDeviceId.HasValue&&byId.TryGetValue(targetDeviceId.Value,out var target)?depth.ContainsKey(target.Id)?$" Selected route: {depth[target.Id]} hop(s) to {target.Name}.":$" {target.Name} is not reachable from this root using confirmed/inferred relationships.":"";return new(nodes,edges,width,height,$"{nodes.Count} reachable device(s), {edges.Count} connection(s), rooted at {root.Name}.{targetText}");
    }
    private static string Pair(long a,long b)=>a<b?$"{a}:{b}":$"{b}:{a}";
    private static int TypeOrder(string? type)=>type?.ToLowerInvariant() switch{"firewall"=>0,"router"=>1,"switch"=>2,"hypervisor host"=>3,"server"=>4,"virtual machine"=>5,_=>6};
    private static TopologyMap Empty(string summary)=>new([],[],560,360,summary);
}
