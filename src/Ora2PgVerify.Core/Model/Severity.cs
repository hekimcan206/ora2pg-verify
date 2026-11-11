namespace Ora2PgVerify.Model;

/// <summary>How much rework a finding implies. Points follow the spirit of ora2pg's cost units (higher = more manual work).</summary>
public enum Severity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public static class SeverityExtensions
{
    /// <summary>Effort points per occurrence, used to rank objects; a relative score, not person-days.</summary>
    public static int Points(this Severity severity) => severity switch
    {
        Severity.Info => 0,
        Severity.Low => 1,
        Severity.Medium => 3,
        Severity.High => 8,
        _ => 0,
    };
}
