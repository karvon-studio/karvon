# gridcraft-pdf, with embedded fonts

GridCraft's `crates/pdf` at the commit `native/documents/Cargo.toml` pins, with one change: text the standard 14 fonts
cannot encode (Cyrillic, Greek and more) is set in an embedded font instead of turning into `?`
([storytold/gridcraft#446](https://github.com/storytold/gridcraft/issues/446), still open). Karvon draws Excel previews
through this writer, so without it a sheet in Cyrillic shows only question marks.

- `src/embedded.rs` is new; `src/lib.rs` calls it from `text_width`, `char_width`, `text_rotated` and `finish`.
- The fonts are Liberation 2 (SIL OFL 1.1, `fonts/LICENSE.txt`), unmodified: as wide as Arial, Times New Roman and Courier
  New, so a sheet keeps its layout. They have no Arabic, Thai or CJK; those still come out as `?`.
- Remove this folder and its `[patch]` in `native/documents/Cargo.toml` once upstream embeds fonts.
