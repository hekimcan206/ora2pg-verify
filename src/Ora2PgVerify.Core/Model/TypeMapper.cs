using System.Globalization;

namespace Ora2PgVerify.Model;

/// <summary>
/// Decides the comparison semantics of an Oracle/PostgreSQL column pair and reports mappings that change meaning.
/// The rules encode the well-known Oracle→PostgreSQL type pitfalls (see docs/adr/0002).
/// </summary>
public static class TypeMapper
{
    /// <summary>VARCHAR2 columns wider than this travel as a digest so the Oracle row string stays under 4000 bytes.</summary>
    public const int InlineTextByteLimit = 1000;

    public static (ColumnPair? Pair, IReadOnlyList<MappingFinding> Findings) Map(string table, CatalogColumn ora, CatalogColumn pg)
    {
        ArgumentNullException.ThrowIfNull(ora);
        ArgumentNullException.ThrowIfNull(pg);
        var findings = new List<MappingFinding>();
        var name = pg.Name.ToLowerInvariant();
        var oType = OracleTypeName(ora);
        var pType = PostgresTypeName(pg);
        var oBase = ora.DataType.ToUpperInvariant();
        var pBase = pg.DataType.ToLowerInvariant();

        void Add(Severity s, string code, string message) => findings.Add(new MappingFinding(table, name, s, code, message));

        CanonicalKind? kind = null;
        var integer = false;

        if (oBase is "NUMBER" or "INTEGER" or "FLOAT")
        {
            kind = CanonicalKind.Number;
            integer = ora.Scale == 0 && (ora.Precision is not null || oBase == "INTEGER");
            var pIsInt = pBase is "smallint" or "integer" or "bigint";
            if (pBase is "double precision" or "real")
            {
                Add(Severity.High, "NUMBER_TO_FLOAT", $"{oType} mapped to {pType}: binary floating point cannot hold every decimal; values with more than 15 significant digits change.");
            }
            else if (pIsInt && !integer)
            {
                Add(Severity.High, "NUMBER_FRACTION_TRUNCATED", $"{oType} mapped to integer type {pType}: any fractional part is lost.");
            }
            else if (pIsInt && ora.Precision is { } p && p > IntegerDigits(pBase))
            {
                Add(Severity.Medium, "NUMBER_INTEGER_OVERFLOW", $"{oType} allows {p} digits but {pType} holds at most {IntegerDigits(pBase)}.");
            }
            else if (pBase == "numeric" && ora.Scale is { } os && pg.Scale is { } ps && ps < os)
            {
                Add(Severity.High, "NUMBER_SCALE_REDUCED", $"{oType} mapped to {pType}: scale reduced from {os} to {ps}, values are rounded.");
            }

            if (ora.Precision is null && oBase == "NUMBER")
            {
                Add(Severity.Low, "NUMBER_UNCONSTRAINED", $"Unconstrained NUMBER mapped to {pType}; ora2pg chooses bigint or numeric depending on PG_INTEGER_TYPE / DEFAULT_NUMERIC, so check the data range.");
            }
        }
        else if (oBase == "DATE")
        {
            kind = CanonicalKind.Date;
            if (pBase == "date")
            {
                Add(Severity.Medium, "DATE_TO_DATE", "Oracle DATE carries a time of day; PostgreSQL date does not. Equal only while every source value is at midnight.");
            }
            else if (pBase.Contains("with time zone", StringComparison.Ordinal))
            {
                Add(Severity.Medium, "DATE_TO_TIMESTAMPTZ", "Oracle DATE has no time zone; timestamptz makes the session time zone part of every read. Compared as UTC.");
            }
            else if (pg.Precision is > 0)
            {
                Add(Severity.Info, "DATE_TO_TIMESTAMP_FRACTION", $"Oracle DATE has whole seconds; {pType} also accepts fractions (timestamp(0) is the exact match).");
            }
        }
        else if (oBase.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            var tz = oBase.Contains("TIME ZONE", StringComparison.Ordinal);
            kind = tz ? CanonicalKind.TimestampTz : CanonicalKind.Timestamp;
            if (ora.Precision is > 6)
            {
                Add(Severity.Medium, "TIMESTAMP_PRECISION", $"{oType} keeps up to {ora.Precision} fractional digits; PostgreSQL keeps 6. Compared at microseconds.");
            }

            var pTz = pBase.Contains("with time zone", StringComparison.Ordinal);
            if (tz && !pTz)
            {
                Add(Severity.High, "TIMESTAMPTZ_ZONE_DROPPED", $"{oType} mapped to {pType}: the instant is ambiguous without a zone. Compared assuming the target holds UTC.");
            }
            else if (tz)
            {
                Add(Severity.Info, "TIMESTAMPTZ_OFFSET_NOT_KEPT", "PostgreSQL stores the instant, not the original offset or region name; compared as UTC instants.");
            }
            else if (pTz)
            {
                Add(Severity.Medium, "TIMESTAMP_GAINED_ZONE", $"{oType} mapped to {pType}: values now depend on the session time zone. Compared as UTC.");
            }
        }
        else if (oBase is "VARCHAR2" or "NVARCHAR2" or "VARCHAR")
        {
            kind = (ora.ByteLength ?? 0) > InlineTextByteLimit ? CanonicalKind.LargeText : CanonicalKind.Text;
            if (pBase == "character")
            {
                Add(Severity.Medium, "VARCHAR_TO_CHAR", $"{oType} mapped to blank-padded {pType}: trailing spaces become insignificant.");
            }
            else if (pg.CharLength is { } pl && ora.CharLength is { } ol && pl < ol)
            {
                Add(Severity.High, "VARCHAR_SHORTENED", $"{oType} mapped to {pType}: shorter maximum length.");
            }

            if (pBase != "character" && pg.Nullable)
            {
                Add(Severity.Info, "EMPTY_STRING_IS_NULL", "Oracle stores '' as NULL; PostgreSQL keeps '' distinct. Any '' on the target is reported unless --empty-as-null is set.");
            }
        }
        else if (oBase is "CHAR" or "NCHAR")
        {
            kind = CanonicalKind.FixedChar;
            if (pBase != "character")
            {
                Add(Severity.Info, "CHAR_PADDING_NOT_KEPT", $"{oType} mapped to {pType}: padding is not stored; compared with trailing blanks removed on both sides (Oracle blank-padded semantics).");
            }
        }
        else if (oBase is "CLOB" or "NCLOB")
        {
            kind = CanonicalKind.LargeText;
            if (pBase is not ("text" or "character varying"))
            {
                Add(Severity.Medium, "CLOB_TARGET_TYPE", $"{oType} mapped to {pType}.");
            }
        }
        else
        {
            Add(Severity.Medium, "TYPE_NOT_COMPARED", $"{oType} → {pType} is not compared by this version (supported: NUMBER, DATE, TIMESTAMP[ WITH TIME ZONE], VARCHAR2, CHAR, CLOB).");
        }

        if (kind is null)
        {
            return (null, findings);
        }

        if (ora.Nullable != pg.Nullable)
        {
            Add(Severity.Low, "NULLABILITY_CHANGED", $"Nullability differs: Oracle {(ora.Nullable ? "NULL" : "NOT NULL")}, PostgreSQL {(pg.Nullable ? "NULL" : "NOT NULL")}.");
        }

        return (new ColumnPair(name, ora.Name, pg.Name, kind.Value, oType, pType, integer), findings);
    }

    private static int IntegerDigits(string pgBase) => pgBase switch
    {
        "smallint" => 4,
        "integer" => 9,
        _ => 18,
    };

    public static string OracleTypeName(CatalogColumn c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var t = c.DataType.ToUpperInvariant();
        return t switch
        {
            "NUMBER" when c.Precision is null => "NUMBER",
            "NUMBER" => string.Create(CultureInfo.InvariantCulture, $"NUMBER({c.Precision},{c.Scale ?? 0})"),
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "VARCHAR" => string.Create(CultureInfo.InvariantCulture, $"{t}({c.CharLength ?? c.ByteLength})"),
            _ => t,
        };
    }

    public static string PostgresTypeName(CatalogColumn c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var t = c.DataType.ToLowerInvariant();
        return t switch
        {
            "numeric" when c.Precision is not null => string.Create(CultureInfo.InvariantCulture, $"numeric({c.Precision},{c.Scale ?? 0})"),
            "character varying" or "character" when c.CharLength is not null => string.Create(CultureInfo.InvariantCulture, $"{t}({c.CharLength})"),
            "timestamp without time zone" or "timestamp with time zone" when c.Precision is not null =>
                t.Replace("timestamp", string.Create(CultureInfo.InvariantCulture, $"timestamp({c.Precision})"), StringComparison.Ordinal),
            _ => t,
        };
    }
}
