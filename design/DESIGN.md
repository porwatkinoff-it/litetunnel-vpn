# LiteTunnel: one design for every platform

The name people see is **Link**: under the icon, in window and tray titles,
widgets, Control Center, the VPN profile and the Quick Settings tile.
LiteTunnel stays the project name: bundle IDs, packages, files, folders and
the User-Agent keep it, so installed copies update in place. On macOS the
file stays LiteTunnel.app and Link comes from en/ru.lproj/InfoPlist.strings:
Finder uses a localized name only when the plain one matches the file name.

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

One scrolling list, title **Link**. Toolbar: **Добавить** (add),
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
3. **One section per subscription**, in the user's order. The subscription a
   connection goes through moves to the top when the connection starts, or
   when the app comes up with one running, but only once per change of
   subscription: moved down by hand afterwards, it stays where it was put
   until another subscription is used. Selecting a server while connected
   does not move anything, so the list never jumps under the finger. Header:
   - name (headline, primary colour, one line) and the fold chevron; tapping
     either folds the section. Folded state is remembered per device.
   - right side, 14 apart: ping (`bolt.horizontal`), refresh
     (`arrow.clockwise`), more (`ellipsis.circle`). Ping and refresh turn
     into a small spinner while they run.
   - more menu: Настроить · Поддержка (if the subscription gives a link) ·
     Сайт провайдера (if given) · divider · Переместить выше (`arrow.up`) ·
     Переместить ниже (`arrow.down`), each hidden at its end, both hidden
     with their divider when there is only one subscription · divider · Удалить подписку (danger, asks
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
While connected through a group, the connect card shows «Сейчас: <server>»
under its name: the member the core runs on now.
Subscriptions never refresh when the app opens: by the refresh button, and on
iOS and macOS in the background. Android, Windows and Linux have no «Обновлять
каждые» row.
Every subscription with two or more servers gets one, unless a group from the
provider already holds all of them; a group that holds only some does not
count. The core picks the lowest ping. A new subscription starts on this row;
a server chosen earlier stays chosen. It has no link to copy and no QR code,
and lists its servers on the details screen.

### Ping badge

Colour by milliseconds: under 150 connected, under 400 busy, otherwise or
failed danger. Text: «87 мс», failed «нет связи». Not yet measured: nothing.

While a server is being checked, a small spinner (secondary) stands in place of
its badge, read «Проверяется» by screen readers; the old number is hidden, so
every number on screen is from the last finished check. A group row spins
while any of its servers is being checked. The details screen does the same on
its ping line.

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
   app runs as a proxy) · Локальная сеть мимо VPN · Переподключаться сам
   (on by default; iOS and macOS turn On Demand on after the app's own start
   and off before the user's stop, so a tunnel the system stopped comes back)
   · IPv6 · Фрагментация TLS.
4. **Энергосбережение**: Умный сон.
5. **Список серверов**: Сортировка (Как в подписке / По пингу / По имени).
   Groups stay on top when sorting by ping or name.
6. **Пинг**: Способ · Адрес проверки · Вид · Пинг при открытии · Пинг после
   обновления подписки. Footer explains the check, then the chosen way.
7. **Дополнительно**: Определять сайт (sniffing) · Сетевой стек · MTU ·
   Журнал · Открыть журнал ›.
8. **О приложении**: Версия · Ядро sing-box (GPLv3) link.

Footers are written as on iOS (`App/Shared/Views/SettingsView.swift`).

## Ping

A number means the server works. Every way checks each server through the
server itself, in two steps, and a server that fails either shows «нет связи»:

1. **Request** to the test address (GET, or HEAD for «Через сервер, HEAD»),
   timed up to the response headers; 5 s timeout. An address whose path ends
   in `generate_204` must answer exactly 204: a page with 200 there is a stub
   or a captive portal, not the internet. Any other address passes with
   200–399.
2. **Check download**: 64 KB of
   `https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb`
   (`Range: bytes=0-65535`, no compression). Status 200 or 206 and all 65 536
   bytes must arrive; 5 s without data or 8 s in all fails. This catches
   servers whose connections freeze after the first kilobytes, which a short
   request does not.

| «Способ» | shows for a server that passed |
| --- | --- |
| Реальный (default) | request time ÷ 3.3, rounded, at least 1 |
| Через сервер, GET | the full request time |
| Через сервер, HEAD | the full request time |
| TCP | TCP handshake to the server (QUIC for Hysteria2, TUIC, Xray Hysteria and XHTTP with TLS and ALPN h3); request time ÷ 3.3 where that cannot be measured (salamander obfs, finalmask, mKCP) |
| ICMP | one echo request; request time ÷ 3.3 when the server does not answer it |

- Test address: `http://www.gstatic.com/generate_204` by default; any http or
  https address the user enters; anything else falls back to the default. The
  field shows in every way.
- A group (Авто-выбор) shows its fastest member. A group whose servers are all
  rows of its subscription is measured through those rows, so no server is
  tested twice.
- The check runs in a short-lived second core with one local mixed inbound per
  server, routed inbound → that server, closed when done. Its sockets go
  around the running VPN. Its config is UTF-8 without a byte order mark.
- If that core does not start, check a config without servers first: if even
  that fails, show «Не удалось проверить серверы: …», clear the pending
  servers' numbers and stop. Otherwise check each server alone; the ones the
  core refuses get «нет связи», and the core starts again with the rest.
  There is no fallback to a plain TCP handshake: it shows a time for servers
  that pass no traffic.
- At most 8 at a time. Results appear as they come in; until then the row
  shows the spinner from «Ping badge».
- «Пинг после обновления подписки» pings that subscription right after it
  updates (not in background refresh, and not when «Пинг при открытии» is
  about to ping everything anyway).

## Xray servers

sing-box runs every server it supports. A server it does not is run through
Xray-core inside the same core, as an outbound
`{"type": "xray", "outbound": {<Xray outbound, no tag>}}` that routing,
groups and ping treat like any other.

- From a link: XHTTP / splithttp (with `extra`, `mode`, HTTP/3), mKCP (seed,
  header), VLESS Encryption, TCP with an HTTP header. From Xray JSON: every
  outbound sing-box refuses, and every one with `finalmask`.
- The row's detail says «… · Xray», e.g. «Reality · XHTTP · Xray».
- Skipped with a reason («Пропущено строк»), as Xray itself refuses them:
  - «VLESS без TLS и шифрования», «Trojan без TLS»: no TLS / Reality (and,
    for VLESS, no Encryption) to a host that is not local. Local is a
    private, loopback or link-local address, a name without a dot, or a name
    under `lan`, `local`, `localdomain`, `home.arpa`, `internal`, `test`,
    `example`, `invalid`, `localhost`.
  - «Reality с транспортом …»: Reality over anything but RAW, XHTTP, gRPC.
  - Hysteria 1 and protocols Xray does not know.
- `allowInsecure` is never passed. A server with a self-signed certificate
  works only with `pcs` (pinned certificate SHA-256) or `vcn` (name to check
  the certificate against).
- Costs: the core is about 15 MB larger on iOS, 10 MB in the APK and 14 MB
  on desktop. mKCP sends much more traffic than TCP and uses more battery;
  prefer another server in the same subscription.

## Auto-select

An auto-select group is a sing-box `urltest` with interval 5m and tolerance
150 ms: it leaves a working server only for one at least that much faster.
The core does not test while paused or asleep, so the app keeps the pick
working itself (`GroupWatch` in each core library):

- After a connect or a reload: a full group test after 3 s and after 20 s.
- Every 120 s, one test of the picked server alone (iOS and Android only
  while the screen is on; desktop always). The tick tests only when 105 s
  have passed since the last check or full test.
- Screen on or the app opened: the same check, if more than 60 s have passed.
- The pick did not answer: a full group test at once. While full tests leave
  it without an answer (no network), they repeat after 30 s, 60 s, 120 s…,
  at most every 600 s. An answer ends the back-off.

## Quick switches

- **iOS 18+, Control Center**: «Link» toggle (power symbol, connected
  colour when on) and «Открыть Link» button with the app logo as a
  monochrome symbol (Control Center takes symbols only, not the coloured
  icon; the Lock Screen rectangular widget uses the same logo). The symbol
  sits in both the widget's and the app's asset catalogs, since Control
  Center may look it up in either. It must be a variable template (v3.0:
  Ultralight-S, Regular-S, Black-S); Control Center draws nothing for a
  static Regular-M one. The toggle starts the
  saved VPN profile with no options; the tunnel runs what the app handed it
  last. With no profile yet it says «Откройте Link и подключитесь один
  раз…».
- **iOS widget**, small and Lock Screen (circular, rectangular, inline): the
  power circle in the status colour, «Подключено» / «Отключено» / …, the
  connection time, or the selected server while disconnected. The circle
  toggles on iOS 17+; the rest opens the app (`litetunnel://open`). No
  timeline refresh: the app reloads widgets and controls when the status
  changes.
- **Android Quick Settings tile**: «Link», power icon, active while
  connecting or connected, subtitle = server name or the status. Opens the
  app instead when the VPN permission or a server is missing. A long press
  opens the app. It listens to the tunnel only while the panel is open.
