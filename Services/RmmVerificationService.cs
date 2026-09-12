using NetworkHelper.Data;
using NetworkHelper.Domain;

namespace NetworkHelper.Services;

public sealed class RmmVerificationService(Database db)
{
    public List<Device> Apply(IEnumerable<Device> devices)
    {
        var list=devices.ToList();if(list.Count==0)return list;
        using var c=db.BeginConnection();
        foreach(var device in list.ToList())
        {
            using var q=c.CreateCommand();
            q.CommandText="""
                SELECT EXISTS(
                    SELECT 1
                    FROM facts v
                    WHERE v.entity_type='device' AND v.entity_id=$id
                      AND v.field_name='rmm_identity_verified' AND v.value='true' AND v.status='confirmed'
                      AND EXISTS(
                          SELECT 1 FROM facts n JOIN fact_evidence ne ON ne.fact_id=n.id JOIN evidence_sources ns ON ns.id=ne.source_id
                          WHERE n.entity_type='device' AND n.entity_id=$id AND n.field_name='name' AND n.value=$name COLLATE NOCASE AND n.status='confirmed' AND ns.source_type='RMM'
                      )
                      AND EXISTS(
                          SELECT 1 FROM facts i JOIN fact_evidence ie ON ie.fact_id=i.id JOIN evidence_sources es ON es.id=ie.source_id
                          WHERE i.entity_type='device' AND i.entity_id=$id AND i.field_name='ip_address' AND i.value=$ip COLLATE NOCASE AND i.status='confirmed' AND es.source_type='RMM'
                      )
                )
                """;
            q.Parameters.AddWithValue("$id",device.Id);q.Parameters.AddWithValue("$name",device.Name);q.Parameters.AddWithValue("$ip",device.IpAddress??"");
            var verified=Convert.ToInt32(q.ExecuteScalar())==1;
            var index=list.FindIndex(x=>x.Id==device.Id);if(index>=0)list[index]=device with{RmmVerified=verified};
        }
        return list;
    }
}
