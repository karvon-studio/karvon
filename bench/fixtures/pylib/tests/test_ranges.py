import pytest

from timespan import merge_ranges, total_covered


def test_merge_sorts_and_joins_overlapping_ranges():
    assert merge_ranges([(5, 8), (1, 3), (2, 4)]) == [(1, 4), (5, 8)]


def test_merge_joins_ranges_that_touch():
    assert merge_ranges([(1, 3), (3, 5), (9, 10)]) == [(1, 5), (9, 10)]


def test_merge_keeps_the_outer_range_of_nested_ones():
    assert merge_ranges([(1, 10), (2, 3)]) == [(1, 10)]


def test_merge_rejects_a_range_that_ends_before_it_starts():
    with pytest.raises(ValueError):
        merge_ranges([(5, 1)])


def test_total_counts_overlaps_once():
    assert total_covered([(0, 10), (5, 15), (20, 25)]) == 20
    assert total_covered([]) == 0
