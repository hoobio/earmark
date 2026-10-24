using AwesomeAssertions;

using Earmark.Core.Routing;

using Xunit;

namespace Earmark.Core.Tests;

public class ListReorderTests
{
    private static readonly double[] Tops = [0, 20, 40, 60];
    private static readonly double[] Heights = [20, 20, 20, 20];

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(15, 1)]
    [InlineData(45, 2)]
    [InlineData(99, 4)]
    public void Insertion_into_another_list_counts_rows_whose_midpoint_is_above_the_pointer(double y, int expected) =>
        ListReorder.InsertionIndex(Tops, Heights, from: -1, slot: 20, y).Should().Be(expected);

    [Theory]
    [InlineData(5, 0)]
    [InlineData(15, 1)]
    [InlineData(35, 2)]
    [InlineData(99, 3)]
    public void Insertion_in_the_source_list_skips_the_dragged_row_and_closes_its_slot(double y, int expected) =>
        ListReorder.InsertionIndex(Tops, Heights, from: 1, slot: 20, y).Should().Be(expected);

    [Fact]
    public void Empty_list_inserts_at_zero() => ListReorder.InsertionIndex([], [], -1, 20, 42).Should().Be(0);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, -20)]
    [InlineData(3, -20)]
    public void Source_list_closes_the_gap_when_the_row_leaves(int index, double expected) =>
        ListReorder.Offset(index, from: 1, insertAt: -1, closeSlot: 20, openSlot: 20).Should().Be(expected);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, -20)]
    [InlineData(3, 0)]
    public void Moving_down_in_the_same_list_opens_the_gap_below(int index, double expected) =>
        ListReorder.Offset(index, from: 1, insertAt: 2, closeSlot: 20, openSlot: 20).Should().Be(expected);

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 20)]
    [InlineData(3, 0)]
    public void Moving_up_in_the_same_list_pushes_rows_down(int index, double expected) =>
        ListReorder.Offset(index, from: 3, insertAt: 0, closeSlot: 20, openSlot: 20).Should().Be(expected);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(2, 30)]
    public void Target_list_opens_a_gap_at_the_insertion_point(int index, double expected) =>
        ListReorder.Offset(index, from: -1, insertAt: 1, closeSlot: 20, openSlot: 30).Should().Be(expected);
}
