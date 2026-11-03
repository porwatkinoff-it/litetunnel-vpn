# LiteTunnel: one design for every platform

iOS, macOS, Android, Windows and Linux show the same screens, in the same
order, with the same words, colours and icons. The iOS app is the reference:
when this file and a platform disagree, follow this file; when this file says
nothing, do what the iOS app does.

Each platform keeps its own controls (a switch looks like an iOS switch on
iOS and a Material switch on Android) and its own system font. What must
match: layout, order, labels, colours, icons, states.

Values are in `tokens.json`. Units: pt on Apple, dp on Android, px (at 100%
scale) on desktop.

## Colours

| role | light | dark | used for |
| --- | --- | --- | --- |
| accent | `#1EB082` | `#33CC99` | selected server, primary buttons, links, progress |
| connected | `#34C759` | `#30D158` | power button when on, good ping, dots |
| busy | `#FF9500` | `#FF9F0A` | connecting, middling ping, "не скачаны" |
| danger | `#FF3B30` | `#FF453A` | errors, bad or failed ping, delete, usage over 90% |
| background | `#F2F2F7` | `#000000` | window / screen behind the groups |
| surface | `#FFFFFF` | `#1C1C1E` | grouped list sections, cards |
| text | `#000000` | `#FFFFFF` | primary text |
| text-secondary | `#6C6C70` | `#8E8E93` | captions, footers, idle power button |
| separator | `#C6C6C8` | `#38383A` | lines between rows |

No dynamic colour (Material You), no brand blue: the accent is the same
everywhere. Follow the system light/dark setting.

## Type

The system font: SF Pro, Roboto, Segoe UI Variable, and the desktop default
(Cantarell, Ubuntu, Noto Sans) on Linux.

| style | size | weight | used for |
| --- | --- | --- | --- |
| title | 20 | semibold | status under the power button |
| headline | 17 | semibold | subscription name, empty-state title |
| body | 17 iOS · 16 Android · 14 desktop | regular | row titles, settings |
| callout | 16 iOS · 15 Android · 13 desktop | regular | selected server under the status |
| rowTitle | 15 iOS · 14 Android · 13 macOS, desktop | regular | server name in the list |
| rowDetail | 11 | regular | «Протокол · транспорт» in the list, no gap under the name |
| caption | 12 | regular | row detail line, ping, usage, "Обновлена…" |
| footer | 13 iOS · 12 elsewhere | regular | section footers |

Ping, the connection timer and traffic use tabular (monospaced) digits.
Sentence case everywhere; section headers are not upper-cased.

## Shape and spacing

- Spacing steps: 4, 8, 12, 16, 20, 24.
- Lists are grouped: sections on `surface` over `background`, radius 10,
  16 side inset. Row height at least 44 (48 on Android and desktop). The
  server list is denser, so more fits: about 40 on Apple, 44 on Android, 38 on
  desktop, and the two lines of a row sit right under each other.
- Cards and the QR code: radius 12.
- Buttons: radius 10. One primary (filled, accent) button per screen at most.
- Desktop: a single column, 640 wide at most, centred; window at least
  420 × 640. On macOS the window itself stops at 640 wide.

## Icons

SF Symbols on Apple; Material Symbols Rounded on Android, Windows and Linux.

| meaning | SF Symbol | Material Symbol |
| --- | --- | --- |
| connect | `power` | `power_settings_new` |
| ping | `bolt.horizontal` | `bolt` |
| refresh | `arrow.clockwise` | `refresh` |
| more | `ellipsis.circle` | `more_horiz` |
| fold | `chevron.down` (−90° when folded) | `expand_more` (−90° when folded) |
| details | `info.circle` | `info` |
| add | `plus` | `add` |
| settings | `gearshape` | `settings` |
| selected / not | `checkmark.circle.fill` / `circle` | `check_circle` (filled) / `radio_button_unchecked` |
| ping bars | `cellularbars` | `signal_cellular_alt` (+ `_2_bar`, `_1_bar`) |
| announce | `megaphone` | `campaign` |
| delete | `trash` | `delete` |
| upload / download | `arrow.up` / `arrow.down` | `arrow_upward` / `arrow_downward` |
| empty list | `server.rack` | `dns` |

## Screens

### Home

One scrolling list, title **LiteTunnel**. Toolbar: **Добавить** (add),
**Пинг всех** (ping; filled while pinging), **Настройки** (settings).

1. **Connect card**, centred, 12 above and below:
   - power button: circle 112, ring 3 wide, icon 38 semibold. Ring and icon
     in the status colour (connected / busy / text-secondary), fill the same
     colour at 18% when on and 8% when off. Tap toggles.
   - status, title style: «Подключено», «Подключение…», «Переподключение…»,
     «Отключение…», «Отключено». The button is disabled while disconnecting.
   - timer `MM:SS` or `H:MM:SS` while connected, callout, secondary.
   - selected server name (callout, one line) and its protocol, or «Самый
     быстрый из N» for a group (caption, secondary); «Сервер не выбран» when
     none.
   - while connected: ↑ speed and total, ↓ speed and total, caption.
2. **Empty state**, only with no servers and no subscriptions: `server.rack`
   large and secondary, «Серверов пока нет» (headline), «Добавьте ссылку на
   подписку или сервер: vless://, hy2://, trojan://, ss:// и другие.»
   (callout, secondary, centred), primary button «Добавить».
3. **One section per subscription**, in the user's order. Header:
   - name (headline, primary colour, one line) and the fold chevron; tapping
     either folds the section. Folded state is remembered per device.
   - right side, 14 apart: ping (`bolt.horizontal`), refresh
     (`arrow.clockwise`), more (`ellipsis.circle`). Ping and refresh turn
     into a small spinner while they run.
   - more menu: Настроить · Поддержка (if the subscription gives a link) ·
     Сайт провайдера (if given) · divider · Удалить подписку (danger, asks
     «Удалить подписку «name» и её серверы?»).
   - usage line, if the subscription sends it: progress bar (accent, danger
     over 90%), then «12 ГБ из 100 ГБ» or «12 ГБ · без лимита» left and
     «до 1 дек. 2026 · 30 дн.» right (danger when expired), caption.
   - last line, caption secondary: «Обновлена 5 мин. назад», plus
     « · 12 серверов» when folded, counting real servers: the auto row and
     groups count by the servers inside them, each once. An error replaces
     it, in danger, two lines.
   - inside: the announce line first if any (`megaphone`, footer size,
     secondary), then the auto row, then the servers.
4. **«Мои серверы»**: servers added by link, swipe (or right-click) to delete.

Server row: selection mark (accent when selected, secondary when not; 20 on
Android, 18 on desktop), 10 gap, then name (rowTitle, one line) over
«Протокол · транспорт» (rowDetail, secondary, one line); then the ping badge
and a details button. Tap the row to select; long-press / right-click:
Подробнее · Проверить пинг · Копировать ссылку · Удалить.

Auto row: **Авто-выбор** over «Самый быстрый из N», first in its subscription.
Every subscription with two or more servers gets one, unless a group from the
provider already holds all of them; a group that holds only some does not
count. The core picks the lowest ping. A new subscription starts on this row;
a server chosen earlier stays chosen. It has no link to copy and no QR code,
and lists its servers on the details screen.

### Ping badge

Colour by milliseconds: under 150 connected, under 400 busy, otherwise or
failed danger. Text: «87 мс», failed «нет связи». Not yet measured: nothing.

| setting «Вид» | shows |
| --- | --- |
| Цифры (default) | the text, caption, tabular digits |
| Полоски | signal bars: 4 under 100, 3 under 200, 2 under 400, 1 above, 0 failed |
| Цифры и полоски | text, 4 gap, bars |
| Точки | an 8 circle: connected colour when measured, danger when failed |

Screen readers read the text in every style.

### Settings

Sections in this order, with these labels:

1. **Маршрутизация**: Режим (Всё через VPN / Россия напрямую) · Блокировать
   рекламу · Свои правила для доменов › · Обновить базы.
2. **DNS**.
3. **Подключение**: Kill switch (or «Локальная сеть мимо прокси» where the
   app runs as a proxy) · Локальная сеть мимо VPN · IPv6 · Фрагментация TLS.
4. **Энергосбережение**: Умный сон.
5. **Список серверов**: Сортировка (Как в подписке / По пингу / По имени).
   Groups stay on top when sorting by ping or name.
6. **Пинг**: Способ · Адрес проверки (proxy modes only) · Вид · Пинг при
   открытии · Пинг после обновления подписки. Footer explains the chosen way.
7. **Дополнительно**: Определять сайт (sniffing) · Сетевой стек · MTU ·
   Журнал · Открыть журнал ›.
8. **О приложении**: Версия · Ядро sing-box (GPLv3) link.

Footers are written as on iOS (`App/Shared/Views/SettingsView.swift`).

## Ping

| «Способ» | what it does | shows |
| --- | --- | --- |
| Реальный (default) | GET to the test address through the server | time ÷ 3.3, rounded, at least 1 |
| Через сервер, GET | the same | the full time |
| Через сервер, HEAD | HEAD instead of GET | the full time |
| TCP | TCP handshake to the server (QUIC for Hysteria2 / TUIC) | the time |
| ICMP | one echo request | the time |

- Test address: `http://www.gstatic.com/generate_204` by default; any http or
  https address the user enters; anything else falls back to the default.
- A request counts as passed with status 200–399, timed up to the response
  headers; 5 s timeout. A group (Авто-выбор) shows its fastest member. A group
  whose servers are all rows of its subscription is measured through those
  rows, so no server is tested twice.
- The proxy modes start a short-lived second core with one local mixed
  inbound per server, routed inbound → that server, and close it when done.
  Its sockets go around the running VPN. If it does not start, show «Пинг
  через сервер не запустился: … Показан TCP.» and measure TCP.
- At most 8 at a time. Results appear as they come in.
- «Пинг после обновления подписки» pings that subscription right after it
  updates (not in background refresh, and not when «Пинг при открытии» is
  about to ping everything anyway).
