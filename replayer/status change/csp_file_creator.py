"""Searchable debug front end for the local CSP command backend."""
import json
import ctypes
import socket
import subprocess
import sys
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, ttk
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent
BACKEND = ROOT / "csp_backend.py"
SETTINGS = Path.home() / "Documents" / "Codex" / "CSPBackend" / "csp_backend_settings.json"
UI_SETTINGS = Path.home() / "Documents" / "Codex" / "CSPBackend" / "csp_ui_settings.json"
NUMERIC_PARAMETERS_FILE = ROOT / "csp_numeric_parameters.json"
API = "http://127.0.0.1:38765/v1"
user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.AllowSetForegroundWindow.argtypes = [ctypes.c_ulong]
user32.AllowSetForegroundWindow.restype = ctypes.c_int


def read_token():
    try:
        return json.loads(SETTINGS.read_text(encoding="utf-8")).get("api_token", "")
    except (OSError, ValueError):
        return ""


def read_ui_settings():
    try:
        return json.loads(UI_SETTINGS.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def write_ui_settings(settings):
    UI_SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    UI_SETTINGS.write_text(json.dumps(settings, ensure_ascii=False, indent=2), encoding="utf-8")


def request_api(method, path, payload=None, timeout=3):
    headers = {"X-CSP-Token": read_token()}
    body = None
    if payload is not None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    request = Request(API + path, data=body, headers=headers, method=method)
    try:
        with urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except HTTPError as exc:
        try:
            detail = json.loads(exc.read().decode("utf-8"))
            raise RuntimeError(detail.get("error", str(exc))) from exc
        except ValueError:
            raise RuntimeError(str(exc)) from exc
    except URLError as exc:
        if isinstance(exc.reason, (TimeoutError, socket.timeout)):
            raise RuntimeError("后台命令等待超时；本次命令可能仍在执行，请勿连续重复点击。稍后检查 CSP 状态。") from exc
        raise RuntimeError("后端未连接；请重新打开前端工具。") from exc
    except (TimeoutError, socket.timeout) as exc:
        raise RuntimeError("后台命令等待超时；本次命令可能仍在执行，请勿连续重复点击。稍后检查 CSP 状态。") from exc


def ensure_backend():
    try:
        health = request_api("GET", "/health")
    except Exception:
        health = {}
    if health.get("version") == "1.8":
        if not health.get("desktop_accessible"):
            raise RuntimeError("后台运行在无法访问 CSP 的隔离桌面。请关闭旧后台后，从你的 Windows 桌面快捷方式启动本工具。")
        return
    flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    subprocess.Popen(
        [sys.executable, str(BACKEND)],
        cwd=str(ROOT),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=flags,
        close_fds=True,
    )
    for _ in range(40):
        time.sleep(0.15)
        try:
            health = request_api("GET", "/health")
        except Exception:
            continue
        if health.get("version") == "1.8":
            if not health.get("desktop_accessible"):
                raise RuntimeError("后台无法访问 CSP 桌面；请从 Windows 桌面快捷方式启动本工具。")
            return
    raise RuntimeError("无法启动 CSP 后端服务。请关闭已运行的旧版后台服务后再打开前端。")


class CspFrontend:
    def __init__(self, root):
        self.root = root
        root.title("CSP 控制台（调试前端）")
        root.geometry("1120x780")
        root.minsize(880, 640)
        root.withdraw()

        self.commands = []
        self.command_by_id = {}
        self.search = tk.StringVar()
        self.category = tk.StringVar(value="全部")
        self.shortcut = tk.StringVar()
        self.directory = tk.StringVar()
        self.base_name = tk.StringVar()
        self.folder_name = tk.StringVar()
        self.status = tk.StringVar(value="")
        self.topmost = tk.BooleanVar(value=bool(read_ui_settings().get("always_on_top", False)))
        self.numeric_parameters = json.loads(NUMERIC_PARAMETERS_FILE.read_text(encoding="utf-8-sig"))
        self.numeric_parameter_by_label = {
            f"{item['category']} · {item['name']} ({item['unit']})": item for item in self.numeric_parameters
        }
        self.numeric_parameter_label = tk.StringVar(value=next(iter(self.numeric_parameter_by_label), ""))
        self.numeric_value = tk.StringVar()
        self.numeric_note = tk.StringVar()

        root.grid_columnconfigure(0, weight=1)
        root.grid_rowconfigure(1, weight=1)

        header = ttk.Frame(root, padding=(12, 10, 12, 4))
        header.grid(row=0, column=0, sticky="ew")
        header.grid_columnconfigure(1, weight=1)
        ttk.Label(header, text="自动保存位置").grid(row=0, column=0, sticky="w", padx=(0, 8))
        ttk.Entry(header, textvariable=self.directory, state="readonly").grid(row=0, column=1, sticky="ew", padx=(0, 14))
        ttk.Label(header, text="文件名前缀").grid(row=0, column=2, sticky="w", padx=(0, 8))
        ttk.Entry(header, textvariable=self.base_name, state="readonly", width=16).grid(row=0, column=3, sticky="ew", padx=(0, 10))
        ttk.Checkbutton(header, text="窗口置顶", variable=self.topmost, command=self.toggle_topmost).grid(row=0, column=4, padx=(0, 10))
        ttk.Button(header, text="新建可打开的 CSP 文件", command=lambda: self.run_command("create_next_file")).grid(row=0, column=5, sticky="ew")

        notebook = ttk.Notebook(root)
        notebook.grid(row=1, column=0, sticky="nsew", padx=12, pady=8)
        command_tab = ttk.Frame(notebook, padding=10)
        brush_tab = ttk.Frame(notebook, padding=18)
        files_tab = ttk.Frame(notebook, padding=18)
        numeric_tab = ttk.Frame(notebook, padding=20)
        notebook.add(command_tab, text="功能目录")
        notebook.add(brush_tab, text="画笔控制")
        notebook.add(files_tab, text="文件操作")
        notebook.add(numeric_tab, text="数值设置")

        command_tab.grid_columnconfigure(1, weight=1)
        command_tab.grid_rowconfigure(1, weight=1)
        ttk.Label(command_tab, text="搜索").grid(row=0, column=0, sticky="w", padx=(0, 8), pady=(0, 8))
        search_entry = ttk.Entry(command_tab, textvariable=self.search)
        search_entry.grid(row=0, column=1, sticky="ew", pady=(0, 8))
        search_entry.bind("<KeyRelease>", lambda _event: self.refresh_list())
        ttk.Label(command_tab, text="分类").grid(row=0, column=2, sticky="w", padx=(14, 8), pady=(0, 8))
        self.category_box = ttk.Combobox(command_tab, textvariable=self.category, state="readonly", width=24)
        self.category_box.grid(row=0, column=3, sticky="ew", pady=(0, 8))
        self.category_box.bind("<<ComboboxSelected>>", lambda _event: self.refresh_list())

        list_frame = ttk.Frame(command_tab)
        list_frame.grid(row=1, column=0, columnspan=4, sticky="nsew")
        list_frame.grid_columnconfigure(0, weight=1)
        list_frame.grid_rowconfigure(0, weight=1)
        self.tree = ttk.Treeview(list_frame, columns=("category", "shortcut", "description"), show="tree headings", selectmode="browse")
        self.tree.heading("#0", text="功能")
        self.tree.heading("category", text="分类")
        self.tree.heading("shortcut", text="CSP 快捷键")
        self.tree.heading("description", text="功能说明")
        self.tree.column("#0", width=230, minwidth=150, stretch=False)
        self.tree.column("category", width=120, minwidth=90, stretch=False)
        self.tree.column("shortcut", width=140, minwidth=100, stretch=False)
        self.tree.column("description", width=560, minwidth=220, stretch=True)
        vertical = ttk.Scrollbar(list_frame, orient="vertical", command=self.tree.yview)
        horizontal = ttk.Scrollbar(list_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=vertical.set, xscrollcommand=horizontal.set)
        self.tree.grid(row=0, column=0, sticky="nsew")
        vertical.grid(row=0, column=1, sticky="ns")
        horizontal.grid(row=1, column=0, sticky="ew")
        self.tree.bind("<<TreeviewSelect>>", self.on_select)
        self.tree.bind("<Double-1>", lambda _event: self.execute_selected())

        shortcut_frame = ttk.LabelFrame(command_tab, text="所选功能快捷键", padding=(10, 8))
        shortcut_frame.grid(row=2, column=0, columnspan=4, sticky="ew", pady=(10, 0))
        shortcut_frame.grid_columnconfigure(1, weight=1)
        ttk.Label(shortcut_frame, text="快捷键").grid(row=0, column=0, sticky="w", padx=(0, 8))
        ttk.Entry(shortcut_frame, textvariable=self.shortcut).grid(row=0, column=1, sticky="ew", padx=(0, 10))
        ttk.Button(shortcut_frame, text="保存快捷键", command=self.save_selected_shortcut).grid(row=0, column=2, padx=(0, 8))
        ttk.Button(shortcut_frame, text="执行所选功能", command=self.execute_selected).grid(row=0, column=3)
        ttk.Label(shortcut_frame, text="无默认键的命令需先在 CSP 快捷键设置中绑定相同快捷键。", foreground="#666666").grid(row=1, column=1, columnspan=3, sticky="w", pady=(6, 0))

        for axis_index in range(2):
            brush_tab.grid_columnconfigure(axis_index, weight=1, uniform="brush")
            brush_tab.grid_rowconfigure(axis_index, weight=1, uniform="brush")
        brush_groups = [
            ("画笔尺寸", [("减小一个尺寸预设", "brush_size_smaller"), ("增大一个尺寸预设", "brush_size_larger")], "按 CSP 画笔尺寸预设逐档切换。"),
            ("工具不透明度", [("降低不透明度", "brush_opacity_decrease"), ("提高不透明度", "brush_opacity_increase")], "作用于当前工具；连续操作逐档调整。"),
            ("笔尖浓度", [("降低笔尖浓度", "brush_density_decrease"), ("提高笔尖浓度", "brush_density_increase")], "这是笔尖浓度，与工具不透明度分开。"),
            ("工具与画笔", [("上一个工具", "previous_tool"), ("下一个工具", "next_tool")], "切换工具；指定单支画笔请在功能目录搜索“选择指定画笔”，并填写其 CSP 快捷键。"),
        ]
        for index, (title, actions, hint) in enumerate(brush_groups):
            row, column = divmod(index, 2)
            group = ttk.LabelFrame(brush_tab, text=title, padding=14)
            group.grid(row=row, column=column, sticky="nsew", padx=8, pady=8)
            for button_column in range(2):
                group.grid_columnconfigure(button_column, weight=1, uniform=f"brush_group_{index}")
            for button_index, (label, command_id) in enumerate(actions):
                ttk.Button(group, text=label, command=lambda cid=command_id: self.run_command(cid)).grid(row=0, column=button_index, sticky="ew", padx=4, pady=(4, 10))
            ttk.Label(group, text=hint, wraplength=410, foreground="#555555").grid(row=1, column=0, columnspan=2, sticky="w", padx=4)

        files_tab.grid_columnconfigure(1, weight=1)
        ttk.Label(files_tab, text="新建文件夹").grid(row=0, column=0, sticky="w", padx=(0, 10), pady=(0, 12))
        ttk.Entry(files_tab, textvariable=self.folder_name).grid(row=0, column=1, sticky="ew", pady=(0, 12))
        ttk.Button(files_tab, text="创建文件夹", command=self.create_folder).grid(row=0, column=2, padx=(10, 0), pady=(0, 12))
        ttk.Label(files_tab, text="顶部按钮从 CSP 制作的空白画布模板复制出可打开的 .clip 文件。默认画布为 1600×1200、白底；可以在 CSP 中打开后继续绘制和保存。", wraplength=900, foreground="#666666").grid(row=1, column=0, columnspan=3, sticky="w", pady=(8, 0))

        numeric_tab.grid_columnconfigure(1, weight=1)
        ttk.Label(numeric_tab, text="参数").grid(row=0, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        numeric_box = ttk.Combobox(numeric_tab, textvariable=self.numeric_parameter_label, state="readonly", values=list(self.numeric_parameter_by_label), width=48)
        numeric_box.grid(row=0, column=1, sticky="ew", pady=(0, 12))
        numeric_box.bind("<<ComboboxSelected>>", self.update_numeric_note)
        ttk.Label(numeric_tab, text="目标数值").grid(row=1, column=0, sticky="w", padx=(0, 12), pady=(0, 12))
        ttk.Entry(numeric_tab, textvariable=self.numeric_value, width=24).grid(row=1, column=1, sticky="ew", pady=(0, 12))
        ttk.Button(numeric_tab, text="写入 CSP 数值框", command=self.set_numeric_value).grid(row=1, column=2, padx=(12, 0), pady=(0, 12))
        ttk.Label(numeric_tab, textvariable=self.numeric_note, wraplength=850, justify="left").grid(row=2, column=0, columnspan=3, sticky="w", pady=(4, 18))
        ttk.Label(numeric_tab, text="使用方法：先在此选参数、填目标值；到 CSP 的“工具属性”中点击对应数字；最后点“写入”。程序会重开刚才的数值编辑框并输入，窗口置顶设置会保留。", wraplength=900, justify="left", foreground="#555555").grid(row=3, column=0, columnspan=3, sticky="w")
        self.update_numeric_note()

        self.status_label = ttk.Label(root, textvariable=self.status, anchor="w", justify="left", wraplength=1000, padding=(12, 7))
        self.status_label.grid(row=2, column=0, sticky="ew")
        root.attributes("-topmost", self.topmost.get())

        try:
            ensure_backend()
            settings = request_api("GET", "/settings")
            self.directory.set(settings.get("directory", ""))
            self.base_name.set(settings.get("base_name", ""))
            if not self.directory.get():
                self.first_time_setup()
            elif "--no-create" not in sys.argv:
                result = request_api("POST", "/command", {"command": "create_next_file"})
                self.set_status("已自动创建 CSP 文件：" + result["result"]["created"])
            self.commands = request_api("GET", "/commands").get("commands", [])
            self.command_by_id = {item["id"]: item for item in self.commands}
            categories = ["全部"] + sorted({item["category"] for item in self.commands})
            self.category_box["values"] = categories
            self.refresh_list()
        except Exception as exc:
            self.set_status(str(exc), error=True)
        root.deiconify()

    def first_time_setup(self):
        initial_dir = r"D:\Desktop" if Path(r"D:\Desktop").is_dir() else str(Path.home() / "Desktop")
        selected = filedialog.asksaveasfilename(
            parent=self.root,
            title="首次选择自动保存位置和文件名",
            initialdir=initial_dir,
            initialfile="新建文件.clip",
            filetypes=[("所有文件", "*.*")],
        )
        if not selected:
            self.set_status("未设置自动保存位置。下次打开时可再次设置。", error=True)
            return
        result = request_api("POST", "/command", {"command": "configure_output", "params": {"path": selected}})
        settings = result["result"]["settings"]
        self.directory.set(settings["directory"])
        self.base_name.set(settings["base_name"])
        self.set_status("已创建：" + result["result"]["created"])

    def refresh_list(self):
        self.tree.delete(*self.tree.get_children())
        query = self.search.get().strip().casefold()
        category = self.category.get()
        for item in self.commands:
            if category != "全部" and item["category"] != category:
                continue
            searchable = " ".join((item["name"], item["category"], item.get("shortcut", ""), item.get("description", ""))).casefold()
            if query and query not in searchable:
                continue
            shortcut = item.get("shortcut") or "需配置"
            self.tree.insert("", "end", iid=item["id"], text=item["name"], values=(item["category"], shortcut, item.get("description", "")))

    def on_select(self, _event=None):
        selection = self.tree.selection()
        if selection:
            item = self.command_by_id.get(selection[0], {})
            self.shortcut.set(item.get("shortcut", ""))

    def save_selected_shortcut(self):
        selection = self.tree.selection()
        if not selection:
            self.set_status("请先选择要配置快捷键的功能。", error=True)
            return
        command_id = selection[0]
        shortcut = self.shortcut.get().strip()
        try:
            result = request_api("POST", "/command", {"command": "set_command_shortcut", "params": {"command_id": command_id, "shortcut": shortcut}})
            item = self.command_by_id[command_id]
            item["shortcut"] = result["result"]["shortcut"]
            item["customized"] = True
            self.refresh_list()
            self.tree.selection_set(command_id)
            self.set_status(f"已保存 {item['name']} 的快捷键：{shortcut}")
        except Exception as exc:
            self.set_status(str(exc), error=True)

    def execute_selected(self):
        selection = self.tree.selection()
        if not selection:
            self.set_status("请先从功能列表中选择一项。", error=True)
            return
        command_id = selection[0]
        params = {}
        shortcut = self.shortcut.get().strip()
        if shortcut:
            params["shortcut"] = shortcut
        if command_id == "select_brush_shortcut":
            params["key"] = shortcut
        self.run_command(command_id, params)

    def create_folder(self):
        self.run_command("create_folder", {"name": self.folder_name.get()})
        self.folder_name.set("")

    def run_command(self, command, params=None):
        sends_to_csp = command in self.command_by_id or command == "set_numeric_value"
        restore_topmost = sends_to_csp and self.topmost.get()
        try:
            if sends_to_csp:
                # Let the backend take foreground after this click. A topmost helper
                # can otherwise cover CSP even when keyboard focus changed correctly.
                if restore_topmost:
                    self.root.attributes("-topmost", False)
                health = request_api("GET", "/health")
                backend_pid = int(health.get("pid", 0))
                if backend_pid:
                    user32.AllowSetForegroundWindow(backend_pid)
            result = request_api("POST", "/command", {"command": command, "params": params or {}}, timeout=20)
            data = result.get("result", {})
            if "created" in data:
                message = "已创建：" + data["created"]
            elif data.get("shortcut"):
                message = f"已发送快捷键 {data['shortcut']}。" + data.get("note", "")
            else:
                message = data.get("note", "命令已发送到 CSP：" + command)
            self.set_status(message)
        except Exception as exc:
            self.set_status(str(exc), error=True)
        finally:
            if restore_topmost:
                self.root.attributes("-topmost", True)

    def update_numeric_note(self, _event=None):
        item = self.numeric_parameter_by_label.get(self.numeric_parameter_label.get())
        self.numeric_note.set(item["note"] if item else "")

    def set_numeric_value(self):
        item = self.numeric_parameter_by_label.get(self.numeric_parameter_label.get())
        if not item:
            self.set_status("请选择一个数值参数。", error=True)
            return
        self.run_command("set_numeric_value", {"parameter_id": item["id"], "value": self.numeric_value.get().strip()})

    def set_status(self, message, error=False):
        self.status.set(message)
        self.status_label.configure(foreground="#a12622" if error else "#245b36")

    def toggle_topmost(self):
        enabled = self.topmost.get()
        self.root.attributes("-topmost", enabled)
        try:
            settings = read_ui_settings()
            settings["always_on_top"] = enabled
            write_ui_settings(settings)
            self.set_status("窗口置顶已开启。" if enabled else "窗口置顶已关闭。")
        except OSError as exc:
            self.set_status(f"置顶状态已切换，但设置未能保存：{exc}", error=True)


def main():
    root = tk.Tk()
    CspFrontend(root)
    root.mainloop()


if __name__ == "__main__":
    main()
