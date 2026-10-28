//! Semantic versions: parse, compare, bump, and match caret requirements (`^1.2.3`).

use std::cmp::Ordering;
use std::fmt;
use std::str::FromStr;

/// A version `major.minor.patch`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct Version {
    pub major: u64,
    pub minor: u64,
    pub patch: u64,
}

/// Why a version did not parse.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    Empty,
    WrongPartCount(usize),
    NotANumber(String),
    LeadingZero(String),
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ParseError::Empty => write!(f, "empty version"),
            ParseError::WrongPartCount(n) => write!(f, "expected 3 parts, found {n}"),
            ParseError::NotANumber(part) => write!(f, "not a number: {part:?}"),
            ParseError::LeadingZero(part) => write!(f, "leading zero in {part:?}"),
        }
    }
}

impl std::error::Error for ParseError {}

impl Version {
    pub const fn new(major: u64, minor: u64, patch: u64) -> Self {
        Version {
            major,
            minor,
            patch,
        }
    }

    pub fn bump_major(self) -> Self {
        Version::new(self.major + 1, 0, 0)
    }

    pub fn bump_minor(self) -> Self {
        Version::new(self.major, self.minor + 1, 0)
    }

    pub fn bump_patch(self) -> Self {
        Version::new(self.major, self.minor, self.patch + 1)
    }

    /// Whether this version meets `^req`: not lower than `req`, and the same in `req`'s leftmost non-zero part
    /// (so `^1.2.3` allows `1.x`, `^0.2.3` allows `0.2.x` and `^0.0.3` only `0.0.3`).
    pub fn matches_caret(&self, req: &Version) -> bool {
        if self < req {
            return false;
        }
        match (req.major, req.minor) {
            (0, 0) => self.major == 0 && self.minor == 0 && self.patch == req.patch,
            (0, minor) => self.major == 0 && self.minor == minor,
            (major, _) => self.major == major,
        }
    }
}

impl Ord for Version {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.major, self.minor, self.patch).cmp(&(other.major, other.minor, other.patch))
    }
}

impl PartialOrd for Version {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
    }
}

impl FromStr for Version {
    type Err = ParseError;

    /// Parses `1.2.3`, also with a leading `v`.
    fn from_str(text: &str) -> Result<Self, ParseError> {
        let text = text.trim();
        let text = text.strip_prefix('v').unwrap_or(text);
        if text.is_empty() {
            return Err(ParseError::Empty);
        }
        let parts: Vec<&str> = text.split('.').collect();
        if parts.len() != 3 {
            return Err(ParseError::WrongPartCount(parts.len()));
        }
        let mut numbers = [0u64; 3];
        for (slot, part) in numbers.iter_mut().zip(&parts) {
            if part.is_empty() || !part.bytes().all(|b| b.is_ascii_digit()) {
                return Err(ParseError::NotANumber(part.to_string()));
            }
            if part.len() > 1 && part.starts_with('0') {
                return Err(ParseError::LeadingZero(part.to_string()));
            }
            *slot = part
                .parse()
                .map_err(|_| ParseError::NotANumber(part.to_string()))?;
        }
        Ok(Version::new(numbers[0], numbers[1], numbers[2]))
    }
}
