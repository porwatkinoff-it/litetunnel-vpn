#!/bin/sh
# Installs LiteTunnel for this user: the files under ~/.local/share/litetunnel,
# a menu entry, and litetunnel:// links from the browser.
#
#   ./install.sh             install or update
#   ./install.sh --setcap    also let VPN (TUN) mode run without a password (asks for sudo once)
#   ./install.sh --uninstall remove it again (settings stay in ~/.local/share/LiteTunnel)
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
DEST="$DATA/litetunnel"
DESKTOP_FILE="$DATA/applications/litetunnel.desktop"
ICON="$DATA/icons/hicolor/256x256/apps/litetunnel.png"

refresh() {
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$DATA/applications" || true
  command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$DATA/icons/hicolor" || true
}

if [ "${1:-}" = "--uninstall" ]; then
  rm -rf "$DEST"
  rm -f "$DESKTOP_FILE" "$ICON"
  refresh
  echo "LiteTunnel удалён. Настройки и подписки остались в $DATA/LiteTunnel."
  exit 0
fi

if pgrep -x LiteTunnel >/dev/null 2>&1; then
  echo "Закройте LiteTunnel (значок в трее → «Выход») и запустите установку снова." >&2
  exit 1
fi

if [ "$HERE" = "$DEST" ]; then
  echo "Запустите install.sh из распакованного архива, а не из $DEST." >&2
  exit 1
fi

mkdir -p "$(dirname "$DESKTOP_FILE")" "$(dirname "$ICON")"
# A fresh copy each time: setcap rights belong to the old sing-box file.
rm -rf "$DEST"
mkdir -p "$DEST"
cp -R "$HERE"/. "$DEST"/
rm -f "$DEST/install.sh"
chmod 755 "$DEST/LiteTunnel" "$DEST/sing-box"
cp "$HERE/litetunnel.png" "$ICON"

sed "s|@EXEC@|\"$DEST/LiteTunnel\"|" "$HERE/litetunnel.desktop" >"$DESKTOP_FILE"
chmod 644 "$DESKTOP_FILE"
refresh
command -v xdg-mime >/dev/null 2>&1 && xdg-mime default litetunnel.desktop x-scheme-handler/litetunnel || true

echo "LiteTunnel установлен в $DEST и добавлен в меню приложений."

if [ "${1:-}" = "--setcap" ]; then
  # Only the core gets the network rights, not the app.
  sudo setcap cap_net_admin,cap_net_raw,cap_net_bind_service+ep "$DEST/sing-box"
  echo "Режим VPN (TUN) теперь включается без пароля."
else
  echo "Для режима VPN (TUN) приложение спросит пароль при подключении."
  echo "Чтобы не спрашивало: ./install.sh --setcap"
fi
