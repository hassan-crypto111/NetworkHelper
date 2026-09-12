using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class TroubleshootingAdvisorService(Database db,PathfindingService pathfinder)
{
    public TroubleshootingPlan Analyze(long siteId,string report,long? selectedDeviceId=null)
    {
        if(string.IsNullOrWhiteSpace(report))throw new InvalidOperationException("Describe the symptom first.");
        var devices=db.Devices(siteId);var target=selectedDeviceId.HasValue?devices.FirstOrDefault(x=>x.Id==selectedDeviceId):ResolveTarget(report,devices);
        var issue=Classify(report);var relationships=db.Relationships(siteId);var networks=db.Networks(siteId);var steps=new List<TroubleshootingStep>();var order=0;
        void Add(string title,string action,string purpose,string evidence,double confidence,string? command=null,long? relationshipId=null)=>steps.Add(new(0,0,++order,title,action,command,purpose,evidence,confidence,"pending",relationshipId));

        Add("Validate the alert and scope","Check the RMM last-contact time, maintenance history, and whether other devices at this site are reporting the same symptom.","Separates a stale RMM alert, single-device failure, and site-wide outage before deeper investigation.","User report: "+report,.9);
        if(target is null)
        {
            Add("Identify the affected device","Select the target device above, or include its exact hostname or IP address in the description.","Network Helper will not guess which asset is affected when no unique inventory match exists.","No unique device name or IP was found in the report.",1);
            return new(issue,"No unique target was identified. The plan is limited until a device is selected.",null,steps);
        }

        var network=target.IpAddress is null?null:networks.Where(x=>RelationshipInferenceService.ContainsAddress(x.Cidr,target.IpAddress)).OrderByDescending(x=>Prefix(x.Cidr)).FirstOrDefault();
        var deviceRelations=relationships.Where(x=>(x.FromEntityType=="device"&&x.FromEntityId==target.Id)||(x.ToEntityType=="device"&&x.ToEntityId==target.Id)).ToList();
        var context=$"Inventory: {target.Name}; IP {target.IpAddress??"unknown"}; type {target.DeviceType??"unknown"}; zone {target.Zone??network?.Zone??"unknown"}"+(network is null?"":$"; subnet {network.Cidr}; VLAN {network.VlanId?.ToString()??"unknown"}");
        Add("Confirm the inventory identity",$"Confirm the alert refers to {target.Name} and that its recorded IP ({target.IpAddress??"not documented"}) is current.","Prevents troubleshooting an obsolete address or similarly named asset.",context,.98);
        var previous=db.TroubleshootingSessions(siteId).FirstOrDefault(x=>x.TargetDeviceId==target.Id);
        if(previous is not null)Add("Review the previous investigation",$"Review the {previous.CreatedAt} session for {target.Name}, especially any failed or repeated checks.","Recurring symptoms and previously failed checks can narrow the fault domain before repeating work.",$"Previous issue: {previous.IssueType}; status {previous.Status}; summary: {previous.Summary}",.88);

        var dependencies=deviceRelations.Where(x=>x.RelationshipType is "RUNS_ON" or "DEPENDS_ON" or "UPLINKS_TO" or "CONNECTED_TO").ToList();
        foreach(var relation in dependencies.Take(3))
        {
            var other=relation.FromEntityId==target.Id?relation.ToEntityName:relation.FromEntityName;
            Add("Check the documented dependency",$"Verify {other} before assuming {target.Name} itself has failed.","A failed host, uplink, or adjacent device can make the target appear offline.",$"{relation.Summary}; source {relation.Source}; status {relation.Status}; confidence {relation.Confidence:P0}",relation.Confidence,relationshipId:relation.Id);
        }

        var paths=pathfinder.Find(relationships,"Current workstation",target.Name,maxPaths:2);
        if(paths.Count>0)
        {
            var path=paths[0];Add("Validate the management path",$"Check each hop in order: {string.Join(" → ",new[]{path.Hops[0].From}.Concat(path.Hops.Select(x=>x.To)))}.","An unavailable VPN, jump host, management VLAN, or uplink can look like a target-device outage.",string.Join("; ",path.Hops.Select(x=>$"#{x.RelationshipId} {x.RelationshipType} from {x.Source}")),path.Confidence,relationshipId:path.Hops.FirstOrDefault()?.RelationshipId);
            if(paths.Count>1)Add("Try the documented alternate path",$"If the preferred path fails, try: {string.Join(" → ",new[]{paths[1].Hops[0].From}.Concat(paths[1].Hops.Select(x=>x.To)))}.","Confirms whether the fault is in the preferred access path rather than the target.",string.Join("; ",paths[1].Hops.Select(x=>$"#{x.RelationshipId} from {x.Source}")),paths[1].Confidence,relationshipId:paths[1].Hops.FirstOrDefault()?.RelationshipId);
        }
        else Add("Establish the access path","Confirm how this site is normally reached (VPN, jump server, management VLAN and gateway) and record those relationships in Relationships & Paths.","Without a documented path, Network Helper cannot distinguish an access-path failure from a target failure.","No usable Current workstation-to-target path is stored.",1);

        if(!string.IsNullOrWhiteSpace(target.IpAddress))
        {
            AddCommand("win_test_ip",new(){["ip"]=target.IpAddress!},"Test IP reachability from an authorised Windows management host",$"Run a controlled reachability test to {target.IpAddress} from the expected management source.","Separates name-resolution problems from basic path or host reachability.",context,.9);
            AddCommand("win_resolve",new(){["hostname"]=target.Name},"Check name resolution",$"Resolve {target.Name} and compare the result with inventory IP {target.IpAddress}.","A correct RMM name with an old DNS address can present as an offline endpoint.",context,.9);
        }
        if(network is not null)Add("Compare nearby devices",$"Check whether another known device in {network.Cidr} / VLAN {network.VlanId?.ToString()??"unknown"} is also affected.","Multiple failures in the same subnet or VLAN point toward gateway, VLAN, uplink, DHCP, or power rather than one server.",$"Subnet and VLAN derived from {target.IpAddress}; recorded zone {network.Zone??"unknown"}.",.84);

        if(!string.IsNullOrWhiteSpace(target.MacAddress))AddCommand("cisco_mac",new(){["mac"]=target.MacAddress!},"Locate the endpoint on a switch",$"On the relevant switch, find where MAC {target.MacAddress} is learned and inspect that interface.","A missing MAC narrows the fault toward power, cabling, NIC, access VLAN, or the wrong switch path.",context,.78);
        if(issue=="RMM / device offline")
        {
            AddCommand("win_services",[],"Check the RMM agent after network reachability is restored","If the host responds outside RMM, identify the installed RMM agent service and inspect its state. Do not restart it until the service name and impact are confirmed.","A responsive OS with only RMM offline usually indicates agent, service, certificate, proxy, or outbound-connectivity trouble.","This step is conditional; Network Helper does not know the RMM product or service name.",.82);
            AddCommand("win_events",[],"Review recent system failures","Inspect recent System events around the last RMM contact time for reboot, NIC, DNS, service-control, or power symptoms.","Time-correlated local events can explain why monitoring stopped.","Read-only Windows event query.",.8);
        }
        else if(issue=="DNS")AddCommand("win_dns_cache",[],"Inspect the local DNS state","Review the resolver cache and compare cached answers with authoritative/current inventory values.","Helps identify stale or inconsistent client-side resolution.","Issue description contains DNS/name-resolution terms.",.82);
        else if(issue=="Performance")Add("Compare layer boundaries","Measure separately from client to gateway, gateway to target, and across each WAN/VPN hop. Record time and direction.","Segmented measurements prevent blaming a server for congestion, duplex errors, WAN loss, or a busy uplink.",context,.82);

        Add("Record the result","Mark each check completed or failed and retain the session. Add confirmed topology or inventory corrections through the existing review workflow.","The next investigation should begin with evidence from this incident instead of repeating assumptions.","This troubleshooting session is stored locally and contains no credentials.",1);
        var summary=$"{issue} plan for {target.Name}. {context}. No commands were executed; these are ordered, evidence-backed checks.";
        return new(issue,summary,target,steps);

        void AddCommand(string key,Dictionary<string,string> values,string title,string action,string purpose,string evidence,double confidence)
        {
            var command=db.CommandByKey(key);if(command is null){Add(title,action,purpose,evidence,confidence);return;}var rendered=values.Aggregate(command.CommandText,(text,p)=>text.Replace("{"+p.Key+"}",p.Value,StringComparison.OrdinalIgnoreCase));Add(title,action,purpose,$"{evidence} Command memory: {command.Platform} / {command.Category} / {command.RiskLevel}.",confidence,rendered);
        }
    }

    private static Device? ResolveTarget(string report,IReadOnlyList<Device> devices)
    {
        var matches=devices.Where(d=>report.Contains(d.Name,StringComparison.OrdinalIgnoreCase)||(!string.IsNullOrWhiteSpace(d.IpAddress)&&report.Contains(d.IpAddress,StringComparison.OrdinalIgnoreCase))).OrderByDescending(x=>x.Name.Length).ToList();return matches.Count==0?null:matches[0];
    }
    private static string Classify(string text)=>text.Contains("rmm",StringComparison.OrdinalIgnoreCase)||new[]{"offline","down","unreachable","not responding"}.Any(x=>text.Contains(x,StringComparison.OrdinalIgnoreCase))?"RMM / device offline":new[]{"dns","resolve","name resolution"}.Any(x=>text.Contains(x,StringComparison.OrdinalIgnoreCase))?"DNS":new[]{"slow","latency","packet loss","intermittent"}.Any(x=>text.Contains(x,StringComparison.OrdinalIgnoreCase))?"Performance":new[]{"rdp","remote desktop"}.Any(x=>text.Contains(x,StringComparison.OrdinalIgnoreCase))?"Remote access":"General connectivity";
    private static int Prefix(string cidr)=>int.TryParse(cidr.Split('/').ElementAtOrDefault(1),out var prefix)?prefix:0;
}
