#!/usr/bin/env bash
# Publishes Karvon and registers it with the desktop: `karvon` command, app menu entry and icon.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_root="${DOTNET_ROOT:-$HOME/.dotnet}"
app_dir="$HOME/.local/share/karvon/app"
bin_dir="$HOME/.local/bin"
apps_dir="$HOME/.local/share/applications"
icons_dir="$HOME/.local/share/icons/hicolor"

echo "Publishing Karvon to $app_dir ..."
rm -rf "$app_dir"
"$dotnet_root/dotnet" publish "$repo/src/Karvon/Karvon.csproj" -c Release -r linux-x64 --self-contained false -o "$app_dir" -nologo -v:quiet

mkdir -p "$bin_dir" "$apps_dir" "$icons_dir/scalable/apps" "$icons_dir/256x256/apps"
cat > "$bin_dir/karvon" <<LAUNCHER
#!/bin/sh
# Desktop sessions do not read ~/.bashrc, so point the apphost at the user's .NET install explicitly.
export DOTNET_ROOT="\${DOTNET_ROOT:-$dotnet_root}"
exec "$app_dir/Karvon" "\$@"
LAUNCHER
chmod +x "$bin_dir/karvon"

cp "$repo/src/Karvon/Assets/karvon.svg" "$icons_dir/scalable/apps/karvon.svg"
cp "$repo/src/Karvon/Assets/karvon.png" "$icons_dir/256x256/apps/karvon.png"

cat > "$apps_dir/karvon.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Karvon Studio
GenericName=Integrated Development Environment
Comment=C#, ASP.NET Core and Blazor IDE
Exec=$bin_dir/karvon %f
Icon=karvon
Terminal=false
Categories=Development;IDE;
Keywords=karvon;ide;csharp;dotnet;blazor;razor;
StartupWMClass=Karvon
DESKTOP

command -v update-desktop-database >/dev/null && update-desktop-database "$apps_dir" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$icons_dir" || true
echo "Installed. Run 'karvon path/to/App.sln' or open Karvon Studio from the applications menu."
