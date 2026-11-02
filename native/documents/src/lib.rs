//! Karvon Studio's documents: four engines' MCP servers (PdfCraft for PDF, WordCraft for Word, GridCraft for Excel,
//! DeckCraft for PowerPoint), headless, in Karvon's own process behind a C ABI: a server opened by its kind, a JSON-RPC
//! message in and its answer out, closed. Karvon calls a server from one thread of its own, one call at a time.

use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

use serde_json::{Value, json};

/// One engine's MCP server, over a session of its own (no window).
pub enum Server {
    Pdf(pdfcraft_automation::mcp::McpServer),
    Word(wordcraft_mcp::Server),
    Excel(gridcraft_mcp::Server),
    Slides(deckcraft_mcp::Server),
}

impl Server {
    /// `pdf`, `word`, `excel` or `slides`; none for another kind.
    pub fn open(kind: &str) -> Option<Self> {
        Some(match kind {
            // Compact: a core set of tools listed, every other one found with tool_search and run with tool_call.
            "pdf" => Server::Pdf(pdfcraft_automation::mcp::McpServer::new(pdfcraft_automation::Automation::new()).with_compact(true)),
            "word" => Server::Word(wordcraft_mcp::Server::new(Box::new(wordcraft_mcp::Headless::default()))),
            "excel" => Server::Excel(gridcraft_mcp::Server::new(Box::new(gridcraft_mcp::Headless::new()))),
            "slides" => Server::Slides(deckcraft_mcp::Server::new(Box::new(deckcraft_mcp::Headless::with_document()))),
            _ => return None,
        })
    }

    /// One JSON-RPC message: its answer, none for a notification.
    pub fn handle_line(&mut self, line: &str) -> Option<String> {
        match self {
            Server::Pdf(s) => s.handle_line(line),
            Server::Word(s) => s.handle_line(line),
            Server::Excel(s) => s.handle_line(line),
            Server::Slides(s) => s.handle_line(line),
        }
    }
}

fn text(ptr: *const c_char) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    // SAFETY: Karvon passes a NUL-terminated UTF-8 string it keeps alive for the call.
    unsafe { CStr::from_ptr(ptr) }.to_str().ok().map(str::to_owned)
}

fn out(s: String) -> *mut c_char {
    CString::new(s.replace('\0', "")).map_or(std::ptr::null_mut(), CString::into_raw)
}

/// The library's version, for Karvon to tell which one it loaded.
#[unsafe(no_mangle)]
pub extern "C" fn karvon_documents_version() -> *const c_char {
    c"0.1.0".as_ptr()
}

/// A server of `kind` (pdf, word, excel, slides); null for another kind or when it could not start.
#[unsafe(no_mangle)]
pub extern "C" fn karvon_documents_open(kind: *const c_char) -> *mut Server {
    let Some(kind) = text(kind) else { return std::ptr::null_mut() };
    match catch_unwind(|| Server::open(&kind)) {
        Ok(Some(server)) => Box::into_raw(Box::new(server)),
        _ => std::ptr::null_mut(),
    }
}

/// The answer to one JSON-RPC message (to free with [`karvon_documents_free`]); null for a notification. An engine that
/// panics answers with a JSON-RPC error, and the server should be closed then.
///
/// # Safety
/// `server` is one [`karvon_documents_open`] returned and not closed; `line` a NUL-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn karvon_documents_handle(server: *mut Server, line: *const c_char) -> *mut c_char {
    let Some(line) = text(line) else { return std::ptr::null_mut() };
    if server.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: as the caller promises, a live server no other thread uses now.
    let server = unsafe { &mut *server };
    match catch_unwind(AssertUnwindSafe(|| server.handle_line(&line))) {
        Ok(Some(answer)) => out(answer),
        Ok(None) => std::ptr::null_mut(),
        Err(panic) => {
            let why = panic.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| panic.downcast_ref::<String>().cloned()).unwrap_or_default();
            let id = serde_json::from_str::<Value>(&line).ok().and_then(|m| m.get("id").cloned()).unwrap_or(Value::Null);
            out(json!({"jsonrpc": "2.0", "id": id, "error": {"code": -32603, "message": format!("the engine failed: {why}")}}).to_string())
        }
    }
}

/// Closes a server (its unsaved changes go).
///
/// # Safety
/// `server` is one [`karvon_documents_open`] returned, not closed yet, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn karvon_documents_close(server: *mut Server) {
    if !server.is_null() {
        // SAFETY: as the caller promises, it came from Box::into_raw and is closed once.
        let _ = catch_unwind(AssertUnwindSafe(|| drop(unsafe { Box::from_raw(server) })));
    }
}

/// Frees an answer of [`karvon_documents_handle`].
///
/// # Safety
/// `text` is one [`karvon_documents_handle`] returned, not freed yet, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn karvon_documents_free(text: *mut c_char) {
    if !text.is_null() {
        // SAFETY: as the caller promises, it came from CString::into_raw and is freed once.
        drop(unsafe { CString::from_raw(text) });
    }
}
