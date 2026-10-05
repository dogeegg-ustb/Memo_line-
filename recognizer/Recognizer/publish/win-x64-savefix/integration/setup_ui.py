"""Explicit startup UI; only this user-operated picker may receive focus."""
import json
import ctypes
import queue
import sys
import threading
import uuid
import os
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from catalog import NAVIGATOR
from csp_workspace_overlay import inspect


class SetupDialog:
    def __init__(self, runtime):
        self.runtime = runtime
        self.pending = False
        self.clip_generation = 0
        self.window = tk.Toplevel(runtime.root)
        self.window.title("记艺线 · 录制初始化")
        self.window.geometry("640x550")
        self.window.protocol("WM_DELETE_WINDOW", self.cancel)
        self.width, self.height = tk.StringVar(), tk.StringVar()
        self.clip = tk.StringVar(value=runtime.settings.get("clipPath", ""))
        self.flash = tk.BooleanVar(value=runtime.settings["flashBorders"])
        self.workers = tk.IntVar(value=runtime.settings["analysisConcurrency"])
        self.status = tk.StringVar(value="请打开 CSP 和待录制文档，保持工作区布局固定。")
        tk.Label(self.window, textvariable=self.status, wraplength=530, justify="left").pack(padx=12,pady=10)
        row = tk.Frame(self.window); row.pack(fill="x",padx=12)
        tk.Entry(row,textvariable=self.clip).pack(side="left",fill="x",expand=True)
        tk.Button(row,text="选择当前 .clip",command=self.select_clip).pack(side="right")
        self.driver_choice = tk.StringVar(value="请选择本次使用的驱动配置")
        self.driver_details = tk.StringVar(value="正在查找已安装驱动的配置文件……")
        self.driver_choices = {}
        self.driver_request_id = None
        self.driver_applied_selection = None
        self.driver_requested_selection = None
        row = tk.Frame(self.window); row.pack(fill="x",padx=12,pady=(10,0))
        tk.Label(row,text="驱动配置：").pack(side="left")
        self.driver_combo = ttk.Combobox(row,textvariable=self.driver_choice,state="readonly")
        self.driver_combo.pack(side="left",fill="x",expand=True)
        self.driver_combo.bind("<<ComboboxSelected>>",lambda _: self.update_driver_details())
        tk.Button(row,text="选择文件…",command=self.select_driver_file).pack(side="right")
        tk.Label(self.window,textvariable=self.driver_details,wraplength=610,justify="left").pack(fill="x",padx=12,pady=4)
        self.screen_choice = tk.StringVar(value="使用配置中的屏幕区域（单屏自动）")
        self.screen_choices = {self.screen_choice.get(): None}
        self.screen_row = tk.Frame(self.window)
        tk.Label(self.screen_row,text="映射屏幕：").pack(side="left")
        self.screen_combo = ttk.Combobox(self.screen_row,textvariable=self.screen_choice,state="readonly")
        self.screen_combo.pack(side="left",fill="x",expand=True)
        self.update_driver_configurations(runtime.driver_configurations)
        row = tk.Frame(self.window); row.pack(pady=10)
        for title, variable in (("画布宽（像素）",self.width),("高（像素）",self.height)):
            tk.Label(row,text=title).pack(side="left")
            tk.Entry(row,textvariable=variable,width=9).pack(side="left")
        tk.Checkbutton(self.window,text="启用面板边框闪烁",variable=self.flash).pack()
        row = tk.Frame(self.window); row.pack(pady=8)
        tk.Label(row,text="同时解析任务数（每个核心始终串行）").pack(side="left")
        tk.Spinbox(row,from_=1,to=4,textvariable=self.workers,width=4).pack(side="left")
        tk.Label(self.window,text="立即冻结 CSP 窗口，在导航器中框选缩放和旋转数字。\n工作区 ROI 不变时自动沿用上次选区；也可主动重新框选。",
                 wraplength=530).pack(pady=8)
        self.capture_button = tk.Button(self.window,text="开始（自动沿用有效数字选区）",command=self.begin)
        self.capture_button.pack(pady=8)
        self.reselect_button = tk.Button(self.window,text="重新冻结 CSP 并框选数字",command=lambda: self.begin(True))
        self.reselect_button.pack()
        if self.clip.get():
            self.window.after(50,lambda: self.read_dimensions(self.clip.get()))
        self.window.after_idle(self.fit_dialog)

    def fit_dialog(self):
        if not self.window.winfo_exists():
            return
        self.window.update_idletasks()
        width = max(640, self.window.winfo_reqwidth() + 16)
        height = max(550, self.window.winfo_reqheight() + 16)
        self.window.minsize(width, height)
        self.window.geometry(f"{width}x{height}")

    def update_driver_configurations(self, catalog):
        if catalog is None:
            return
        previous = self.driver_choices.get(self.driver_choice.get(), self.runtime.settings.get("driverConfigPath"))
        fallback = "不使用驱动映射（保留原始数据）"
        self.driver_choices = {fallback: None}
        for index, choice in enumerate(catalog.get("choices", []), 1):
            label = f"{index}. {choice['vendor']} · {choice['deviceName']}"
            self.driver_choices[label] = choice["path"]
        self.driver_combo.configure(values=list(self.driver_choices))
        for label, path in self.driver_choices.items():
            if path and previous and os.path.normcase(os.path.normpath(path)) == os.path.normcase(os.path.normpath(previous)):
                self.driver_choice.set(label)
                break
        else:
            if self.runtime.settings.get("driverMappingDisabled"):
                self.driver_choice.set(fallback)
        self.screen_choices = {"使用配置中的屏幕区域（单屏自动）": None}
        for display in catalog.get("displays", []):
            bounds = display["bounds"]
            label = f"屏幕 {display['index'] + 1} · {display['name']} · {bounds['width']:g}×{bounds['height']:g}"
            self.screen_choices[label] = display["index"]
        self.screen_combo.configure(values=list(self.screen_choices))
        previous_screen = self.runtime.settings.get("driverScreenIndex")
        for label, index in self.screen_choices.items():
            if previous_screen == index:
                self.screen_choice.set(label)
                break
        if len(catalog.get("displays", [])) > 1:
            self.screen_row.pack(fill="x",padx=12,pady=4,after=self.driver_combo.master)
        self.update_driver_details()

    def update_driver_details(self):
        label = self.driver_choice.get()
        if label not in self.driver_choices:
            self.driver_details.set("请选择要使用的驱动配置，也可手动选择配置文件。")
            return
        path = self.driver_choices[label]
        self.driver_details.set(path or "本次保留原始压力与 Windows 屏幕坐标，不应用驱动映射。")
        self.window.after_idle(self.fit_dialog)

    def select_driver_file(self):
        path = filedialog.askopenfilename(parent=self.window,title="选择本次使用的数位板驱动配置",
            filetypes=[("驱动配置","*.dt *.json *.xml *.wacomprefs *.wacomxs *.prefs *.dat"),("所有文件","*.*")])
        if path:
            label = "手动选择 · " + os.path.basename(path)
            self.driver_choices[label] = path
            self.driver_combo.configure(values=list(self.driver_choices))
            self.driver_choice.set(label)
            self.update_driver_details()

    def driver_configuration_result(self, msg):
        if not self.driver_request_id or msg.get("requestId") != self.driver_request_id:
            return
        self.driver_request_id = None
        self.pending = False
        self.capture_button.configure(state="normal")
        data = msg["data"]
        if data.get("status") not in {"selected", "disabled"}:
            error = "所选驱动配置不可用：" + "；".join(data.get("warnings") or ["无法读取配置文件"])
            self.status.set(error)
            messagebox.showerror("驱动配置未应用",error,parent=self.window)
            return
        self.driver_applied_selection = self.driver_requested_selection
        path, screen = self.driver_applied_selection
        self.runtime.settings.update(driverConfigPath=path,driverMappingDisabled=path is None,driverScreenIndex=screen)
        self.begin(self.driver_continue_force_selection)

    def select_clip(self):
        path = filedialog.askopenfilename(parent=self.window,filetypes=[("CSP 文档","*.clip")])
        if not path:
            return
        self.clip.set(path)
        self.read_dimensions(path)

    def read_dimensions(self, path):
        self.clip_generation += 1
        generation = self.clip_generation
        replies = queue.Queue()
        self.status.set("正在读取 .clip 画布尺寸，请稍候……")
        self.capture_button.configure(state="disabled")
        def read():
            try:
                from recognizer_core.clip_layers_core import read_clip_layers
                replies.put((read_clip_layers(path)["canvas"],None))
            except Exception as ex:
                replies.put((None,str(ex)))
        def poll():
            if generation != self.clip_generation or not self.window.winfo_exists():
                return
            try:
                info,error = replies.get_nowait()
            except queue.Empty:
                self.window.after(50,poll)
                return
            self.capture_button.configure(state="normal")
            if error:
                self.status.set("无法读取文档，请手动填写尺寸："+error)
            elif info["unit_raw"] == 0:
                self.width.set(str(round(float(info["width_raw"]))))
                self.height.set(str(round(float(info["height_raw"]))))
                self.status.set("已读取文档尺寸，请确认与 CSP 当前画布一致，然后点击截图。")
            else:
                self.status.set("文档尺寸使用非像素单位，请在 CSP 确认并手动填写实际像素宽高。")
        threading.Thread(target=read,daemon=True,name="setup-clip-dimensions").start()
        self.window.after(50,poll)

    def layout_key(self):
        # Check live geometry at use time, not only the last background poll.
        _, layout = inspect(self.runtime.path, self.runtime.hwnd)
        signature = json.dumps({k: layout[k] for k in ("hwnd", "client_rect", "regions")}, sort_keys=True)
        if signature != self.runtime.initial_signature:
            raise ValueError("工作区位置已变化，请恢复启动时布局或重新启动记录器")
        user32 = self.runtime.user32
        user32.GetDpiForWindow.argtypes = [ctypes.c_void_p]
        user32.GetDpiForWindow.restype = ctypes.c_uint
        return dict(clientRect=layout["client_rect"], regions=layout["regions"],
                    dpi=int(user32.GetDpiForWindow(self.runtime.hwnd) or 96))

    def cached_roi(self):
        cached = self.runtime.settings.get("navigatorOcrSelection", {})
        roi = cached.get("roi")
        if cached.get("layout") != self.selection_layout or not isinstance(roi, list) or len(roi) != 4:
            return None
        if not all(type(v) is int for v in roi) or min(roi[2:]) < 32:
            return None
        x,y,w,h = self.runtime.engine.regions[NAVIGATOR]
        rx,ry,rw,rh = roi
        return roi if x <= rx and y <= ry and rx+rw <= x+w and ry+rh <= y+h else None

    def begin(self, force_selection=False):
        if self.pending:
            return
        try:
            if not self.width.get().strip() or not self.height.get().strip():
                raise ValueError("请先选择当前 .clip 读取尺寸，或填写画布的像素宽高。")
            self.dimensions = (int(self.width.get()),int(self.height.get()))
            if not all(0 < n <= 1000000 for n in self.dimensions):
                raise ValueError("画布像素宽高需为正整数")
            workers = int(self.workers.get())
            if not 1 <= workers <= 4:
                raise ValueError("同时解析任务数需为 1–4")
            if not self.runtime.engine.regions:
                raise ValueError("尚未匹配 CSP 工作区；请打开 CSP 并保存工作区布局")
            if self.driver_choice.get() not in self.driver_choices:
                raise ValueError("请先选择本次使用的驱动配置，或明确选择不使用驱动映射。")
            selection = (self.driver_choices[self.driver_choice.get()], self.screen_choices.get(self.screen_choice.get()))
            if self.driver_applied_selection != selection:
                self.pending = True
                self.driver_request_id = uuid.uuid4().hex
                self.driver_requested_selection = selection
                self.driver_continue_force_selection = force_selection
                self.capture_button.configure(state="disabled")
                self.status.set("正在读取并应用所选驱动配置……")
                self.runtime.request_driver_configuration(self.driver_request_id, *selection)
                return
            self.runtime.settings.update(flashBorders=self.flash.get(),analysisConcurrency=workers,clipPath=self.clip.get())
            self.runtime.settings_path.write_text(json.dumps(self.runtime.settings,ensure_ascii=False,indent=2),encoding="utf-8")
            self.selection_layout = self.layout_key()
            self.pending = True
            self.capture_button.configure(state="disabled")
            self.status.set("正在冻结 CSP 窗口……")
            self.window.withdraw()
            self.runtime.show(set())
            self.runtime.show_origin(False)
            self.runtime.show_core_rois(False)
            self.runtime.show_viewport(False)
            # This explicit setup action brings CSP forward; the observer itself never does.
            self.runtime.user32.SetForegroundWindow.argtypes = [ctypes.c_void_p]
            self.runtime.user32.IsIconic.argtypes = [ctypes.c_void_p]
            if self.runtime.user32.IsIconic(self.runtime.hwnd):
                self.runtime.user32.ShowWindow(self.runtime.hwnd, 9)
            self.runtime.user32.SetForegroundWindow(self.runtime.hwnd)
            if self.runtime.user32.GetForegroundWindow() != self.runtime.hwnd:
                raise ValueError("无法将 CSP 窗口置于前台，请打开 CSP 后重试")
            roi = None if force_selection else self.cached_roi()
            if roi:
                print("[Initialization] 工作区 ROI 未变，沿用上次导航器数字选区。",file=sys.stderr,flush=True)
                self.window.destroy()
                self.runtime.start_pipeline(self.dimensions,roi,self.clip.get())
            else:
                self.runtime.root.after_idle(self.freeze)
        except Exception as ex:
            self.pending = False
            self.capture_button.configure(state="normal")
            self.window.deiconify()
            self.status.set(str(ex))
            messagebox.showerror("尚未开始截图",str(ex),parent=self.window)

    def freeze(self):
        try:
            if self.runtime.user32.GetForegroundWindow() != self.runtime.hwnd:
                raise ValueError("无法将 CSP 窗口置于前台，请打开 CSP 后重试截图")
            if self.layout_key() != self.selection_layout or not self.runtime.engine.regions:
                raise ValueError("工作区位置已变化，请恢复布局后重试")
            from PIL import Image, ImageTk
            import mss
            self.nav = tuple(self.runtime.engine.regions[NAVIGATOR])
            self.freeze_rect = tuple(self.selection_layout["clientRect"])
            x,y,w,h = self.freeze_rect
            with mss.mss() as grabber:
                pixels = grabber.grab(dict(left=x,top=y,width=w,height=h))
                self.frozen = Image.frombytes("RGB",pixels.size,pixels.bgra,"raw","BGRX")
            self.picker = tk.Toplevel(self.runtime.root)
            self.picker.title("冻结 CSP：框选导航器缩放和旋转数字，Enter 确认，Esc 取消")
            self.picker.overrideredirect(True)
            self.picker.geometry(f"{w}x{h}{x:+d}{y:+d}")
            self.picker.attributes("-topmost", True)
            self.photo = ImageTk.PhotoImage(self.frozen)
            self.canvas = tk.Canvas(self.picker,width=w,height=h,highlightthickness=0)
            self.canvas.pack()
            self.canvas.create_image(0,0,anchor="nw",image=self.photo)
            nx,ny,nw,nh = self.nav
            self.canvas.create_rectangle(nx-x,ny-y,nx-x+nw,ny-y+nh,outline="#168cff",width=2)
            self.canvas.create_text(20,20,anchor="nw",text="在蓝框导航器内框选缩放和旋转数字 · Enter 确认 · Esc 返回",
                                    fill="#ffcc00",font=("Microsoft YaHei UI",14,"bold"))
            self.rectangle = None
            self.selected = None
            self.canvas.bind("<ButtonPress-1>",self.down)
            self.canvas.bind("<B1-Motion>",self.drag)
            self.picker.bind("<Return>",self.confirm)
            self.picker.bind("<Escape>",self.back)
            self.picker.protocol("WM_DELETE_WINDOW",self.back)
            self.picker.lift()
            self.picker.focus_force()
            print("[Initialization] CSP 窗口截图已冻结，请在导航器内框选数字并按 Enter。",file=sys.stderr,flush=True)
        except Exception as ex:
            self.pending = False
            self.capture_button.configure(state="normal")
            self.status.set(str(ex)); self.window.deiconify(); self.window.lift()
            print("[Initialization] 截图失败："+str(ex),file=sys.stderr,flush=True)
            messagebox.showerror("截图失败，可重试",str(ex),parent=self.window)

    def down(self,event):
        self.start = (event.x,event.y)
        self.selected = None
        if self.rectangle:
            self.canvas.delete(self.rectangle)
        self.rectangle = self.canvas.create_rectangle(event.x,event.y,event.x,event.y,outline="#ffcc00",width=2)

    def drag(self,event):
        w,h = self.freeze_rect[2:]
        x0,y0 = self.start
        x1,y1 = max(0,min(w,event.x)),max(0,min(h,event.y))
        self.selected = (min(x0,x1),min(y0,y1),abs(x1-x0),abs(y1-y0))
        self.canvas.coords(self.rectangle,x0,y0,x1,y1)

    def confirm(self,event=None):
        if not self.selected or min(self.selected[2:]) < 32:
            messagebox.showinfo("选区过小","请框选同时包含两个数字的区域，宽高至少 32 像素。",parent=self.picker)
            return
        x,y,w,h = self.selected
        roi = [self.freeze_rect[0]+x,self.freeze_rect[1]+y,w,h]
        nx,ny,nw,nh = self.nav
        if not (nx <= roi[0] and ny <= roi[1] and roi[0]+w <= nx+nw and roi[1]+h <= ny+nh):
            messagebox.showinfo("请在导航器内框选","数字选区需完整位于蓝框标出的导航器面板内。",parent=self.picker)
            return
        if self.layout_key() != self.selection_layout or not self.runtime.engine.regions:
            messagebox.showerror("工作区已变化","请按 Esc 返回，恢复工作区后重新截图。",parent=self.picker)
            return
        self.runtime.settings["navigatorOcrSelection"] = dict(layout=self.selection_layout,roi=roi)
        self.runtime.settings_path.write_text(json.dumps(self.runtime.settings,ensure_ascii=False,indent=2),encoding="utf-8")
        self.picker.destroy(); self.window.destroy(); self.frozen.close()
        self.runtime.user32.SetForegroundWindow(self.runtime.hwnd)
        self.runtime.start_pipeline(self.dimensions,roi,self.clip.get())

    def back(self,event=None):
        self.picker.destroy(); self.frozen.close(); self.window.deiconify()
        self.pending = False
        self.capture_button.configure(state="normal")

    def cancel(self):
        self.status.set("初始化尚未完成。填写尺寸并框选数字后才能开始状态采集；关闭 Recorder 可退出。")
