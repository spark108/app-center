package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

func TestInstall(t *testing.T) {
	valid := packageBytes(t, map[string]string{
		clientExe: "client", updaterExe: "updater", "spark-release.txt": "1.0.1",
		"nested/file.txt": "installed",
	})
	badPath := packageBytes(t, map[string]string{"../escape.txt": "bad"})
	trailingDot := packageBytes(t, map[string]string{"nested./file.txt": "bad"})
	for _, tc := range []struct {
		name    string
		content []byte
		valid   bool
		badHash bool
	}{
		{"published package", valid, true, false},
		{"wrong checksum", valid, false, true},
		{"path outside destination", badPath, false, false},
		{"ambiguous Windows path", trailingDot, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path != "/download/app-center/package" {
					t.Errorf("unexpected download path %q", r.URL.Path)
					w.WriteHeader(404)
					return
				}
				checksum := fmt.Sprintf("%x", sha256.Sum256(tc.content))
				if tc.badHash {
					checksum = strings.Repeat("0", 64)
				}
				w.Header().Set("X-AppCenter-Version", "1.0.1")
				w.Header().Set("X-AppCenter-Size", strconv.Itoa(len(tc.content)))
				w.Header().Set("X-AppCenter-Sha256", checksum)
				w.Write(tc.content)
			}))
			defer server.Close()
			origin, err := parseOrigin(server.URL)
			if err != nil {
				t.Fatal(err)
			}
			root := t.TempDir()
			exe, err := install(root, origin, func(string, int64) error { return nil })
			if !tc.valid {
				if err == nil {
					t.Fatal("invalid package was accepted")
				}
				if _, err := os.Stat(filepath.Join(root, "client")); !os.IsNotExist(err) {
					t.Fatal("invalid package left an installation")
				}
				return
			}
			if err != nil || exe != filepath.Join(root, "client", clientExe) {
				t.Fatalf("installation failed, exe=%q, err=%v", exe, err)
			}
			settings, err := os.ReadFile(filepath.Join(root, "client.json"))
			if err != nil || string(settings) != `{"origin":"`+server.URL+`"}` {
				t.Fatalf("missing client configuration: %q, %v", settings, err)
			}
		})
	}
}

func packageBytes(t *testing.T, files map[string]string) []byte {
	t.Helper()
	var buffer bytes.Buffer
	archive := zip.NewWriter(&buffer)
	for name, contents := range files {
		writer, err := archive.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := writer.Write([]byte(contents)); err != nil {
			t.Fatal(err)
		}
	}
	if err := archive.Close(); err != nil {
		t.Fatal(err)
	}
	return buffer.Bytes()
}
