using AwesomeAssertions;

using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class ListReorderTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(15, 1)]
    [InlineData(25, 1)]
    [InlineData(35, 2)]
    [InlineData(99, 3)]
    public void Insertion_index_is_the_first_row_whose_midpoint_is_below_the_pointer(double y, int expected) =>
        ListReorder.InsertionIndex([10, 30, 50], y).Should().Be(expected);

    [Fact]
    public void Empty_list_inserts_at_zero() => ListReorder.InsertionIndex([], 42).Should().Be(0);

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(2, 1, false)]
    [InlineData(2, 4, false)]
    public void Dropping_on_its_own_slot_or_the_next_is_a_no_op(int from, int insertAt, bool expected) =>
        ListReorder.IsNoOp(from, insertAt).Should().Be(expected);

    [Theory]
    [InlineData(0, 3, 4, 2)]
    [InlineData(0, 4, 4, 3)]
    [InlineData(3, 0, 4, 0)]
    [InlineData(1, 9, 4, 3)]
    public void Move_target_accounts_for_the_removed_source(int from, int insertAt, int count, int expected) =>
        ListReorder.MoveTarget(from, insertAt, count).Should().Be(expected);
}
