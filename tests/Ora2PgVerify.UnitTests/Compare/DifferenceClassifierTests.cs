using Ora2PgVerify.Compare;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Compare;

public sealed class DifferenceClassifierTests
{
    private static ValueDifference Kind(string column, string s, string t) =>
        DifferenceClassifier.Classify(TestSpecs.Orders.Column(column), s, t).Difference;

    [Theory]
    [InlineData("note", "~", "0:", ValueDifference.EmptyStringVsNull)]
    [InlineData("note", "~", "3:abc", ValueDifference.NullVsValue)]
    [InlineData("note", "3:abc", "~", ValueDifference.NullVsValue)]
    [InlineData("status", "4:PAID", "5:PAID ", ValueDifference.TrailingWhitespace)]
    [InlineData("status", "4:paid", "4:PAID", ValueDifference.CaseOnly)]
    [InlineData("status", "4:PAID", "4:SENT", ValueDifference.Other)]
    [InlineData("amount", "5:10.25", "5:10.26", ValueDifference.NumericDrift)]
    [InlineData("created_on", "19:2024-03-01T10:15:00", "19:2024-03-01T00:00:00", ValueDifference.TimeOfDayLost)]
    [InlineData("updated_at", "27:2024-03-01T10:15:00.000000Z", "27:2024-03-01T13:15:00.000000Z", ValueDifference.TimeShift)]
    [InlineData("updated_at", "27:2024-03-01T10:15:00.000000Z", "27:2024-03-01T10:15:00.000001Z", ValueDifference.Other)]
    public void Classifies_migration_failure_modes(string column, string s, string t, ValueDifference expected) =>
        Kind(column, s, t).Should().Be(expected);

    [Fact]
    public void Turkish_dotless_and_dotted_i_count_as_case_only()
    {
        Kind("status", "12:Evin Samancı", "12:EVIN SAMANCI").Should().Be(ValueDifference.CaseOnly);
        Kind("status", "6:İzmir", "5:izmir").Should().Be(ValueDifference.CaseOnly);
    }

    [Fact]
    public void Details_name_the_size_of_the_change()
    {
        DifferenceClassifier.Classify(TestSpecs.Orders.Column("amount"), "4:9.99", "5:10.00").Detail.Should().Be("+0.01");
        DifferenceClassifier.Classify(TestSpecs.Orders.Column("updated_at"), "27:2024-03-01T10:15:00.000000Z", "27:2024-03-01T07:15:00.000000Z")
            .Detail.Should().Be("shifted by -3:00:00");
    }

    [Fact]
    public void Clob_differences_are_digest_differences() =>
        DifferenceClassifier.Classify(TestSpecs.Documents.Column("body"), "32:aa", "32:bb").Difference.Should().Be(ValueDifference.DigestDiffers);
}
