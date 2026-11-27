package main

import (
	"context"
	"crypto/tls"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
	"tailscale.com/feature/buildfeatures"
	"tailscale.com/ipn/ipnstate"
)

const testHost = "marten.example.ts.net"

func TestRunRequiresPeerAPIServerAndLogUploadOmitted(t *testing.T) {
	dir := t.TempDir()
	state := filepath.Join(dir, "state")
	want := badKey // no credential or state, so never start a node
	if buildfeatures.HasLogTail || buildfeatures.HasPeerAPIServer {
		want = badConfig
	}
	if code := run([]string{testHost, "7450", state, filepath.Join(dir, "missing-key")}); code != want {
		t.Fatalf("startup returned %d; want %d", code, want)
	}
	if _, err := os.Stat(state); want == badConfig && !os.IsNotExist(err) {
		t.Fatalf("unsafe build checked the state directory before being rejected: %v", err)
	}
}

func TestPrivateStateAndDeviceKey(t *testing.T) {
	d := t.TempDir()
	state := filepath.Join(d, "state")
	f, err := lockState(state)
	if err != nil {
		t.Fatal(err)
	}
	if other, err := lockState(state); err == nil {
		other.Close()
		t.Fatal("second helper could acquire the state lock")
	}
	f.Close()
	if err := os.Chmod(state, 0755); err != nil {
		t.Fatal(err)
	}
	if other, err := lockState(state); err == nil {
		other.Close()
		t.Fatal("accepted insecure state directory")
	}
	key := filepath.Join(d, "key")
	for _, tc := range []struct {
		value string
		mode  os.FileMode
		ok    bool
	}{
		{"tskey-auth-fake_only-123\n", 0600, true},
		{"tskey-auth-fake_only-123", 0644, false},
		{"tskey-api-fake_only-123", 0600, false},
		{"tskey-client-fake_only-123", 0600, false},
	} {
		_ = os.Remove(key)
		if err := os.WriteFile(key, []byte(tc.value), tc.mode); err != nil {
			t.Fatal(err)
		}
		if err := os.Chmod(key, tc.mode); err != nil {
			t.Fatal(err)
		}
		if _, err := readKey(key); (err == nil) != tc.ok {
			t.Fatalf("key acceptance mismatch for mode %o, expected %v", tc.mode, tc.ok)
		}
	}
	link := filepath.Join(d, "link")
	if err := os.Symlink(key, link); err != nil {
		t.Fatal(err)
	}
	if _, err := readKey(link); err == nil {
		t.Fatal("accepted a symlink as a secret file")
	}
}

func TestRequireExactDeviceAndTLS(t *testing.T) {
	s := &ipnstate.Status{
		Self:           &ipnstate.PeerStatus{DNSName: testHost + "."},
		CurrentTailnet: &ipnstate.TailnetStatus{MagicDNSEnabled: true},
		CertDomains:    []string{testHost},
	}
	if got := checkStatus(s, testHost); got != 0 {
		t.Fatalf("expected usable identity, got %d", got)
	}
	s.Self.DNSName = "marten-1.example.ts.net."
	if got := checkStatus(s, testHost); got != wrongName {
		t.Fatalf("name mismatch wasn't rejected: %d", got)
	}
	s.Self.DNSName = testHost + "."
	s.CertDomains = nil
	if got := checkStatus(s, testHost); got != noHTTPS {
		t.Fatalf("missing HTTPS capability wasn't rejected: %d", got)
	}
	cfg := exactTLS(testHost, func(context.Context) (*tls.Certificate, error) { return &tls.Certificate{}, nil })
	for _, sni := range []string{"", "marten", "marten.other.ts.net", testHost + ".", strings.ToUpper(testHost), testHost} {
		if _, err := cfg.GetCertificate(&tls.ClientHelloInfo{ServerName: sni}); (err == nil) != (sni == testHost) {
			t.Fatalf("SNI %q: %v", sni, err)
		}
	}
}

func TestProxyKeepsRequestAuthAndDropsSpoofedIdentity(t *testing.T) {
	up := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Host != testHost || r.Header.Get("Origin") != "https://"+testHost || r.Header.Get("Authorization") != "Bearer fake" || r.URL.RequestURI() != "/api/agents/123/notebook.ipynb?keep=1" {
			t.Error("proxy dropped host, origin, auth, or target")
		}
		for _, key := range []string{"Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Server", "Tailscale-User-Login", "Via"} {
			if r.Header.Get(key) != "" {
				t.Errorf("forwarded spoofed header %s", key)
			}
		}
		w.Header().Set("Content-Disposition", `attachment; filename="note.ipynb"`)
		_, _ = io.WriteString(w, "notebook")
	}))
	defer up.Close()
	u, _ := url.Parse(up.URL)
	proxy := tailnetProxy(testHost, u)
	for _, origins := range [][]string{{"https://foreign.example"}, {"https://" + testHost, "https://" + testHost}} {
		r := httptest.NewRequest(http.MethodGet, "/api/status", nil)
		r.Host = testHost
		r.Header["Origin"] = origins
		w := httptest.NewRecorder()
		proxy.ServeHTTP(w, r)
		if w.Code != http.StatusForbidden {
			t.Fatalf("unexpected Origin accepted: %v", origins)
		}
	}
	for _, tc := range []struct {
		host, path string
		status     int
	}{
		{testHost, "/", 404},
		{testHost, "/sw.js", 404},
		{testHost, "/assets/index.js", 404},
		{"localhost:7450", "/api/status", 403},
		{testHost + ":443", "/api/status", 403},
		{testHost, "/api/agents/123/notebook.ipynb?keep=1", 200},
	} {
		r := httptest.NewRequest(http.MethodGet, tc.path, nil)
		r.Host = tc.host
		r.Header.Set("Authorization", "Bearer fake")
		r.Header.Set("Origin", "https://"+testHost)
		for _, key := range []string{"Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Server", "Tailscale-User-Login", "Via"} {
			r.Header.Set(key, "untrusted")
		}
		w := httptest.NewRecorder()
		proxy.ServeHTTP(w, r)
		if w.Code != tc.status {
			t.Fatalf("%s %s gave %d, want %d", tc.host, tc.path, w.Code, tc.status)
		}
		if w.Code == 200 && (w.Body.String() != "notebook" || w.Header().Get("Content-Disposition") == "") {
			t.Fatal("download metadata or bytes not forwarded")
		}
	}
}

func TestWebSocketUpgradePreservesAuthorization(t *testing.T) {
	up := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/events" || r.Host != testHost || r.Header.Get("Authorization") != "Bearer fake" {
			t.Error("wrong websocket request")
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		conn, err := websocket.Accept(w, r, nil)
		if err != nil {
			t.Error(err)
			return
		}
		defer conn.CloseNow()
		if err := conn.Write(r.Context(), websocket.MessageText, []byte("event")); err != nil {
			t.Error(err)
		}
	}))
	defer up.Close()
	u, _ := url.Parse(up.URL)
	proxy := httptest.NewServer(tailnetProxy(testHost, u))
	defer proxy.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, proxy.URL+"/api/events", &websocket.DialOptions{
		HTTPHeader: http.Header{"Authorization": {"Bearer fake"}},
		Host:       testHost,
	})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.CloseNow()
	_, message, err := conn.Read(ctx)
	if err != nil || string(message) != "event" {
		t.Fatalf("event %q, error %v", message, err)
	}
}
