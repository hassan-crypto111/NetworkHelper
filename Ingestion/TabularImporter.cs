using System.Globalization;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;
using NetworkHelper.Domain;

namespace NetworkHelper.Ingestion;

public interface IFileImporter { bool Supports(string path); IReadOnlyList<ImportRow> Read(string path); }
public sealed class TabularImporter : IFileImporter
{
    public bool Supports(string path) => new[]{".csv",".xlsx"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ImportRow> Read(string path)
    {
        var rows=Path.GetExtension(path).Equals(".csv",StringComparison.OrdinalIgnoreCase)?ReadCsv(path):ReadXlsx(path);
        if(rows.Count<2) return [];
        var headers=rows[0].Select((x,i)=>(Key:Normalize(x),Index:i)).ToDictionary(x=>x.Key,x=>x.Index,StringComparer.OrdinalIgnoreCase);
        string? Get(string[] row,params string[] names) { foreach(var n in names) if(headers.TryGetValue(Normalize(n),out var i)&&i<row.Length&&!string.IsNullOrWhiteSpace(row[i])) return row[i].Trim(); return null; }
        return rows.Skip(1).Where(r=>r.Any(x=>!string.IsNullOrWhiteSpace(x))).Select(r=>new ImportRow(
            Get(r,"hostname","device","device name","name")??"Unnamed device",Get(r,"management ip","ip","ip address"),Get(r,"mac","mac address"),Get(r,"serial","serial number","service tag"),Get(r,"vendor","manufacturer"),Get(r,"model"),Get(r,"network","subnet","cidr"),int.TryParse(Get(r,"vlan","vlan id"),out var v)?v:null,Get(r,"site location","site"),Get(r,"type","device type"),Get(r,"device description","description"),Get(r,"zone","security zone","network zone"),Get(r,"hosted location","hosted on","hypervisor","esxi host","vm host","physical host"),Get(r,"switch","connected switch","access switch"),Get(r,"switch port","port","switch interface"),Get(r,"gateway","default gateway"))).ToList();
    }
    private static List<string[]> ReadCsv(string path) { using var p=new TextFieldParser(path){TextFieldType=FieldType.Delimited}; p.SetDelimiters(","); p.HasFieldsEnclosedInQuotes=true; var r=new List<string[]>(); while(!p.EndOfData) r.Add(p.ReadFields()??[]); return r; }
    private static List<string[]> ReadXlsx(string path) { using var w=new XLWorkbook(path); var sheet=w.Worksheets.FirstOrDefault(x=>x.Name.Equals("All Sites",StringComparison.OrdinalIgnoreCase))??w.Worksheets.First(); var range=sheet.RangeUsed(); if(range is null)return []; return range.Rows().Select(row=>row.Cells(1,range.ColumnCount()).Select(c=>c.GetFormattedString()).ToArray()).ToList(); }
    private static string Normalize(string s)=>new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
