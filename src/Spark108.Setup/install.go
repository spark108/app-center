package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode"
)

const (
	clientExe   = "Spark108.Desktop.exe"
	updaterExe  = "Spark108.Updater.exe"
	maxArchive  = 512 << 20
	maxUnpacked = 2 << 30
)

var (
	versionPattern = regexp.MustCompile(`^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$`)
	errCanceled    = errors.New("установка отменена")
)

type reportProgress func(message string, percent int64) error

func parseOrigin(value string) (*url.URL, error) {
	origin, err := url.Parse(value)
	if err != nil || origin.Host == "" || origin.User != nil ||
		(origin.Path != "" && origin.Path != "/") || origin.RawQuery != "" || origin.Fragment != "" {
		return nil, errors.New("некорректный адрес App Center")
	}
	if origin.Scheme != "https" && !(origin.Scheme == "http" && origin.Hostname() == "127.0.0.1") {
		return nil, errors.New("для загрузки App Center требуется HTTPS")
	}
	return origin, nil
}

func install(root string, origin *url.URL, progress reportProgress) (string, error) {
	if err := os.MkdirAll(root, 0700); err != nil {
		return "", err
	}
	stage, err := os.MkdirTemp(root, ".install-")
	if err != nil {
		return "", err
	}
	defer os.RemoveAll(stage)

	archivePath := filepath.Join(stage, "client.zip")
	version, err := download(origin, archivePath, progress)
	if err != nil {
		return "", err
	}
	if err := progress("Распаковка App Center", 95); err != nil {
		return "", err
	}
	pending := filepath.Join(stage, "client")
	if err := unpack(archivePath, pending, progress); err != nil {
		return "", err
	}
	marker, err := os.ReadFile(filepath.Join(pending, "spark-release.txt"))
	if err != nil || len(marker) > 64 || strings.TrimSpace(string(marker)) != version {
		return "", errors.New("версия внутри пакета не совпадает с опубликованной")
	}
	for _, name := range []string{clientExe, updaterExe} {
		info, err := os.Stat(filepath.Join(pending, name))
		if err != nil || !info.Mode().IsRegular() || info.Size() == 0 {
			return "", errors.New("в пакете отсутствуют файлы App Center")
		}
	}
	if err := progress("Завершение установки", 99); err != nil {
		return "", err
	}
	settings, err := json.Marshal(map[string]string{"origin": origin.String()})
	if err != nil {
		return "", err
	}
	config := filepath.Join(stage, "client.json")
	if err := os.WriteFile(config, settings, 0600); err != nil {
		return "", err
	}
	if err := os.Rename(config, filepath.Join(root, "client.json")); err != nil {
		return "", err
	}
	destination := filepath.Join(root, "client")
	if err := os.Rename(pending, destination); err != nil {
		return "", err
	}
	return filepath.Join(destination, clientExe), nil
}

func download(origin *url.URL, destination string, progress reportProgress) (string, error) {
	if err := progress("Подключение к App Center", 0); err != nil {
		return "", err
	}
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: 15 * time.Second}).DialContext,
		TLSHandshakeTimeout:   15 * time.Second,
		ResponseHeaderTimeout: 30 * time.Second,
		DisableCompression:    true,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		Timeout:   20 * time.Minute,
		CheckRedirect: func(request *http.Request, previous []*http.Request) error {
			if len(previous) >= 3 || request.URL.Scheme != origin.Scheme || request.URL.Host != origin.Host || request.URL.User != nil {
				return errors.New("сервер перенаправил загрузку на другой адрес")
			}
			return nil
		},
	}
	response, err := client.Get(origin.ResolveReference(&url.URL{Path: "/download/app-center/package"}).String())
	if err != nil {
		return "", fmt.Errorf("не удалось подключиться к App Center: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", fmt.Errorf("сервер загрузки вернул HTTP %d", response.StatusCode)
	}
	version := response.Header.Get("X-AppCenter-Version")
	size, sizeErr := strconv.ParseInt(response.Header.Get("X-AppCenter-Size"), 10, 64)
	checksum, hashErr := hex.DecodeString(response.Header.Get("X-AppCenter-Sha256"))
	if !versionPattern.MatchString(version) || sizeErr != nil || size <= 0 || size > maxArchive ||
		hashErr != nil || len(checksum) != sha256.Size ||
		(response.ContentLength >= 0 && response.ContentLength != size) {
		return "", errors.New("сервер вернул некорректные данные пакета")
	}

	output, err := os.OpenFile(destination, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return "", err
	}
	defer output.Close()
	hash := sha256.New()
	writer := io.MultiWriter(output, hash)
	buffer := make([]byte, 128*1024)
	var total int64
	lastPercent := int64(-1)
	for {
		count, readErr := response.Body.Read(buffer)
		if count > 0 {
			total += int64(count)
			if total > size {
				return "", errors.New("размер загруженного пакета превышает заявленный")
			}
			if _, err := writer.Write(buffer[:count]); err != nil {
				return "", err
			}
			percent := total * 90 / size
			if percent != lastPercent {
				if err := progress("Скачивание App Center "+version, percent); err != nil {
					return "", err
				}
				lastPercent = percent
			}
		}
		if readErr == io.EOF {
			break
		}
		if readErr != nil {
			return "", fmt.Errorf("загрузка прервалась: %w", readErr)
		}
	}
	if total != size || !bytes.Equal(hash.Sum(nil), checksum) {
		return "", errors.New("контрольная сумма пакета не совпадает")
	}
	return version, output.Close()
}

func unpack(archivePath, destination string, progress reportProgress) error {
	archive, err := zip.OpenReader(archivePath)
	if err != nil {
		return err
	}
	defer archive.Close()
	if len(archive.File) == 0 || len(archive.File) > 20000 {
		return errors.New("недопустимое количество файлов в архиве")
	}
	seen := make(map[string]bool, len(archive.File))
	var total uint64
	for _, entry := range archive.File {
		if err := progress("Распаковка App Center", 95); err != nil {
			return err
		}
		name := strings.TrimSuffix(entry.Name, "/")
		local, err := filepath.Localize(name)
		key := strings.ToUpper(name)
		if err != nil || !fs.ValidPath(name) || name == "." || len(name) > 240 ||
			strings.ContainsAny(name, "\\:<>\"|?*") || strings.ContainsFunc(name, unicode.IsControl) ||
			strings.EqualFold(strings.Split(name, "/")[0], ".spark108-package.json") || seen[key] ||
			entry.UncompressedSize64 > maxUnpacked-total {
			return errors.New("недопустимое содержимое архива")
		}
		for _, part := range strings.Split(name, "/") {
			if strings.HasSuffix(part, ".") || strings.HasSuffix(part, " ") {
				return errors.New("недопустимое имя файла в архиве")
			}
		}
		seen[key] = true
		total += entry.UncompressedSize64
		path := filepath.Join(destination, local)
		if entry.FileInfo().IsDir() {
			if entry.UncompressedSize64 != 0 {
				return errors.New("недопустимая папка в архиве")
			}
			if err := os.MkdirAll(path, 0700); err != nil {
				return err
			}
			continue
		}
		if !entry.Mode().IsRegular() {
			return errors.New("недопустимый тип файла в архиве")
		}
		if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
			return err
		}
		if err := extractFile(entry, path); err != nil {
			return err
		}
	}
	return nil
}

func extractFile(entry *zip.File, path string) error {
	input, err := entry.Open()
	if err != nil {
		return err
	}
	defer input.Close()
	output, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	length := int64(entry.UncompressedSize64)
	written, err := io.Copy(output, io.LimitReader(input, length+1))
	closeErr := output.Close()
	if err != nil {
		return err
	}
	if written != length {
		return errors.New("размер файла в архиве не совпадает")
	}
	return closeErr
}
