# Third-party components

Karvon Studio is MIT ([LICENSE](../LICENSE)). What it ships with or uses, and their licenses:

Cascadia Mono font (SIL OFL 1.1); Avalonia, AvaloniaEdit, TextMateSharp,
Exclr8.Terminal and Porta.Pty (MIT); Markdig (BSD 2-Clause); roslyn-language-server, netcoredbg, js-debug, Pyright, debugpy, Delve and CodeLLDB (MIT), gopls (BSD-3-Clause) and
rust-analyzer (MIT or Apache-2.0), clangd (Apache-2.0 with LLVM exceptions), jdtls (EPL-2.0) and java-debug (EPL-1.0),
JetBrains' kotlin-lsp (IntelliJ's Apache-2.0 sources and proprietary parts of JetBrains Air and Fleet, under JetBrains' terms),
downloaded or built at runtime;
Material Icon Theme icons (MIT, Copyright (c) 2025 Material Extensions; `src/Karvon.Core/Assets/MaterialIcons`, refreshed with
`scripts/update-material-icons.sh`, and `src/Karvon.Database/Assets/Icons`). The Database Explorer uses Bengine (same
author) and its drivers: Npgsql (PostgreSQL License), Microsoft.Data.SqlClient, MySqlConnector, SSH.NET and
Avalonia.Controls.DataGrid (MIT), MongoDB.Driver and SQLitePCLRaw (Apache 2.0), Oracle.ManagedDataAccess.Core (Oracle
Free Use Terms and Conditions). The Kotlin template writes Gradle 9.1.0's wrapper (`gradlew`, `gradlew.bat` and
`gradle-wrapper.jar`, Apache-2.0, Copyright the original authors; `src/Karvon.Kotlin/GradleWrapper`) into the projects
it makes.
