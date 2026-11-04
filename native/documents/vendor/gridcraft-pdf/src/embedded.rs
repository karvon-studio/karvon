//! Karvon's addition to GridCraft's PDF writer: text the standard fonts cannot encode, in an embedded font.
//!
//! One Liberation face stands for each standard one (they are as wide as Arial, Times New Roman and Courier New), embedded
//! whole as a CID TrueType font with Identity-H, and with a ToUnicode map so the text can be copied and searched.

use std::collections::{BTreeMap, HashMap};
use std::fmt::Write as _;
use std::sync::OnceLock;

/// In the order of `Font::ALL`.
static FILES: [(&str, &[u8]); 12] = [
    ("LiberationSans", include_bytes!("../fonts/LiberationSans-Regular.ttf")),
    ("LiberationSans-Bold", include_bytes!("../fonts/LiberationSans-Bold.ttf")),
    ("LiberationSans-Italic", include_bytes!("../fonts/LiberationSans-Italic.ttf")),
    ("LiberationSans-BoldItalic", include_bytes!("../fonts/LiberationSans-BoldItalic.ttf")),
    ("LiberationSerif", include_bytes!("../fonts/LiberationSerif-Regular.ttf")),
    ("LiberationSerif-Bold", include_bytes!("../fonts/LiberationSerif-Bold.ttf")),
    ("LiberationSerif-Italic", include_bytes!("../fonts/LiberationSerif-Italic.ttf")),
    ("LiberationSerif-BoldItalic", include_bytes!("../fonts/LiberationSerif-BoldItalic.ttf")),
    ("LiberationMono", include_bytes!("../fonts/LiberationMono-Regular.ttf")),
    ("LiberationMono-Bold", include_bytes!("../fonts/LiberationMono-Bold.ttf")),
    ("LiberationMono-Italic", include_bytes!("../fonts/LiberationMono-Italic.ttf")),
    ("LiberationMono-BoldItalic", include_bytes!("../fonts/LiberationMono-BoldItalic.ttf")),
];

pub(crate) struct Face {
    name: &'static str,
    data: &'static [u8],
    units: u32,
    bbox: [i32; 4],
    ascent: i32,
    descent: i32,
    cap_height: i32,
    italic_angle: i32,
    hmtx: usize,
    metrics: usize,
    glyphs: HashMap<u32, u16>,
}

fn u16_at(d: &[u8], at: usize) -> Option<u16> {
    let b = d.get(at..at.checked_add(2)?)?;
    Some(u16::from_be_bytes([*b.first()?, *b.get(1)?]))
}

fn i16_at(d: &[u8], at: usize) -> Option<i32> {
    u16_at(d, at).map(|v| i32::from(v as i16))
}

fn u32_at(d: &[u8], at: usize) -> Option<u32> {
    let b = d.get(at..at.checked_add(4)?)?;
    Some(u32::from_be_bytes([*b.first()?, *b.get(1)?, *b.get(2)?, *b.get(3)?]))
}

fn table(d: &[u8], tag: &[u8; 4]) -> Option<usize> {
    let count = usize::from(u16_at(d, 4)?);
    (0..count).find_map(|i| {
        let record = 12 + i * 16;
        if d.get(record..record + 4)? == tag { u32_at(d, record + 8).map(|o| o as usize) } else { None }
    })
}

/// The characters of a format 4 (BMP) or format 12 (all of Unicode) cmap subtable.
fn read_cmap(d: &[u8], t: usize) -> Option<HashMap<u32, u16>> {
    let mut map = HashMap::new();
    match u16_at(d, t)? {
        4 => {
            let seg_x2 = usize::from(u16_at(d, t + 6)?);
            let (ends, starts) = (t + 14, t + 16 + seg_x2);
            let (deltas, ranges) = (starts + seg_x2, starts + 2 * seg_x2);
            for i in (0..seg_x2).step_by(2) {
                let (end, start) = (u16_at(d, ends + i)?, u16_at(d, starts + i)?);
                let (delta, range) = (u16_at(d, deltas + i)?, usize::from(u16_at(d, ranges + i)?));
                for c in start..=end {
                    if c == 0xFFFF {
                        continue;
                    }
                    let glyph = if range == 0 {
                        c.wrapping_add(delta)
                    } else {
                        match u16_at(d, ranges + i + range + 2 * usize::from(c - start))? {
                            0 => 0,
                            g => g.wrapping_add(delta),
                        }
                    };
                    if glyph != 0 {
                        map.insert(u32::from(c), glyph);
                    }
                }
            }
        }
        12 => {
            let groups = u32_at(d, t + 12)? as usize;
            for i in 0..groups {
                let g = t + 16 + i * 12;
                let (start, end, first) = (u32_at(d, g)?, u32_at(d, g + 4)?, u32_at(d, g + 8)?);
                for c in start..=end.min(0x10FFFF) {
                    if let Ok(glyph) = u16::try_from(first + (c - start)) {
                        map.insert(c, glyph);
                    }
                }
            }
        }
        _ => return None,
    }
    Some(map)
}

fn parse(name: &'static str, d: &'static [u8]) -> Option<Face> {
    let head = table(d, b"head")?;
    let hhea = table(d, b"hhea")?;
    let cmap = table(d, b"cmap")?;
    // The widest Unicode subtable there is: Windows' full, then its BMP one, then Unicode's.
    let count = usize::from(u16_at(d, cmap + 2)?);
    let mut best: Option<(u8, usize)> = None;
    for i in 0..count {
        let record = cmap + 4 + i * 8;
        let (platform, encoding) = (u16_at(d, record)?, u16_at(d, record + 2)?);
        let rank = match (platform, encoding) {
            (3, 10) => 3,
            (3, 1) => 2,
            (0, _) => 1,
            _ => 0,
        };
        if rank > best.map_or(0, |b| b.0) {
            best = Some((rank, cmap + u32_at(d, record + 4)? as usize));
        }
    }
    let os2 = table(d, b"OS/2");
    let ascent = i16_at(d, hhea + 4)?;
    Some(Face {
        name,
        data: d,
        units: u32::from(u16_at(d, head + 18)?).max(1),
        bbox: [i16_at(d, head + 36)?, i16_at(d, head + 38)?, i16_at(d, head + 40)?, i16_at(d, head + 42)?],
        ascent,
        descent: i16_at(d, hhea + 6)?,
        cap_height: os2.and_then(|o| i16_at(d, o + 88)).filter(|h| *h > 0).unwrap_or(ascent),
        italic_angle: table(d, b"post").and_then(|p| i16_at(d, p + 4)).unwrap_or(0),
        hmtx: table(d, b"hmtx")?,
        metrics: usize::from(u16_at(d, hhea + 34)?).max(1),
        glyphs: read_cmap(d, best?.1)?,
    })
}

/// The face standing for the standard font at `index` of `Font::ALL`.
pub(crate) fn face(index: usize) -> Option<&'static Face> {
    static FACES: [OnceLock<Option<Face>>; 12] = [const { OnceLock::new() }; 12];
    let (name, data) = FILES.get(index)?;
    FACES.get(index)?.get_or_init(|| parse(name, data)).as_ref()
}

impl Face {
    /// A character's glyph; one the face does not have is a `?`, as the standard fonts draw it.
    pub(crate) fn glyph(&self, c: char) -> u16 {
        self.glyphs.get(&(c as u32)).or_else(|| self.glyphs.get(&('?' as u32))).copied().unwrap_or(0)
    }

    fn scaled(&self, v: i32) -> i64 {
        i64::from(v) * 1000 / i64::from(self.units)
    }

    fn glyph_width(&self, glyph: u16) -> u16 {
        let i = usize::from(glyph).min(self.metrics - 1);
        let advance = u16_at(self.data, self.hmtx + i * 4).unwrap_or(0);
        u16::try_from((u32::from(advance) * 1000 + self.units / 2) / self.units).unwrap_or(u16::MAX)
    }

    /// Advance width of a character in 1/1000 em.
    pub(crate) fn width(&self, c: char) -> u16 {
        self.glyph_width(self.glyph(c))
    }

    /// The face's five PDF objects, numbered from `first` (the font itself), for the glyphs drawn with it.
    pub(crate) fn objects(&self, first: u32, used: &BTreeMap<u16, char>) -> Vec<(u32, Vec<u8>)> {
        let (cid, descriptor, file, unicode) = (first + 1, first + 2, first + 3, first + 4);
        let name = self.name;
        let mut widths = String::new();
        for glyph in used.keys() {
            let _ = write!(widths, "{glyph} [{}] ", self.glyph_width(*glyph));
        }
        let mut stream = format!("<< /Length {0} /Length1 {0} >>\nstream\n", self.data.len()).into_bytes();
        stream.extend_from_slice(self.data);
        stream.extend_from_slice(b"\nendstream");
        vec![
            (first, format!("<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {unicode} 0 R >>").into_bytes()),
            (cid, format!("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 1000 /W [{widths}] >>").into_bytes()),
            (descriptor, format!(
                "<< /Type /FontDescriptor /FontName /{name} /Flags 4 /FontBBox [{} {} {} {}] /ItalicAngle {} /Ascent {} /Descent {} /CapHeight {} /StemV 80 /FontFile2 {file} 0 R >>",
                self.scaled(self.bbox[0]), self.scaled(self.bbox[1]), self.scaled(self.bbox[2]), self.scaled(self.bbox[3]),
                self.italic_angle, self.scaled(self.ascent), self.scaled(self.descent), self.scaled(self.cap_height)).into_bytes()),
            (file, stream),
            (unicode, to_unicode(used)),
        ]
    }
}

/// The map from glyphs back to the text, so a PDF reader can copy and search it.
fn to_unicode(used: &BTreeMap<u16, char>) -> Vec<u8> {
    let mut map = String::from(
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n",
    );
    let pairs: Vec<(&u16, &char)> = used.iter().collect();
    for chunk in pairs.chunks(100) {
        let _ = writeln!(map, "{} beginbfchar", chunk.len());
        for (glyph, c) in chunk {
            let mut units = [0u16; 2];
            let text: String = c.encode_utf16(&mut units).iter().map(|u| format!("{u:04X}")).collect();
            let _ = writeln!(map, "<{glyph:04X}> <{text}>");
        }
        map.push_str("endbfchar\n");
    }
    map.push_str("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
    let mut stream = format!("<< /Length {} >>\nstream\n", map.len()).into_bytes();
    stream.extend_from_slice(map.as_bytes());
    stream.extend_from_slice(b"endstream");
    stream
}
