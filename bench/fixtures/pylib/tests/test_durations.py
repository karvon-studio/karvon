import pytest

from timespan import format_duration, parse_duration


def test_parse_reads_each_unit():
    assert parse_duration("90s") == 90
    assert parse_duration("1h30m") == 5400
    assert parse_duration("2d") == 172800
    assert parse_duration("1h 5m 3s") == 3903


@pytest.mark.parametrize("text", ["", "abc", "5x", "1m1h", "1h1h", "h1"])
def test_parse_rejects_what_is_not_a_duration(text):
    with pytest.raises(ValueError):
        parse_duration(text)


def test_format_uses_the_largest_units():
    assert format_duration(5400) == "1h 30m"
    assert format_duration(90061) == "1d 1h 1m 1s"
    assert format_duration(0) == "0s"


def test_format_rejects_negative_durations():
    with pytest.raises(ValueError):
        format_duration(-1)


def test_format_and_parse_round_trip():
    for seconds in (1, 59, 3600, 86399, 90061):
        assert parse_duration(format_duration(seconds)) == seconds
