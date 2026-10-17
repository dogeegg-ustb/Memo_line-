"""Local request/response endpoint through which other programs read the stashed CSP palettes.

``\\\\.\\pipe\\MemoLine.PanelStash`` by default, open to the current Windows user only and never to
remote clients. Byte pipe, UTF-8 without BOM: the client writes one JSON request per line and reads
one JSON response line for each, as many as it likes on one connection. The commands are described
in README.md; `handler(request) -> dict` implements them.
"""
from __future__ import annotations

import ctypes
import json
import threading
from ctypes import wintypes

KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
ADVAPI32 = ctypes.WinDLL("advapi32", use_last_error=True)

PIPE_ACCESS_DUPLEX, FILE_FLAG_FIRST_PIPE_INSTANCE = 0x3, 0x00080000
PIPE_TYPE_BYTE, PIPE_READMODE_BYTE, PIPE_WAIT, PIPE_REJECT_REMOTE_CLIENTS = 0x0, 0x0, 0x0, 0x8
PIPE_UNLIMITED_INSTANCES = 255
ERROR_PIPE_CONNECTED, ERROR_ACCESS_DENIED = 535, 5
TOKEN_QUERY, TOKEN_USER = 0x8, 1
INVALID_HANDLE = wintypes.HANDLE(-1).value
MAX_REQUEST = 64 * 1024
BUFFER = 1 << 16
SCHEMA_VERSION = 1


class SecurityAttributes(ctypes.Structure):
    _fields_ = [("nLength", wintypes.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p),
                ("bInheritHandle", wintypes.BOOL)]


KERNEL32.CreateNamedPipeW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.DWORD, wintypes.DWORD, wintypes.DWORD,
                                      ctypes.POINTER(SecurityAttributes)]
KERNEL32.CreateNamedPipeW.restype = wintypes.HANDLE
KERNEL32.ConnectNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
KERNEL32.DisconnectNamedPipe.argtypes = [wintypes.HANDLE]
KERNEL32.ReadFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                              ctypes.c_void_p]
KERNEL32.WriteFile.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                               ctypes.c_void_p]
KERNEL32.FlushFileBuffers.argtypes = [wintypes.HANDLE]
KERNEL32.CloseHandle.argtypes = [wintypes.HANDLE]
KERNEL32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                 wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
KERNEL32.CreateFileW.restype = wintypes.HANDLE
KERNEL32.GetCurrentProcess.restype = wintypes.HANDLE
KERNEL32.LocalFree.argtypes = [ctypes.c_void_p]
ADVAPI32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
ADVAPI32.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD,
                                         ctypes.POINTER(wintypes.DWORD)]
ADVAPI32.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
ADVAPI32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.ULONG)]


def pipe_path(name: str) -> str:
    return "\\\\.\\pipe\\" + name


def current_user_sid() -> str:
    token = wintypes.HANDLE()
    if not ADVAPI32.OpenProcessToken(KERNEL32.GetCurrentProcess(), TOKEN_QUERY, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        size = wintypes.DWORD()
        ADVAPI32.GetTokenInformation(token, TOKEN_USER, None, 0, ctypes.byref(size))
        buffer = ctypes.create_string_buffer(size.value)
        if not ADVAPI32.GetTokenInformation(token, TOKEN_USER, buffer, size, ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        sid = ctypes.c_void_p.from_buffer(buffer).value  # TOKEN_USER starts with SID_AND_ATTRIBUTES.Sid
        text = wintypes.LPWSTR()
        if not ADVAPI32.ConvertSidToStringSidW(sid, ctypes.byref(text)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            return text.value
        finally:
            KERNEL32.LocalFree(text)
    finally:
        KERNEL32.CloseHandle(token)


class AlreadyRunning(RuntimeError):
    pass


class PipeServer:
    def __init__(self, name: str, handler, log):
        self.name, self.path, self.handler, self.log = name, pipe_path(name), handler, log
        self.stopping = False
        descriptor = ctypes.c_void_p()
        # Protected DACL: full access for the current user only (same scope as .NET's CurrentUserOnly).
        sddl = f"D:P(A;;GA;;;{current_user_sid()})"
        if not ADVAPI32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(descriptor), None):
            raise ctypes.WinError(ctypes.get_last_error())
        self.security = SecurityAttributes(ctypes.sizeof(SecurityAttributes), descriptor, False)
        self.first = self.create(first=True)
        self.thread = threading.Thread(target=self.accept_loop, name="panel-api", daemon=True)

    def create(self, first=False) -> int:
        handle = KERNEL32.CreateNamedPipeW(
            self.path, PIPE_ACCESS_DUPLEX | (FILE_FLAG_FIRST_PIPE_INSTANCE if first else 0),
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            PIPE_UNLIMITED_INSTANCES, BUFFER, BUFFER, 0, ctypes.byref(self.security))
        if handle == INVALID_HANDLE or handle is None:
            error = ctypes.get_last_error()
            if first and error == ERROR_ACCESS_DENIED:
                raise AlreadyRunning(f"管道 {self.path} 已被另一个进程占用（程序已在运行？）")
            raise ctypes.WinError(error)
        return handle

    def start(self) -> None:
        self.thread.start()

    def accept_loop(self) -> None:
        handle = self.first
        while not self.stopping:
            if not KERNEL32.ConnectNamedPipe(handle, None) and ctypes.get_last_error() != ERROR_PIPE_CONNECTED:
                KERNEL32.CloseHandle(handle)
            elif self.stopping:
                KERNEL32.CloseHandle(handle)
                return
            else:
                threading.Thread(target=self.serve, args=(handle,), name="panel-api-client", daemon=True).start()
            try:
                handle = self.create()
            except OSError as ex:
                self.log(f"接口管道创建失败：{ex}")
                return

    def serve(self, handle: int) -> None:
        pending = b""
        chunk = ctypes.create_string_buffer(BUFFER)
        read = wintypes.DWORD()
        try:
            while not self.stopping:
                if not KERNEL32.ReadFile(handle, chunk, BUFFER, ctypes.byref(read), None) or not read.value:
                    return  # client closed the pipe
                pending += chunk.raw[:read.value]
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    if line.strip() and not self.write(handle, self.respond(line)):
                        return
                if len(pending) > MAX_REQUEST:
                    self.write(handle, self.error(None, f"请求超过 {MAX_REQUEST} 字节"))
                    return
        finally:
            KERNEL32.FlushFileBuffers(handle)
            KERNEL32.DisconnectNamedPipe(handle)
            KERNEL32.CloseHandle(handle)

    @staticmethod
    def error(request_id, message: str) -> dict:
        response = dict(success=False, error=message)
        if request_id is not None:
            response["requestId"] = request_id
        return response

    def respond(self, line: bytes) -> dict:
        try:
            request = json.loads(line.decode("utf-8-sig"))
        except ValueError as ex:
            return self.error(None, f"不是合法的 JSON：{ex}")
        if not isinstance(request, dict) or not isinstance(request.get("command"), str):
            return self.error(None, "请求必须是带 command 字段的 JSON 对象")
        request_id = request.get("requestId")
        try:
            response = dict(success=True, **self.handler(request))
        except (KeyError, ValueError, LookupError, RuntimeError, OSError, TimeoutError) as ex:
            response = self.error(request_id, str(ex) or type(ex).__name__)
        except Exception as ex:  # a bug must not take the endpoint down
            self.log(f"接口内部错误：{ex!r}")
            response = self.error(request_id, f"内部错误：{ex!r}")
        if request_id is not None:
            response["requestId"] = request_id
        return response

    def write(self, handle: int, response: dict) -> bool:
        data = (json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        view = memoryview(data)
        written = wintypes.DWORD()
        while view:
            if not KERNEL32.WriteFile(handle, bytes(view[:BUFFER]), min(len(view), BUFFER), ctypes.byref(written), None):
                return False
            view = view[written.value:]
        return True

    def stop(self) -> None:
        self.stopping = True
        # Wake the blocking ConnectNamedPipe with a throwaway connection.
        handle = KERNEL32.CreateFileW(self.path, 0xC0000000, 0, None, 3, 0, None)
        if handle not in (INVALID_HANDLE, None):
            KERNEL32.CloseHandle(handle)
        self.thread.join(timeout=1)
        KERNEL32.LocalFree(self.security.lpSecurityDescriptor)


class PipeClient:
    """Minimal client, used by the tests and `panel_host.py --query`."""

    def __init__(self, name: str, timeout_ms: int = 2000):
        import time
        deadline = time.monotonic() + timeout_ms / 1000
        while True:
            handle = KERNEL32.CreateFileW(pipe_path(name), 0xC0000000, 0, None, 3, 0, None)
            if handle not in (INVALID_HANDLE, None):
                break
            if time.monotonic() >= deadline:
                raise ConnectionError(f"连不上 {pipe_path(name)}（错误 {ctypes.get_last_error()}）")
            time.sleep(0.05)
        self.handle, self.pending = handle, b""

    def request(self, command: str, **fields) -> dict:
        data = (json.dumps(dict(command=command, **fields), ensure_ascii=False) + "\n").encode("utf-8")
        written = wintypes.DWORD()
        if not KERNEL32.WriteFile(self.handle, data, len(data), ctypes.byref(written), None):
            raise ctypes.WinError(ctypes.get_last_error())
        chunk = ctypes.create_string_buffer(BUFFER)
        read = wintypes.DWORD()
        while b"\n" not in self.pending:
            if not KERNEL32.ReadFile(self.handle, chunk, BUFFER, ctypes.byref(read), None) or not read.value:
                raise ConnectionError("接口关闭了连接")
            self.pending += chunk.raw[:read.value]
        line, self.pending = self.pending.split(b"\n", 1)
        return json.loads(line.decode("utf-8"))

    def close(self) -> None:
        KERNEL32.CloseHandle(self.handle)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
