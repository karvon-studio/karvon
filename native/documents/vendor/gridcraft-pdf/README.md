# gridcraft-pdf, with embedded fonts

GridCraft's `crates/pdf` at the commit `native/documents/Cargo.toml` pins, with one change: text the standard 14 fonts
cannot encode (Cyrillic, Greek and more) is set in an embedded font. Upstream sets it in STSong-Light, a Chinese font
not embedded, which has no Uzbek letters (Ҳ, Қ, Ғ, Ў) and which a reader without it draws as it can
([storytold/gridcraft#446](https://github.com/storytold/gridcraft/issues/446), still open). Karvon draws Excel previews
through this writer, so without it a sheet in Uzbek Cyrillic loses letters.

- `src/embedded.rs` is new; `src/lib.rs` calls it from `char_width`, `standard_text` (a run of its own beside the
  standard font's and the CID font's) and `finish`.
- The fonts are Liberation 2 (SIL OFL 1.1, `fonts/LICENSE.txt`), unmodified: as wide as Arial, Times New Roman and Courier
  New, so a sheet keeps its layout. What they have not (Arabic, Thai, CJK) goes to upstream's own fonts as before.
- `Cargo.toml` names the dependencies GridCraft's workspace gives it at that commit.
- Remove this folder and its `[patch]` in `native/documents/Cargo.toml` once upstream embeds fonts.
