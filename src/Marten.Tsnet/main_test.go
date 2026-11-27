package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
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
	if code := run([]string{testHost, "7450", state, filepath.Join(dir, "missing-key")}, ""); code != want {
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

// Opt-in cross-language fixture: the .NET test starts an isolated real Kestrel
// server and passes synthetic credentials over a private child stdin pipe.
func TestMacFilesProxyToKestrel(t *testing.T) {
	if os.Getenv("MARTEN_TEST_MAC_FILES_KESTREL") != "1" {
		t.Skip("requires scratch Kestrel launcher")
	}
	var fixture struct{ URL, Bearer, Ingress string }
	if err := json.NewDecoder(os.Stdin).Decode(&fixture); err != nil {
		t.Fatal("invalid fixture input")
	}
	up, err := url.Parse(fixture.URL)
	if err != nil || up.Scheme != "http" || up.Hostname() != "127.0.0.1" || len(fixture.Ingress) != 64 {
		t.Fatal("invalid fixture backend")
	}
	const host = "example.com" // covered by httptest's synthetic certificate
	certFixture := httptest.NewTLSServer(http.NotFoundHandler())
	defer certFixture.Close()
	cert := &certFixture.TLS.Certificates[0]
	start := func(proof string) *httptest.Server {
		srv := httptest.NewUnstartedServer(tailnetProxyWithProof(host, up, proof))
		srv.TLS = exactTLS(host, func(context.Context) (*tls.Certificate, error) { return cert, nil })
		// StartTLS supplies its own certificate when Certificates is empty. Keep
		// strict SNI via GetConfigForClient; the returned config uses exactTLS.
		strict := srv.TLS
		srv.TLS = &tls.Config{GetConfigForClient: func(*tls.ClientHelloInfo) (*tls.Config, error) { return strict, nil }}
		srv.StartTLS()
		t.Cleanup(srv.Close)
		return srv
	}
	front := start(fixture.Ingress)
	disabledProof := start("")
	client := func(sni string) *http.Client {
		tr := certFixture.Client().Transport.(*http.Transport).Clone()
		tr.TLSClientConfig.ServerName = sni
		tr.Proxy = nil
		t.Cleanup(tr.CloseIdleConnections)
		return &http.Client{Transport: tr, Timeout: 4 * time.Second}
	}
	native := client(host)
	get := func(c *http.Client, address, path, authority, token, proof string, want int) (http.Header, []byte) {
		t.Helper()
		r, err := http.NewRequest(http.MethodGet, address+"/api/"+path, nil)
		if err != nil {
			t.Fatal("invalid fixture request")
		}
		r.Host = authority
		if token != "" {
			r.Header.Set("Authorization", "Bearer "+token)
		}
		if proof != "" {
			r.Header.Set(nativeProofHeader, proof)
		}
		r.Header.Set("X-Forwarded-Proto", "https")
		r.Header.Set("Forwarded", "proto=https;host="+host)
		res, err := c.Do(r)
		if want == 0 && err != nil {
			return nil, nil
		}
		if err != nil || res.StatusCode != want {
			if res != nil {
				res.Body.Close()
				t.Fatalf("fixture returned %d; want %d", res.StatusCode, want)
			}
			t.Fatal("fixture request failed")
		}
		defer res.Body.Close()
		body, err := io.ReadAll(res.Body)
		if err != nil {
			t.Fatal("failed reading fixture response")
		}
		return res.Header, body
	}
	for _, c := range []struct{ address, path, proof string }{
		{fixture.URL, "status", ""}, {disabledProof.URL, "status", fixture.Ingress},
		{front.URL, "status?", ""}, {front.URL, "%73tatus", ""},
	} {
		_, body := get(native, c.address, c.path, host, fixture.Bearer, c.proof, 200)
		if strings.Contains(string(body), "macFiles") {
			t.Fatal("status advertised Files outside canonical proved ingress")
		}
	}
	get(native, front.URL, "status", host, "", "", 401)
	_, statusBody := get(native, front.URL, "status", host, fixture.Bearer, "", 200)
	var status struct{ MacFiles bool }
	if json.Unmarshal(statusBody, &status) != nil || !status.MacFiles {
		t.Fatal("paired canonical status did not advertise Files")
	}
	const roots = "mac-files/roots"
	get(native, fixture.URL, roots, host, fixture.Bearer, "", 403)
	get(native, fixture.URL, roots, host, fixture.Bearer, strings.Repeat("0", 64), 403)
	get(native, disabledProof.URL, roots, host, fixture.Bearer, fixture.Ingress, 403)
	get(native, front.URL, "proof-control", host, fixture.Bearer, fixture.Ingress, 204)
	get(native, front.URL, roots, "wrong.example", fixture.Bearer, "", 403)
	get(client("wrong.example"), front.URL, roots, host, fixture.Bearer, "", 0)
	get(native, front.URL, roots, host, "", "", 401)
	_, body := get(native, front.URL, roots, host, fixture.Bearer, strings.Repeat("0", 64), 200)
	var root []struct{ ID string }
	if json.Unmarshal(body, &root) != nil || len(root) != 1 || root[0].ID == "" {
		t.Fatal("unexpected fixture roots")
	}
	_, body = get(native, front.URL, "mac-files/entries/"+root[0].ID, host, fixture.Bearer, "", 200)
	var page struct{ Entries []struct{ ID, Name string } }
	if json.Unmarshal(body, &page) != nil || len(page.Entries) != 1 || page.Entries[0].Name != ".fixture" {
		t.Fatal("unexpected fixture entries")
	}
	h, body := get(native, front.URL, "mac-files/bytes/"+page.Entries[0].ID+"/share", host, fixture.Bearer, "", 200)
	if string(body) != "scratch bytes" || h.Get("Content-Length") != strconv.Itoa(len(body)) ||
		h.Get("Content-Type") != "application/octet-stream" || h.Get("Content-Disposition") != "attachment" || h.Get("Cache-Control") != "no-store" {
		t.Fatal("unexpected fixture download")
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
