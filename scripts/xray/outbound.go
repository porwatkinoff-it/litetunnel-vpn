// Package xray runs an Xray-core outbound inside sing-box, for the servers
// sing-box has no transport for: XHTTP, mKCP, VLESS Encryption, TCP with an
// HTTP header, Hysteria with finalmask, and anything else only Xray speaks.
//
// It is an ordinary sing-box outbound:
//
//	{"type": "xray", "tag": "proxy", "outbound": { <an Xray outbound> }}
//
// so groups, url tests, the ping core and the desktop core take it as they
// take any other. There is no second process and no local port: sing-box
// hands each connection to the Xray instance in memory, and Xray opens its
// own sockets through sing-box's dialer, which resolves names with sing-box's
// DNS and keeps the sockets out of the tunnel (interface binding on Apple,
// VpnService.protect on Android).
//
// The build scripts copy this file into sing-box as protocol/xray and
// register it (scripts/xray/apply.sh). It is LiteTunnel's own code, not part
// of sing-box or Xray.
package xray

import (
	"context"
	"encoding/json"
	"net"
	"os"
	"sync"
	"time"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/common/dialer"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	E "github.com/sagernet/sing/common/exceptions"
	"github.com/sagernet/sing/common/logger"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"

	xbuf "github.com/xtls/xray-core/common/buf"
	xlog "github.com/xtls/xray-core/common/log"
	xnet "github.com/xtls/xray-core/common/net"
	xudp "github.com/xtls/xray-core/common/protocol/udp"
	xcore "github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/features/routing"
	"github.com/xtls/xray-core/infra/conf"
	"github.com/xtls/xray-core/transport/internet"
	"github.com/xtls/xray-core/transport/internet/udp"

	// What a client needs, and nothing a server or the xray command does.
	_ "github.com/xtls/xray-core/app/dispatcher"
	_ "github.com/xtls/xray-core/app/log"
	_ "github.com/xtls/xray-core/app/policy"
	_ "github.com/xtls/xray-core/app/proxyman/inbound"
	_ "github.com/xtls/xray-core/app/proxyman/outbound"
	_ "github.com/xtls/xray-core/proxy/http"
	_ "github.com/xtls/xray-core/proxy/hysteria"
	_ "github.com/xtls/xray-core/proxy/shadowsocks"
	_ "github.com/xtls/xray-core/proxy/socks"
	_ "github.com/xtls/xray-core/proxy/trojan"
	_ "github.com/xtls/xray-core/proxy/vless/outbound"
	_ "github.com/xtls/xray-core/proxy/vmess/outbound"
	_ "github.com/xtls/xray-core/transport/internet/grpc"
	_ "github.com/xtls/xray-core/transport/internet/headers/http"
	_ "github.com/xtls/xray-core/transport/internet/headers/noop"
	_ "github.com/xtls/xray-core/transport/internet/httpupgrade"
	_ "github.com/xtls/xray-core/transport/internet/hysteria"
	_ "github.com/xtls/xray-core/transport/internet/kcp"
	_ "github.com/xtls/xray-core/transport/internet/reality"
	_ "github.com/xtls/xray-core/transport/internet/splithttp"
	_ "github.com/xtls/xray-core/transport/internet/tagged/taggedimpl"
	_ "github.com/xtls/xray-core/transport/internet/tcp"
	_ "github.com/xtls/xray-core/transport/internet/tls"
	_ "github.com/xtls/xray-core/transport/internet/udp"
	_ "github.com/xtls/xray-core/transport/internet/websocket"
)

const TypeXray = "xray"

type OutboundOptions struct {
	option.DialerOptions
	// One Xray outbound, as in the "outbounds" list of an Xray config.
	Outbound json.RawMessage `json:"outbound"`
}

func RegisterOutbound(registry *outbound.Registry) {
	outbound.Register[OutboundOptions](registry, TypeXray, NewOutbound)
}

var (
	_ adapter.Outbound  = (*Outbound)(nil)
	_ adapter.Lifecycle = (*Outbound)(nil)
)

type Outbound struct {
	outbound.Adapter
	logger logger.ContextLogger
	dialer N.Dialer
	config *xcore.Config

	access     sync.RWMutex
	instance   *xcore.Instance
	dispatcher routing.Dispatcher
}

func NewOutbound(ctx context.Context, router adapter.Router, logger log.ContextLogger, tag string, options OutboundOptions) (adapter.Outbound, error) {
	if len(options.Outbound) == 0 {
		return nil, E.New("missing xray outbound")
	}
	xrayLogs.use(logger)
	config, err := buildConfig(options.Outbound)
	if err != nil {
		return nil, E.Cause(err, "xray outbound")
	}
	outboundDialer, err := dialer.New(ctx, options.DialerOptions, true)
	if err != nil {
		return nil, err
	}
	useSingBoxDialer.Do(func() { internet.UseAlternativeSystemDialer(systemDialer{}) })
	return &Outbound{
		Adapter: outbound.NewAdapterWithDialerOptions(TypeXray, tag, []string{N.NetworkTCP, N.NetworkUDP}, options.DialerOptions),
		logger:  logger,
		dialer:  outboundDialer,
		config:  config,
	}, nil
}

// buildConfig turns the outbound into a whole Xray config with no inbounds,
// no routing and no logs. What would make Xray open sockets or resolve names
// on its own is dropped: sing-box does both.
func buildConfig(raw json.RawMessage) (*xcore.Config, error) {
	var detour conf.OutboundDetourConfig
	if err := json.Unmarshal(raw, &detour); err != nil {
		return nil, err
	}
	detour.Tag = "proxy"
	detour.SendThrough = nil
	detour.ProxySettings = nil
	detour.TargetStrategy = ""
	if detour.StreamSetting != nil {
		detour.StreamSetting.SocketSettings = nil
		// Xray 26 refuses to build a config with allowInsecure, which older
		// panels still write. Without it the certificate is checked as usual,
		// and a server whose certificate is valid works as before.
		if tls := detour.StreamSetting.TLSSettings; tls != nil {
			tls.AllowInsecure = false
		}
	}
	full := conf.Config{
		LogConfig:       &conf.LogConfig{AccessLog: "none", ErrorLog: "none", LogLevel: "none"},
		OutboundConfigs: []conf.OutboundDetourConfig{detour},
	}
	return full.Build()
}

func (h *Outbound) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateStart {
		return nil
	}
	instance, err := xcore.New(h.config)
	// Its log app has just made itself the process's logger; take it back.
	xrayLogs.use(h.logger)
	if err != nil {
		return E.Cause(err, "create xray")
	}
	if err = instance.Start(); err != nil {
		instance.Close()
		return E.Cause(err, "start xray")
	}
	dispatcher, _ := instance.GetFeature(routing.DispatcherType()).(routing.Dispatcher)
	if dispatcher == nil {
		instance.Close()
		return E.New("xray has no dispatcher")
	}
	h.access.Lock()
	h.instance = instance
	h.dispatcher = dispatcher
	h.access.Unlock()
	live.add(h)
	scope.Add(h.close)
	return nil
}

func (h *Outbound) close() error {
	live.remove(h)
	h.access.Lock()
	instance := h.instance
	h.instance = nil
	h.dispatcher = nil
	h.access.Unlock()
	if instance == nil {
		return nil
	}
	return instance.Close()
}

func (h *Outbound) running() (*xcore.Instance, routing.Dispatcher, error) {
	h.access.RLock()
	defer h.access.RUnlock()
	if h.instance == nil {
		return nil, nil, os.ErrClosed
	}
	return h.instance, h.dispatcher, nil
}

// xrayContext keeps the connection's values, but not its cancel: sing-box
// may dial with a short timeout context and use the connection after, and
// Xray would tear the connection down with it. Closing the returned
// connection ends Xray's side.
func (h *Outbound) xrayContext(ctx context.Context) context.Context {
	return context.WithValue(context.WithoutCancel(ctx), dialerKey{}, h.dialer)
}

func (h *Outbound) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	instance, _, err := h.running()
	if err != nil {
		return nil, err
	}
	var target xnet.Destination
	switch N.NetworkName(network) {
	case N.NetworkTCP:
		h.logger.InfoContext(ctx, "outbound connection to ", destination)
		target = xnet.TCPDestination(xrayAddress(destination), xnet.Port(destination.Port))
	case N.NetworkUDP:
		h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
		target = xnet.UDPDestination(xrayAddress(destination), xnet.Port(destination.Port))
	default:
		return nil, E.Extend(N.ErrUnknownNetwork, network)
	}
	return xcore.Dial(h.xrayContext(ctx), instance, target)
}

func (h *Outbound) ListenPacket(ctx context.Context, destination M.Socksaddr) (net.PacketConn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	_, dispatcher, err := h.running()
	if err != nil {
		return nil, err
	}
	h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
	return newPacketConn(h.xrayContext(ctx), dispatcher), nil
}

func xrayAddress(destination M.Socksaddr) xnet.Address {
	destination = destination.Unwrap()
	if destination.IsFqdn() {
		return xnet.DomainAddress(destination.Fqdn)
	}
	return xnet.IPAddress(destination.Addr.AsSlice())
}

func socksAddress(destination xnet.Destination) M.Socksaddr {
	port := uint16(destination.Port)
	if destination.Address.Family().IsDomain() {
		return M.ParseSocksaddrHostPort(destination.Address.Domain(), port)
	}
	return M.SocksaddrFromNet(&net.UDPAddr{IP: destination.Address.IP(), Port: int(port)}).Unwrap()
}

// MARK: Xray's log

// Xray logs through one handler for the whole process. This one passes its
// errors and warnings to sing-box's log, and its Severity keeps Xray from
// even formatting the rest: info and debug lines come with every packet.
var xrayLogs = &logSink{}

type logSink struct {
	access sync.Mutex
	logger logger.ContextLogger
}

func (s *logSink) use(l logger.ContextLogger) {
	s.access.Lock()
	s.logger = l
	s.access.Unlock()
	xlog.RegisterHandler(s)
}

func (s *logSink) Severity() xlog.Severity {
	return xlog.Severity_Warning
}

func (s *logSink) Handle(msg xlog.Message) {
	m, ok := msg.(*xlog.GeneralMessage)
	if !ok {
		return // access log lines
	}
	s.access.Lock()
	l := s.logger
	s.access.Unlock()
	if l == nil {
		return
	}
	if m.Severity == xlog.Severity_Error {
		l.Info("xray: ", m.Content)
	} else if m.Severity == xlog.Severity_Warning {
		l.Debug("xray: ", m.Content)
	}
}

// MARK: Xray's own sockets

type dialerKey struct{}

var useSingBoxDialer sync.Once

// The outbounds that are running, latest last. Xray drops the context's
// values on some paths (mux workers), and then any of them will do: their
// dialers are made from the same options.
var live liveOutbounds

type liveOutbounds struct {
	access sync.Mutex
	list   []*Outbound
}

func (l *liveOutbounds) add(h *Outbound) {
	l.access.Lock()
	l.list = append(l.list, h)
	l.access.Unlock()
}

func (l *liveOutbounds) remove(h *Outbound) {
	l.access.Lock()
	for i, it := range l.list {
		if it == h {
			l.list = append(l.list[:i], l.list[i+1:]...)
			break
		}
	}
	l.access.Unlock()
}

func (l *liveOutbounds) dialer() N.Dialer {
	l.access.Lock()
	defer l.access.Unlock()
	if len(l.list) == 0 {
		return nil
	}
	return l.list[len(l.list)-1].dialer
}

func dialerFrom(ctx context.Context) N.Dialer {
	if d, ok := ctx.Value(dialerKey{}).(N.Dialer); ok {
		return d
	}
	return live.dialer()
}

// systemDialer is how Xray opens a socket to its server.
type systemDialer struct{}

func (systemDialer) Dial(ctx context.Context, _ xnet.Address, destination xnet.Destination, _ *internet.SocketConfig) (net.Conn, error) {
	d := dialerFrom(ctx)
	if d == nil {
		return nil, E.New("xray: sing-box is not running")
	}
	target := socksAddress(destination)
	switch destination.Network {
	case xnet.Network_TCP:
		return d.DialContext(ctx, N.NetworkTCP, target)
	case xnet.Network_UDP:
		// A connected socket, named and resolved by sing-box. QUIC and mKCP
		// want a packet conn to their one server, which it serves as.
		conn, err := d.DialContext(ctx, N.NetworkUDP, target)
		if err != nil {
			return nil, err
		}
		remote := M.SocksaddrFromNet(conn.RemoteAddr()).Unwrap().UDPAddr()
		return &xnet.PacketConnWrapper{PacketConn: &connectedPacketConn{Conn: conn, remote: remote}, Dest: remote}, nil
	default:
		return nil, E.New("xray: unknown network ", destination.Network)
	}
}

func (systemDialer) DestIpAddress() net.IP {
	return nil
}

type connectedPacketConn struct {
	net.Conn
	remote *net.UDPAddr
}

func (c *connectedPacketConn) ReadFrom(p []byte) (int, net.Addr, error) {
	n, err := c.Conn.Read(p)
	return n, c.remote, err
}

func (c *connectedPacketConn) WriteTo(p []byte, _ net.Addr) (int, error) {
	return c.Conn.Write(p)
}

// MARK: UDP through Xray

// packetConn sends each packet to its own destination, as sing-box's UDP
// relay expects. Xray keeps one link for them and puts the destination on
// every packet.
type packetConn struct {
	ctx        context.Context
	dispatcher *udp.Dispatcher
	packets    chan *xudp.Packet
	done       chan struct{}
	closeOnce  sync.Once
	deadline   deadline
}

func newPacketConn(ctx context.Context, dispatcher routing.Dispatcher) *packetConn {
	c := &packetConn{
		ctx:      ctx,
		packets:  make(chan *xudp.Packet, 64),
		done:     make(chan struct{}),
		deadline: makeDeadline(),
	}
	c.dispatcher = udp.NewDispatcher(dispatcher, c.receive)
	return c
}

func (c *packetConn) receive(_ context.Context, packet *xudp.Packet) {
	select {
	case <-c.done:
		packet.Payload.Release()
	case c.packets <- packet:
	default:
		// A reader that falls behind loses packets, as on a real socket.
		packet.Payload.Release()
	}
}

func (c *packetConn) ReadFrom(p []byte) (int, net.Addr, error) {
	select {
	case packet := <-c.packets:
		n := copy(p, packet.Payload.Bytes())
		packet.Payload.Release()
		return n, socksAddress(packet.Source), nil
	case <-c.done:
		return 0, nil, net.ErrClosed
	case <-c.deadline.wait():
		return 0, nil, os.ErrDeadlineExceeded
	}
}

func (c *packetConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	select {
	case <-c.done:
		return 0, net.ErrClosed
	default:
	}
	destination := M.SocksaddrFromNet(addr)
	if !destination.IsValid() {
		return 0, E.New("xray: bad address ", addr)
	}
	target := xnet.UDPDestination(xrayAddress(destination), xnet.Port(destination.Port))
	var b *xbuf.Buffer
	if len(p) > xbuf.Size {
		b = xbuf.NewWithSize(int32(len(p)))
	} else {
		b = xbuf.New()
	}
	if _, err := b.Write(p); err != nil {
		b.Release()
		return 0, err
	}
	b.UDP = &target
	c.dispatcher.Dispatch(c.ctx, target, b)
	return len(p), nil
}

func (c *packetConn) Close() error {
	c.closeOnce.Do(func() {
		close(c.done)
		c.dispatcher.RemoveRay()
	})
	return nil
}

func (c *packetConn) LocalAddr() net.Addr {
	return &net.UDPAddr{IP: net.IPv4zero}
}

func (c *packetConn) SetDeadline(t time.Time) error {
	c.deadline.set(t)
	return nil
}

func (c *packetConn) SetReadDeadline(t time.Time) error {
	c.deadline.set(t)
	return nil
}

func (c *packetConn) SetWriteDeadline(time.Time) error {
	return nil
}

// deadline is a read deadline as in net.Pipe: a channel that closes when it
// passes.
type deadline struct {
	access sync.Mutex
	timer  *time.Timer
	passed chan struct{}
}

func makeDeadline() deadline {
	return deadline{passed: make(chan struct{})}
}

func (d *deadline) set(t time.Time) {
	d.access.Lock()
	defer d.access.Unlock()
	if d.timer != nil && !d.timer.Stop() {
		<-d.passed // the timer's function ran; wait for its close
	}
	d.timer = nil
	closed := isClosed(d.passed)
	if t.IsZero() {
		if closed {
			d.passed = make(chan struct{})
		}
		return
	}
	if wait := time.Until(t); wait > 0 {
		if closed {
			d.passed = make(chan struct{})
		}
		passed := d.passed
		d.timer = time.AfterFunc(wait, func() { close(passed) })
		return
	}
	if !closed {
		close(d.passed)
	}
}

func (d *deadline) wait() chan struct{} {
	d.access.Lock()
	defer d.access.Unlock()
	return d.passed
}

func isClosed(c chan struct{}) bool {
	select {
	case <-c:
		return true
	default:
		return false
	}
}
