# LiteTunnel

VPN-клиент на ядре [sing-box](https://github.com/SagerNet/sing-box) для iOS,
macOS, Android, Windows и Linux. Без своего сервера и аккаунтов: вы добавляете
подписку или ссылку на сервер, приложение подключается. Главное требование при
разработке — как можно меньше расход батареи.

Скачать: [последний релиз](https://github.com/porwatkinoff-it/litetunnel-vpn/releases/latest).

## Что умеет

- Протоколы: VLESS (Reality, Vision, TLS, WebSocket, gRPC, XHTTP), VMess,
  Trojan, Shadowsocks (включая 2022), Hysteria2, TUIC.
- Подписки: ссылки, base64, JSON sing-box и Xray. Показывает трафик, срок,
  объявление и ссылку на поддержку, если панель их отдаёт. Обновление по
  расписанию, сворачивание, пинг и обновление каждой подписки отдельно.
- Авто-выбор: группа серверов, подключение идёт через самый быстрый.
- Пинг пятью способами: реальный (через сервер), через сервер GET или HEAD,
  TCP, ICMP. Вид — цифры, полоски или точки.
- Маршрутизация: всё через VPN или Россия напрямую, блокировка рекламы, свои
  правила для доменов. DNS через VPN и напрямую, отдельно.
- Kill switch, IPv6, фрагментация TLS, выбор сетевого стека и MTU.
- Умный сон: пока экран выключен, ядро закрывает простаивающие соединения и
  останавливает таймеры.

Дизайн одинаковый на всех платформах: [design/DESIGN.md](design/DESIGN.md).

## Установка

| файл | платформа | как поставить |
| --- | --- | --- |
| `LiteTunnel-<версия>.dmg` | macOS 13+ | Перетащите в «Программы». Первый запуск: правый клик → «Открыть». Сборка без платного аккаунта Apple, поэтому работает как системный прокси, а не как VPN. |
| `LiteTunnel-<версия>.ipa` | iOS 16+ | Подписана ad-hoc. На обычный iPhone её нужно переподписать своим аккаунтом разработчика (VPN требует платный), без этого ставится только через TrollStore. |
| `LiteTunnel-<версия>.apk` | Android 8+ | Откройте файл на телефоне и разрешите установку из этого источника. |

## Сборка из исходников

Apple (нужны Xcode, Go и [XcodeGen](https://github.com/yonaskolb/XcodeGen)):

```bash
scripts/build-libbox.sh   # ядро sing-box → Frameworks/Libbox.xcframework
xcodegen generate         # LiteTunnel.xcodeproj из project.yml
scripts/package.sh        # dist/LiteTunnel-<версия>.ipa и .dmg
```

Скрипты сборки ядра накладывают на sing-box правку
[scripts/patches/sing-box-reality-mlkem.patch](scripts/patches/sing-box-reality-mlkem.patch):
без неё VLESS Reality не подключается к серверам на Xray 26.9 и новее.

Чтобы собрать настоящий VPN для своих устройств, впишите свою команду в
`Config/Signing.xcconfig` и соберите в Xcode.

Android (нужны JDK 17, Android SDK и NDK, Go):

```bash
android/scripts/build-libbox-android.sh   # ядро → android/app/libs/libbox.aar
cd android && ./gradlew assembleRelease
```

Тесты общей логики: `cd Packages/LiteKit && swift test`.

## Лицензия

GPL-3.0, как и sing-box, на котором приложение построено. См. [LICENSE](LICENSE).
