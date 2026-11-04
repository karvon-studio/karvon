# karvon-documents

The document engines Karvon's Documents module loads (`src/Karvon.Documents`): the MCP servers of
[PdfCraft](https://github.com/storytold/pdfcraft), [WordCraft](https://github.com/storytold/wordcraft),
[GridCraft](https://github.com/storytold/gridcraft) and [DeckCraft](https://github.com/storytold/deckcraft), headless,
at the commits `Cargo.toml` pins, behind a C ABI (`src/lib.rs`).

```sh
cargo build --release --locked   # target/release/libkarvon_documents.so; the tests' live test uses it
```

GridCraft's PDF writer comes from `vendor/gridcraft-pdf`, which embeds fonts for text like Cyrillic (see its README).

A new version: raise `version`, run the `documents` workflow, upload its zips (and their `SHA256SUMS`) to a
`documents-<version>` prerelease, and pin the version and hashes in `DocumentEnginesLocator`.
