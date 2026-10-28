"""Durations and time ranges, in whole seconds."""

from .durations import format_duration, parse_duration
from .ranges import merge_ranges, total_covered

__all__ = ["format_duration", "merge_ranges", "parse_duration", "total_covered"]
__version__ = "0.3.0"
