namespace NetworkHelper.Domain;

public record Company(long Id, string Name);
public record Site(long Id, long CompanyId, string Name, string? Address);
public record Device(long Id, long SiteId, string Name, string? IpAddress, string? MacAddress, string? SerialNumber, string? Vendor, string? Model, string? DeviceType, string? Zone, string? Description);
public record NetworkRecord(long Id, long SiteId, string Name, string Cidr, int? VlanId, string? Zone);
public record ImportRow(string Name, string? IpAddress, string? MacAddress, string? SerialNumber, string? Vendor, string? Model, string? Network, int? VlanId, string? SiteLocation = null, string? DeviceType = null, string? Description = null, string? Zone = null, string? HostedLocation = null, string? ConnectedSwitch = null, string? SwitchPort = null, string? Gateway = null)
{
    public string Match { get; set; } = "New";
    public bool Import { get; set; } = true;
}
public record Conflict(long Id, long SiteId, long? DeviceId, string EntityName, string FieldName, string? ExistingValue, string? NewValue, string Status, string SourceName);
public enum Resolution { KeepExisting, AcceptNew, Manual, SeparateDevice }

public enum VisioProposalKind { Device, IpAddress, Vlan, Subnet, Relationship }
public sealed class VisioProposal
{
    public VisioProposalKind Kind { get; init; }
    public string ProposedValue { get; init; } = "";
    public string? Details { get; init; }
    public string SourcePage { get; init; } = "";
    public string? DetectedSite { get; init; }
    public string? NetworkZone { get; init; }
    public string? DeviceType { get; init; }
    public string? DestinationSite { get; set; }
    public string? SourceShapeId { get; init; }
    public string? RelatedShapeId { get; init; }
    public string? ParentShapeId { get; init; }
    public double Confidence { get; init; }
    public string Status { get; set; } = "New";
    public string? MatchedExistingEntity { get; set; }
    public bool Import { get; set; } = true;
}
public record VisioImportResult(IReadOnlyList<VisioProposal> Proposals);

public static class RelationshipTypes
{
    public static readonly string[] All=["BELONGS_TO_SITE","HAS_IP","BELONGS_TO_SUBNET","BELONGS_TO_VLAN","CONNECTED_TO","UPLINKS_TO","RUNS_ON","GATEWAY_FOR","MANAGED_FROM","ACCESSIBLE_FROM","DEPENDS_ON","ROUTES_THROUGH"];
}
public record EntityRef(string Type,long? Id,string Name)
{
    public string Display=>$"{Name}  ·  {Type.Replace('_',' ')}";
}
public record Relationship(long Id,long SiteId,string FromEntityType,long? FromEntityId,string FromEntityName,string ToEntityType,long? ToEntityId,string ToEntityName,string RelationshipType,string Source,string? SourcePage,double Confidence,string Status,string CreatedAt,string? LastVerified,string? Basis)
{
    public string Summary=>$"{FromEntityName}  {RelationshipType.Replace('_',' ')}  {ToEntityName}";
}
public record PathHop(string From,string RelationshipType,string To,string Source,double Confidence,long RelationshipId);
public record AccessPath(IReadOnlyList<PathHop> Hops,double Confidence,string Label="Path")
{
    public string Display=>$"{Label}:  {string.Join("  →  ",Hops.Count==0?[]:new[]{Hops[0].From}.Concat(Hops.Select(x=>x.To)))}   ({Confidence:P0})\n"+string.Join("\n",Hops.Select(x=>$"    #{x.RelationshipId}  {x.From}  — {x.RelationshipType.Replace('_',' ')} →  {x.To}  ·  {x.Source}  ·  {x.Confidence:P0}"));
}

public record CommandKnowledge(long Id,string Key,string Platform,string Category,string CommandText,string Purpose,string RiskLevel,string? Notes,bool IsBuiltIn);
public record TroubleshootingSession(long Id,long SiteId,long? TargetDeviceId,string? TargetName,string Description,string IssueType,string Summary,string Status,string CreatedAt,string UpdatedAt);
public record TroubleshootingStep(long Id,long SessionId,int Order,string Title,string Action,string? Command,string Purpose,string Evidence,double Confidence,string Status,long? RelationshipId=null)
{
    public string Number=>$"{Order}.";
}
public record TroubleshootingPlan(string IssueType,string Summary,Device? Target,IReadOnlyList<TroubleshootingStep> Steps);
public record DiagnosticHop(int Order,int Depth,string From,string RelationshipType,string To,string Source,double Confidence,string Status,long RelationshipId,string Guidance)
{
    public string Relationship=>RelationshipType.Replace('_',' ');
}
public record TopologyNode(Device Device,double X,double Y,int Layer,bool IsRoot,bool IsTarget,bool IsOnSelectedPath);
public record TopologyEdge(long RelationshipId,long FromDeviceId,long ToDeviceId,string RelationshipType,string Source,double Confidence,string Status,bool IsOnSelectedPath);
public record TopologyMap(IReadOnlyList<TopologyNode> Nodes,IReadOnlyList<TopologyEdge> Edges,double Width,double Height,string Summary);
