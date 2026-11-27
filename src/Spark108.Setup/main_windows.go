package main

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"unsafe"
)

const setupTitle = "Установка Spark108 App Center"

var serviceOrigin = "https://app.godish.ru"

func main() {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()
	if err := run(); err != nil && !errors.Is(err, errCanceled) {
		messageBox("Не удалось установить App Center.\n\n"+err.Error(), 0x10)
		os.Exit(1)
	}
}

func run() error {
	origin, err := parseOrigin(serviceOrigin)
	if err != nil {
		return err
	}
	root := os.Getenv("SPARK108_APP_CENTER_HOME")
	if root == "" {
		data, err := os.UserCacheDir()
		if err != nil {
			return err
		}
		root = filepath.Join(data, "Spark108", "AppCenter")
	}
	root, err = filepath.Abs(root)
	if err != nil {
		return err
	}
	lock, err := setupLock(root)
	if err != nil {
		return err
	}
	defer syscall.CloseHandle(lock)

	executable := filepath.Join(root, "client", clientExe)
	if info, err := os.Stat(executable); err == nil && info.Mode().IsRegular() {
		return launch(executable)
	}
	for _, folder := range []string{"client", "client.previous"} {
		if _, err := os.Stat(filepath.Join(root, folder)); !os.IsNotExist(err) {
			return errors.New("обнаружена незавершённая установка App Center")
		}
	}
	if messageBox("Скачать и установить Spark108 App Center?", 0x21) != 1 {
		return errCanceled
	}

	comResult, _, _ := coInitialize.Call(0, 2)
	comReady := int32(comResult) >= 0
	if comReady {
		defer coUninitialize.Call()
	}
	progress := &progressDialog{}
	if comReady {
		progress.start()
	}
	executable, err = install(root, origin, progress.update)
	progress.close()
	if err != nil {
		return err
	}
	if comReady && os.Getenv("SPARK108_APP_CENTER_HOME") == "" {
		addStartMenuShortcut(executable)
	}
	return launch(executable)
}

func launch(executable string) error {
	command := exec.Command(executable)
	command.Dir = filepath.Dir(executable)
	if err := command.Start(); err != nil {
		return fmt.Errorf("App Center установлен, но запустить его не удалось: %w", err)
	}
	return command.Process.Release()
}

func setupLock(root string) (syscall.Handle, error) {
	scope := sha256.Sum256([]byte(strings.ToUpper(root)))
	name := winString(fmt.Sprintf(`Local\Spark108.AppCenter.Setup.%x`, scope[:8]))
	handle, _, err := createMutex.Call(0, 0, uintptr(unsafe.Pointer(name)))
	if handle == 0 {
		return 0, err
	}
	if err == syscall.ERROR_ALREADY_EXISTS {
		syscall.CloseHandle(syscall.Handle(handle))
		return 0, errors.New("установщик уже запущен")
	}
	return syscall.Handle(handle), nil
}

func dll(name string) *syscall.LazyDLL {
	return syscall.NewLazyDLL(filepath.Join(os.Getenv("SystemRoot"), "System32", name))
}

var (
	ole32          = dll("ole32.dll")
	coInitialize   = ole32.NewProc("CoInitializeEx")
	coUninitialize = ole32.NewProc("CoUninitialize")
	coCreate       = ole32.NewProc("CoCreateInstance")
	coFree         = ole32.NewProc("CoTaskMemFree")
	showMessage    = dll("user32.dll").NewProc("MessageBoxW")
	createMutex    = dll("kernel32.dll").NewProc("CreateMutexW")
	getKnownFolder = dll("shell32.dll").NewProc("SHGetKnownFolderPath")
)

func winString(value string) *uint16 {
	text, _ := syscall.UTF16PtrFromString(value)
	return text
}

func messageBox(message string, flags uintptr) uintptr {
	action, _, _ := showMessage.Call(0, uintptr(unsafe.Pointer(winString(message))), uintptr(unsafe.Pointer(winString(setupTitle))), flags)
	return action
}

type comInterface struct {
	vtable *[21]uintptr
}

func (object *comInterface) call(method int, arguments ...uintptr) uintptr {
	parameters := append([]uintptr{uintptr(unsafe.Pointer(object))}, arguments...)
	result, _, _ := syscall.SyscallN(object.vtable[method], parameters...)
	return result
}

func (object *comInterface) setString(method int, value string) uintptr {
	result, _, _ := syscall.SyscallN(object.vtable[method],
		uintptr(unsafe.Pointer(object)), uintptr(unsafe.Pointer(winString(value))))
	return result
}

func createObject(class, identifier *syscall.GUID) *comInterface {
	var object *comInterface
	result, _, _ := coCreate.Call(uintptr(unsafe.Pointer(class)), 0, 1,
		uintptr(unsafe.Pointer(identifier)), uintptr(unsafe.Pointer(&object)))
	if int32(result) < 0 {
		return nil
	}
	return object
}

var (
	progressClass = syscall.GUID{Data1: 0xf8383852, Data2: 0xfcd3, Data3: 0x11d1, Data4: [8]byte{0xa6, 0xb9, 0, 0x60, 0x97, 0xdf, 0x5b, 0xd4}}
	progressIID   = syscall.GUID{Data1: 0xebbc7c04, Data2: 0x315e, Data3: 0x11d2, Data4: [8]byte{0xb6, 0x2f, 0, 0x60, 0x97, 0xdf, 0x5b, 0xd4}}
	shellClass    = syscall.GUID{Data1: 0x00021401, Data4: [8]byte{0xc0, 0, 0, 0, 0, 0, 0, 0x46}}
	shellIID      = syscall.GUID{Data1: 0x000214f9, Data4: [8]byte{0xc0, 0, 0, 0, 0, 0, 0, 0x46}}
	persistIID    = syscall.GUID{Data1: 0x0000010b, Data4: [8]byte{0xc0, 0, 0, 0, 0, 0, 0, 0x46}}
	programsID    = syscall.GUID{Data1: 0xa77f5d77, Data2: 0x2e2b, Data3: 0x44c3, Data4: [8]byte{0xa6, 0xa2, 0xab, 0xa6, 0x01, 0x05, 0x4a, 0x51}}
)

type progressDialog struct {
	object  *comInterface
	message string
	percent int64
}

func (dialog *progressDialog) start() {
	dialog.object = createObject(&progressClass, &progressIID)
	if dialog.object == nil {
		return
	}
	dialog.object.setString(5, setupTitle)                              // SetTitle
	if result := dialog.object.call(3, 0, 0, 4, 0); int32(result) < 0 { // StartProgressDialog
		dialog.object.call(2)
		dialog.object = nil
	}
}

func (dialog *progressDialog) update(message string, percent int64) error {
	if dialog.object == nil {
		return nil
	}
	if dialog.object.call(7) != 0 { // HasUserCancelled
		return errCanceled
	}
	if message != dialog.message {
		syscall.SyscallN(dialog.object.vtable[10], uintptr(unsafe.Pointer(dialog.object)),
			1, uintptr(unsafe.Pointer(winString(message))), 0, 0) // SetLine
		dialog.message = message
	}
	if percent != dialog.percent {
		dialog.object.call(9, uintptr(percent), 100) // SetProgress64
		dialog.percent = percent
	}
	return nil
}

func (dialog *progressDialog) close() {
	if dialog.object != nil {
		dialog.object.call(4) // StopProgressDialog
		dialog.object.call(2) // Release
	}
}

func addStartMenuShortcut(executable string) {
	var programs *uint16
	result, _, _ := getKnownFolder.Call(uintptr(unsafe.Pointer(&programsID)), 0, 0, uintptr(unsafe.Pointer(&programs)))
	if int32(result) < 0 || programs == nil {
		return
	}
	defer coFree.Call(uintptr(unsafe.Pointer(programs)))
	folder := syscall.UTF16ToString(unsafe.Slice(programs, 32768))
	link := createObject(&shellClass, &shellIID)
	if link == nil {
		return
	}
	defer link.call(2)
	pathResult := link.setString(20, executable)              // SetPath
	workResult := link.setString(9, filepath.Dir(executable)) // SetWorkingDirectory
	if int32(pathResult) < 0 || int32(workResult) < 0 {
		return
	}
	var file *comInterface
	result, _, _ = syscall.SyscallN(link.vtable[0], uintptr(unsafe.Pointer(link)),
		uintptr(unsafe.Pointer(&persistIID)), uintptr(unsafe.Pointer(&file))) // QueryInterface
	if int32(result) < 0 || file == nil {
		return
	}
	defer file.call(2)
	syscall.SyscallN(file.vtable[6], uintptr(unsafe.Pointer(file)),
		uintptr(unsafe.Pointer(winString(filepath.Join(folder, "Spark108 App Center.lnk")))), 1) // Save
}
