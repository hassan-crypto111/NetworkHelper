namespace NetworkHelper.Services;

public sealed class SourceAuthorityPolicy
{
    private readonly Dictionary<(string SourceType,string FieldName),int> _priorities = new()
    {
        [("RMM","ip_address")] = 100,
        [("MANUAL","ip_address")] = 110,
        [("LIVE","ip_address")] = 105,
        [("XLSX","ip_address")] = 40,
        [("CSV","ip_address")] = 40,
        [("VISIO","ip_address")] = 30
    };

    public int Priority(string sourceType,string fieldName)
        => _priorities.TryGetValue((sourceType.Trim().ToUpperInvariant(),fieldName.Trim().ToLowerInvariant()),out var priority) ? priority : 0;

    public bool IsAuthoritative(string sourceType,string fieldName)
        => Priority(sourceType,fieldName) >= 100;
}
