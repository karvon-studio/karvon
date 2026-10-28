import re

UNITS = {"d": 86400, "h": 3600, "m": 60, "s": 1}
_PART = re.compile(r"(\d+)([dhms])")


def parse_duration(text: str) -> int:
    """'1h30m' or '1h 30m' -> 5400. Units d, h, m and s, largest first, each at most once."""
    compact = text.replace(" ", "").lower()
    if not compact:
        raise ValueError("empty duration")
    total, pos, last = 0, 0, -1
    for match in _PART.finditer(compact):
        order = "dhms".index(match.group(2))
        if match.start() != pos or order <= last:
            raise ValueError(f"not a duration: {text!r}")
        total += int(match.group(1)) * UNITS[match.group(2)]
        pos, last = match.end(), order
    if pos != len(compact):
        raise ValueError(f"not a duration: {text!r}")
    return total


def format_duration(seconds: int) -> str:
    """5400 -> '1h 30m', 0 -> '0s'."""
    if seconds < 0:
        raise ValueError("negative duration")
    if seconds == 0:
        return "0s"
    parts = []
    for unit, size in UNITS.items():
        count, seconds = divmod(seconds, size)
        if count:
            parts.append(f"{count}{unit}")
    return " ".join(parts)
