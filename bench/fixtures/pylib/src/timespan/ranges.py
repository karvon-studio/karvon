Range = tuple[int, int]


def merge_ranges(ranges: list[Range]) -> list[Range]:
    """Merges the (start, end) ranges that overlap or touch; returns them sorted."""
    merged: list[Range] = []
    for start, end in sorted(ranges):
        if start > end:
            raise ValueError(f"range ends before it starts: {(start, end)}")
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    return merged


def total_covered(ranges: list[Range]) -> int:
    """Seconds covered by at least one range."""
    return sum(end - start for start, end in merge_ranges(ranges))
