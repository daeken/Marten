// marten-tsnet serves one HTTPS origin on a private Tailscale node.
package main

import (
	"context"
	"crypto/tls"
	"errors"
	"io"
	"log"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"golang.org/x/sys/unix"
	"tailscale.com/feature/buildfeatures"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/tsnet"
)

const (
	badConfig = 10 + iota
	badState
	badKey
	notConnected
	wrongName
	noHTTPS
	noCertificate
	listenFailed
)

var hostname = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.ts\.net$`)
var enrollmentKey = regexp.MustCompile(`^tskey-auth-[a-zA-Z0-9_-]+$`)

func main() {
	// The parent holds the only writer. Exit even during a stuck enrollment or
	// if Marten is killed before running its shutdown hooks.
	go func() { _, _ = io.Copy(io.Discard, os.Stdin); os.Exit(0) }()
	log.SetOutput(io.Discard)
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	// Reject builds that leave SDK log upload enabled. Never use interactive
	// login or a credential inherited from a Tailscale install/environment.
	if len(args) != 4 || buildfeatures.HasLogTail || !hostname.MatchString(args[0]) ||
		!filepath.IsAbs(args[2]) || !filepath.IsAbs(args[3]) {
		return badConfig
	}
	for _, v := range os.Environ() {
		name, _, _ := strings.Cut(v, "=")
		if strings.HasPrefix(name, "TS_") || strings.HasPrefix(name, "TSNET_") {
			_ = os.Unsetenv(name)
		}
	}
	port, err := strconv.Atoi(args[1])
	if err != nil || port < 1 || port > 65535 {
		return badConfig
	}
	host, dir, keyPath := args[0], args[2], args[3]
	lock, err := lockState(dir)
	if err != nil {
		return badState
	}
	defer lock.Close()
	key, err := readKey(keyPath)
	if errors.Is(err, os.ErrNotExist) {
		// Missing key is expected after enrollment, not on first use.
		if _, stateErr := os.Stat(filepath.Join(dir, "tailscaled.state")); stateErr != nil {
			return badKey
		}
	} else if err != nil {
		return badKey
	}

	label, _, _ := strings.Cut(host, ".")
	discard := func(string, ...any) {}
	node := &tsnet.Server{Dir: dir, Hostname: label, AuthKey: key, Logf: discard, UserLogf: discard}
	defer node.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	status, err := node.Up(ctx)
	if err != nil {
		return notConnected
	}
	if code := checkStatus(status, host); code != 0 {
		return code
	}
	lc, err := node.LocalClient()
	if err != nil {
		return notConnected
	}
	loadCert := func(ctx context.Context) (*tls.Certificate, error) {
		cert, key, err := lc.CertPair(ctx, host)
		if err != nil {
			return nil, err
		}
		pair, err := tls.X509KeyPair(cert, key)
		if err == nil {
			err = pair.Leaf.VerifyHostname(host)
		}
		return &pair, err
	}
	// Preflight issuance before advertising readiness; subsequent handshakes
	// consult tsnet so it can renew the certificate without a Marten restart.
	certCtx, done := context.WithTimeout(context.Background(), time.Minute)
	_, err = loadCert(certCtx)
	done()
	if err != nil {
		return noCertificate
	}
	ln, err := node.Listen("tcp", ":443") // tailnet only, no Serve or Funnel registration
	if err != nil {
		return listenFailed
	}
	defer ln.Close()
	srv := &http.Server{
		TLSConfig:         exactTLS(host, loadCert),
		ReadHeaderTimeout: 15 * time.Second,
		IdleTimeout:       90 * time.Second,
		Handler:           tailnetProxy(host, &url.URL{Scheme: "http", Host: "127.0.0.1:" + strconv.Itoa(port)}),
		ErrorLog:          log.New(io.Discard, "", 0),
	}
	_, _ = io.WriteString(os.Stdout, "READY\n")
	_ = srv.ServeTLS(ln, "", "")
	return listenFailed
}

func checkStatus(s *ipnstate.Status, host string) int {
	if s == nil || s.Self == nil || strings.TrimSuffix(s.Self.DNSName, ".") != host {
		return wrongName
	}
	if s.CurrentTailnet == nil || !s.CurrentTailnet.MagicDNSEnabled || !slices.Contains(s.CertDomains, host) {
		return noHTTPS
	}
	return 0
}

func exactTLS(host string, getCert func(context.Context) (*tls.Certificate, error)) *tls.Config {
	return &tls.Config{MinVersion: tls.VersionTLS12, GetCertificate: func(hello *tls.ClientHelloInfo) (*tls.Certificate, error) {
		if hello.ServerName != host {
			return nil, errors.New("unexpected TLS server name")
		}
		return getCert(hello.Context())
	}}
}

func tailnetProxy(host string, upstream *url.URL) http.Handler {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = nil // never send Marten credentials through an environment proxy
	proxy := &httputil.ReverseProxy{
		Transport: transport,
		Rewrite: func(r *httputil.ProxyRequest) {
			r.SetURL(upstream)
			r.Out.Host = host
			for h := range r.Out.Header {
				name := strings.ToLower(h)
				if strings.HasPrefix(name, "x-forwarded-") || strings.HasPrefix(name, "tailscale-") || name == "via" {
					r.Out.Header.Del(h)
				}
			}
			// ReverseProxy removes Forwarded and handles HTTP Upgrade. No tailnet
			// peer identity substitutes for Marten's existing authentication.
		},
		ErrorLog: log.New(io.Discard, "", 0),
	}
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != host || r.Method == http.MethodConnect || r.URL.IsAbs() {
			w.WriteHeader(http.StatusForbidden)
			return
		}
		if origins := r.Header.Values("Origin"); len(origins) != 0 && (len(origins) != 1 || origins[0] != "https://"+host) {
			w.WriteHeader(http.StatusForbidden)
			return
		}
		// Defense in depth only: the upstream selects the route and owns the
		// authoritative endpoint/credential policy (including denials within /api/).
		if !strings.HasPrefix(r.URL.Path, "/api/") {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		proxy.ServeHTTP(w, r)
	})
}

func openPrivate(path string, flags int) (*os.File, error) {
	fd, err := unix.Open(path, flags|unix.O_NOFOLLOW|unix.O_CLOEXEC|unix.O_NONBLOCK, 0600)
	if err != nil {
		return nil, err
	}
	f := os.NewFile(uintptr(fd), path)
	var stat unix.Stat_t
	if unix.Fstat(fd, &stat) != nil || stat.Uid != uint32(os.Geteuid()) || stat.Mode&077 != 0 || stat.Mode&unix.S_IFMT != unix.S_IFREG {
		f.Close()
		return nil, errors.New("expected a private regular file owned by the service user")
	}
	return f, nil
}

func readKey(path string) (string, error) {
	f, err := openPrivate(path, unix.O_RDONLY)
	if err != nil {
		return "", err
	}
	defer f.Close()
	b, err := io.ReadAll(io.LimitReader(f, 4097))
	key := strings.TrimSpace(string(b))
	if err != nil || len(b) > 4096 || !enrollmentKey.MatchString(key) {
		return "", errors.New("expected a Tailscale device auth key")
	}
	return key, nil
}

func lockState(dir string) (*os.File, error) {
	if err := os.Mkdir(dir, 0700); err != nil && !errors.Is(err, os.ErrExist) {
		return nil, err
	}
	var stat unix.Stat_t
	if unix.Lstat(dir, &stat) != nil || stat.Uid != uint32(os.Geteuid()) || stat.Mode&0777 != 0700 || stat.Mode&unix.S_IFMT != unix.S_IFDIR {
		return nil, errors.New("expected a private state directory owned by the service user")
	}
	lock, err := openPrivate(filepath.Join(dir, "lock"), unix.O_CREAT|unix.O_RDWR)
	if err != nil {
		return nil, err
	}
	if err := unix.Flock(int(lock.Fd()), unix.LOCK_EX|unix.LOCK_NB); err != nil {
		lock.Close()
		return nil, err
	}
	return lock, nil
}
