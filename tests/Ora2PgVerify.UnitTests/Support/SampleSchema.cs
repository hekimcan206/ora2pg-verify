using System.Text;
using Ora2PgVerify.Oracle;
using Ora2PgVerify.Scan;

namespace Ora2PgVerify.UnitTests.Support;

/// <summary>The sample PL/SQL script as ALL_SOURCE / ALL_VIEWS return it (shared with the integration tests).</summary>
internal static class SampleSchema
{
    public static IReadOnlyList<SourceObject> Objects()
    {
        var result = new List<SourceObject>();
        foreach (var statement in OracleSeeder.Statements(OracleSeeder.Script("plsql.sql")))
        {
            var text = statement.StartsWith("CREATE OR REPLACE ", StringComparison.Ordinal) ? statement["CREATE OR REPLACE ".Length..] : statement["CREATE ".Length..];
            var words = text.Split([' ', '\n', '('], StringSplitOptions.RemoveEmptyEntries);
            var (type, name) = words[0] == "PACKAGE" && words[1] == "BODY" ? ("PACKAGE BODY", words[2]) : (words[0], words[1]);
            if (type == "SYNONYM")
            {
                continue; // no stored source; reported from the dictionary
            }

            if (type == "VIEW")
            {
                text = text[(text.IndexOf("AS\n", StringComparison.Ordinal) + 3)..];
            }

            result.Add(new SourceObject("APP_OWNER", name, type, name == "PURGE_REMOTE_COPY" ? "INVALID" : "VALID", text));
        }

        return result;
    }

    public static string Describe(IEnumerable<ScanFinding> findings)
    {
        var sb = new StringBuilder();
        foreach (var f in findings)
        {
            sb.Append(f.Severity).Append(' ').Append(f.RuleId).Append(' ').Append(f.ObjectType).Append(' ').Append(f.ObjectName)
              .Append(f.Line is { } l ? $" line {l}" : "").Append(f.Snippet is { Length: > 0 } s ? $": {s}" : "").Append('\n');
        }

        return sb.ToString();
    }
}
