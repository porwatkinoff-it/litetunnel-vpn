LiteTunnel @VERSION@ для Linux (x86-64)

Установка
  tar xzf LiteTunnel-@VERSION@-linux-x64.tar.gz
  cd LiteTunnel-@VERSION@-linux-x64
  ./install.sh

Приложение появится в меню, ссылки litetunnel:// из браузера будут открываться в нём.
Можно и без установки: ./LiteTunnel в этой папке.

Режимы
  Системный прокси: GNOME и KDE. При выходе и после сбоя настройки возвращаются как были.
  VPN (TUN): весь трафик. Нужны права на сетевые настройки: приложение спросит пароль
  при подключении (pkexec) либо один раз выполните ./install.sh --setcap.

Нужно в системе
  X11 или XWayland, fontconfig, libICE, libSM (есть почти везде).
  Значок в трее: в GNOME нужно расширение AppIndicator, в KDE работает сразу.
  Закрытое окно при активном подключении уходит в трей; «Выход» в меню значка.

Данные: ~/.local/share/LiteTunnel. Удаление: ./install.sh --uninstall

sing-box
  Ядро sing-box @SING_BOX@ собрано из исходников с правкой для VLESS Reality с Xray 26.9+
  (sing-box-reality-mlkem.patch). Исходники: https://github.com/SagerNet/sing-box, тег
  v@SING_BOX@, правка и параметры сборки в SING-BOX-BUILD.txt. Лицензия GPLv3: LICENSE.sing-box.
