using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class PathfindingService
{
    private static readonly HashSet<string> Traversable=["CONNECTED_TO","UPLINKS_TO","RUNS_ON","GATEWAY_FOR","MANAGED_FROM","ACCESSIBLE_FROM","DEPENDS_ON","ROUTES_THROUGH","BELONGS_TO_SUBNET","BELONGS_TO_VLAN"];
    public IReadOnlyList<AccessPath> Find(IEnumerable<Relationship> relationships,string start,string target,ISet<long>? unavailableRelationships=null,ISet<string>? unavailableEntities=null,int maxPaths=3,int maxHops=8)
    {
        unavailableRelationships??=new HashSet<long>();unavailableEntities??=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var edges=new Dictionary<string,List<PathHop>>(StringComparer.OrdinalIgnoreCase);
        foreach(var r in relationships.Where(x=>(x.Status is "inferred" or "confirmed")&&Traversable.Contains(x.RelationshipType)&&!unavailableRelationships.Contains(x.Id)))
        {
            Add(r.FromEntityName,new(r.FromEntityName,r.RelationshipType,r.ToEntityName,r.Source,r.Confidence,r.Id));
            if(r.RelationshipType is "CONNECTED_TO" or "BELONGS_TO_SUBNET" or "BELONGS_TO_VLAN" or "RUNS_ON" or "GATEWAY_FOR")Add(r.ToEntityName,new(r.ToEntityName,r.RelationshipType,r.FromEntityName,r.Source,r.Confidence,r.Id));
            if(r.RelationshipType is "MANAGED_FROM" or "ACCESSIBLE_FROM")Add(r.ToEntityName,new(r.ToEntityName,r.RelationshipType,r.FromEntityName,r.Source,r.Confidence,r.Id));
        }
        void Add(string key,PathHop hop){if(!edges.TryGetValue(key,out var list))edges[key]=list=[];list.Add(hop);}
        var results=new List<AccessPath>();var queue=new Queue<(string Node,List<PathHop> Hops,HashSet<string> Seen)>();queue.Enqueue((start,[],new(StringComparer.OrdinalIgnoreCase){start}));
        while(queue.Count>0&&results.Count<maxPaths){var current=queue.Dequeue();if(current.Hops.Count>=maxHops)continue;if(!edges.TryGetValue(current.Node,out var next))continue;foreach(var hop in next.OrderByDescending(x=>x.Confidence)){if(unavailableEntities.Contains(hop.To)||current.Seen.Contains(hop.To))continue;var hops=new List<PathHop>(current.Hops){hop};if(hop.To.Equals(target,StringComparison.OrdinalIgnoreCase)){results.Add(new(hops,hops.Min(x=>x.Confidence)));continue;}var seen=new HashSet<string>(current.Seen,StringComparer.OrdinalIgnoreCase){hop.To};queue.Enqueue((hop.To,hops,seen));}}
        return results.OrderBy(x=>x.Hops.Count).ThenByDescending(x=>x.Confidence).Select((x,i)=>x with{Label=i==0?"Preferred":$"Alternative {i}"}).ToList();
    }
}
