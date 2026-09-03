#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use tauri::Manager;

fn main() {
    tauri::Builder::default()
        .menu(|handle| {
            tauri::menu::MenuBuilder::new(handle)
                .text("refresh", "刷新")
                .text("about", "关于 LlamaHarness")
                .build()
        })
        .on_menu_event(|app, event| {
            if event.id().as_ref() == "refresh" {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.eval("window.refresh && window.refresh()") ;
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running LlamaHarness UI");
}
