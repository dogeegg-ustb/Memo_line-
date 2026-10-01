"""
CLIP STUDIO PAINT (CSP) 快捷键与配置文件全景管理器
- 读取当前用户数据目录中已保存的菜单命令与工具快捷键
- 用户目录含默认项；不推断某项是否曾被手工修改
- 支持载入任意外部 .khc 快捷键文件
- 零第三方依赖，纯 Python 3 标准库
"""

import os
import sys
import sqlite3
import json
import subprocess
from html import escape
from datetime import datetime
import tkinter as tk
from tkinter import ttk, messagebox, filedialog
from csp_shortcuts import read_menu_shortcuts, read_tool_shortcuts

# 保证 UTF-8 编码环境
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


def find_csp_user_dir():
    """自动探测本机 CSP 用户数据与配置目录"""
    appdata = os.environ.get("APPDATA", "")
    userprofile = os.environ.get("USERPROFILE", "")

    candidates = [
        os.path.join(appdata, "CELSYSUserData", "CELSYS", "CLIPStudioPaintVer1_5_0"),
        os.path.join(appdata, "CELSYS", "CLIPStudioPaintVer1_5_0"),
        os.path.join(userprofile, "Documents", "CELSYS", "CLIPStudioPaintVer1_5_0"),
    ]

    for cand in candidates:
        if os.path.exists(cand):
            return os.path.abspath(cand)

    if appdata and os.path.exists(os.path.join(appdata, "CELSYSUserData")):
        for root, dirs, _ in os.walk(os.path.join(appdata, "CELSYSUserData")):
            if "CLIPStudioPaintVer1_5_0" in dirs:
                return os.path.abspath(os.path.join(root, "CLIPStudioPaintVer1_5_0"))

    return None


def get_file_meta(file_path):
    """获取文件物理元信息"""
    if not os.path.exists(file_path):
        return {"exists": False, "size": 0, "mtime": "不存在", "size_str": "0 B"}
    
    stat = os.stat(file_path)
    size = stat.st_size
    if size < 1024:
        size_str = f"{size} B"
    elif size < 1024 * 1024:
        size_str = f"{size / 1024:.1f} KB"
    else:
        size_str = f"{size / (1024 * 1024):.2f} MB"
        
    mtime = datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M:%S")
    return {"exists": True, "size": size, "mtime": mtime, "size_str": size_str}


class CascadeShortcutResolver:
    """Read shortcuts saved in the current CSP user profile."""

    def __init__(self, base_dir=None, custom_khc_path=None):
        self.base_dir = base_dir or find_csp_user_dir()
        self.custom_khc_path = custom_khc_path
        self.configs = []
        self.final_shortcuts = []
        self.errors = []

    def resolve(self):
        """Read saved menu and tool bindings without inventing fallback values."""
        self.configs = []
        self.final_shortcuts = []
        self.errors = []

        if not self.base_dir or not os.path.exists(self.base_dir):
            return False

        # 1. 登记核心配置文件元信息
        config_files_info = [
            ("绘图工具箱", os.path.join("Tool", "EditImageTool.todb"), "画笔工具箱与子工具快捷键核心数据库"),
            ("主菜单快捷键", os.path.join("Shortcut", "default.khc"), "用户已保存的菜单命令快捷键方案"),
            ("修饰键交互", os.path.join("Shortcut", "DefaultToolModifyKey.tomd"), "工具修饰键行为与动态交互配置"),
            ("触控与全局偏好", os.path.join("Preference", "Config.sqlite"), "多点触控手势、手绘板与全局首选项"),
            ("混色调色板", os.path.join("MixPalette", "MixPaletteTool.todb"), "混合调色板专用工具与笔刷配置"),
            ("变换参数预设", os.path.join("DialogBox", "Transform.sqlite"), "自由变换与变形设置历史配置")
        ]

        for cat, rel, title in config_files_info:
            fpath = os.path.join(self.base_dir, rel)
            meta = get_file_meta(fpath)
            self.configs.append({
                "category": cat,
                "name": os.path.basename(fpath),
                "rel_path": rel,
                "full_path": fpath,
                "feature_title": title,
                "exists": meta["exists"],
                "size_str": meta["size_str"],
                "mtime": meta["mtime"]
            })

        khc = self.custom_khc_path or os.path.join(self.base_dir, "Shortcut", "default.khc")
        todb = os.path.join(self.base_dir, "Tool", "EditImageTool.todb")
        for path, reader in ((khc, read_menu_shortcuts), (todb, read_tool_shortcuts)):
            try:
                self.final_shortcuts.extend(reader(path))
            except (OSError, sqlite3.DatabaseError, ValueError) as exc:
                self.errors.append(f"{path}: {exc}")

        return True

# ==============================================================
# 3. 现代化桌面交互界面 (Tkinter GUI)
# ==============================================================

class CSPAppUI:
    def __init__(self, root):
        self.root = root
        self.root.title("CLIP STUDIO PAINT (CSP) 快捷键与配置文件全景管理器")
        self.root.geometry("1100x720")
        self.root.minsize(950, 620)

        self.resolver = CascadeShortcutResolver()
        self.current_filter_mode = "all"  # 'all', 'menu_only', 'tool_only'

        self._setup_style()
        self._create_widgets()

        # 启动后读取用户配置
        self.root.after(100, self.do_scan)

    def _setup_style(self):
        style = ttk.Style()
        style.theme_use("clam")

        self.bg_color = "#f4f6f9"
        self.card_bg = "#ffffff"
        self.primary_color = "#1976d2"
        self.text_color = "#2c3e50"

        self.root.configure(bg=self.bg_color)
        style.configure("Treeview", 
                        background="#ffffff", 
                        foreground="#333333", 
                        rowheight=26, 
                        fieldbackground="#ffffff",
                        font=("Segoe UI", 9))
        style.configure("Treeview.Heading", 
                        background="#e8edf2", 
                        foreground="#2c3e50", 
                        font=("Segoe UI", 9, "bold"))
        style.map("Treeview", background=[("selected", "#d1e4fa")], foreground=[("selected", "#0d47a1")])

    def _create_widgets(self):
        # 1. 顶部 Header
        header_frame = tk.Frame(self.root, bg=self.primary_color, height=68)
        header_frame.pack(fill=tk.X, side=tk.TOP)

        title_frame = tk.Frame(header_frame, bg=self.primary_color)
        title_frame.pack(side=tk.LEFT, padx=18, pady=10)

        title_label = tk.Label(title_frame, text="🎨 CLIP STUDIO PAINT 快捷键全景管理器", 
                               font=("Segoe UI", 13, "bold"), fg="#ffffff", bg=self.primary_color)
        title_label.pack(anchor="w")

        self.subtitle_var = tk.StringVar(value="读取本机已保存的菜单命令与工具快捷键")
        subtitle_label = tk.Label(title_frame, textvariable=self.subtitle_var, 
                                  font=("Segoe UI", 8), fg="#bbdefb", bg=self.primary_color)
        subtitle_label.pack(anchor="w")

        # 顶部操作按钮
        btn_frame = tk.Frame(header_frame, bg=self.primary_color)
        btn_frame.pack(side=tk.RIGHT, padx=15, pady=12)

        btn_load_custom = tk.Button(btn_frame, text="📂 载入外部快捷键(.khc)", font=("Segoe UI", 9), 
                                    bg="#e3f2fd", fg="#0d47a1", relief=tk.FLAT, padx=8, pady=4,
                                    cursor="hand2", command=self.load_custom_khc)
        btn_load_custom.pack(side=tk.LEFT, padx=4)

        btn_rescan = tk.Button(btn_frame, text="🔄 重新扫描", font=("Segoe UI", 9, "bold"), 
                               bg="#ffffff", fg=self.primary_color, relief=tk.FLAT, padx=8, pady=4,
                               cursor="hand2", command=self.do_scan)
        btn_rescan.pack(side=tk.LEFT, padx=4)

        btn_open_folder = tk.Button(btn_frame, text="📁 打开配置目录", font=("Segoe UI", 9), 
                                    bg="#e3f2fd", fg="#0d47a1", relief=tk.FLAT, padx=8, pady=4,
                                    cursor="hand2", command=self.open_current_dir)
        btn_open_folder.pack(side=tk.LEFT, padx=4)

        btn_export = tk.Button(btn_frame, text="💾 导出对照表", font=("Segoe UI", 9), 
                               bg="#e3f2fd", fg="#0d47a1", relief=tk.FLAT, padx=8, pady=4,
                               cursor="hand2", command=self.export_data)
        btn_export.pack(side=tk.LEFT, padx=4)

        # 2. 中间上下分栏 PanedWindow
        paned = tk.PanedWindow(self.root, orient=tk.VERTICAL, sashrelief=tk.RAISED, sashwidth=4, bg=self.bg_color)
        paned.pack(fill=tk.BOTH, expand=True, padx=15, pady=10)

        # 2.1 上半区：关联配置文件与系统职责清单
        top_frame = tk.Frame(paned, bg=self.card_bg, bd=1, relief=tk.SOLID)
        paned.add(top_frame, height=190)

        top_title_bar = tk.Frame(top_frame, bg="#fafafa", height=32)
        top_title_bar.pack(fill=tk.X, side=tk.TOP)
        tk.Label(top_title_bar, text="📑 1. 负责快捷键与交互行为的核心配置文件列表", 
                 font=("Segoe UI", 9, "bold"), fg=self.text_color, bg="#fafafa").pack(side=tk.LEFT, padx=10, pady=6)

        self.khc_path_var = tk.StringVar(value="当前生效快捷键方案: 默认 default.khc")
        tk.Label(top_title_bar, textvariable=self.khc_path_var, 
                 font=("Segoe UI", 8), fg="#1976d2", bg="#fafafa").pack(side=tk.RIGHT, padx=10)

        cfg_cols = ("category", "name", "feature_title", "size", "mtime", "full_path")
        self.tree_cfg = ttk.Treeview(top_frame, columns=cfg_cols, show="headings", selectmode="browse")
        self.tree_cfg.heading("category", text="功能模块分类")
        self.tree_cfg.heading("name", text="配置文件名")
        self.tree_cfg.heading("feature_title", text="承载的 CSP 核心功能职责")
        self.tree_cfg.heading("size", text="大小")
        self.tree_cfg.heading("mtime", text="最后修改时间")
        self.tree_cfg.heading("full_path", text="物理绝对路径")

        self.tree_cfg.column("category", width=110, anchor="center")
        self.tree_cfg.column("name", width=170, anchor="w")
        self.tree_cfg.column("feature_title", width=330, anchor="w")
        self.tree_cfg.column("size", width=80, anchor="center")
        self.tree_cfg.column("mtime", width=140, anchor="center")
        self.tree_cfg.column("full_path", width=340, anchor="w")

        scy1 = ttk.Scrollbar(top_frame, orient=tk.VERTICAL, command=self.tree_cfg.yview)
        self.tree_cfg.configure(yscrollcommand=scy1.set)
        scy1.pack(side=tk.RIGHT, fill=tk.Y)
        self.tree_cfg.pack(fill=tk.BOTH, expand=True)

        # 2.2 下半区：已保存的快捷键表
        bottom_frame = tk.Frame(paned, bg=self.card_bg, bd=1, relief=tk.SOLID)
        paned.add(bottom_frame, height=410)

        bot_title_bar = tk.Frame(bottom_frame, bg="#fafafa", height=36)
        bot_title_bar.pack(fill=tk.X, side=tk.TOP)

        self.detail_summary_var = tk.StringVar(value="⌨️ 2. 已保存的快捷键对照表")
        tk.Label(bot_title_bar, textvariable=self.detail_summary_var, 
                 font=("Segoe UI", 9, "bold"), fg=self.text_color, bg="#fafafa").pack(side=tk.LEFT, padx=10, pady=8)

        # 来源过滤按钮组
        filter_frame = tk.Frame(bot_title_bar, bg="#fafafa")
        filter_frame.pack(side=tk.LEFT, padx=15)
        
        self.btn_filter_all = tk.Button(filter_frame, text="全部显示", font=("Segoe UI", 8, "bold"), 
                                        bg="#e0e0e0", relief=tk.FLAT, padx=6, command=lambda: self.set_filter("all"))
        self.btn_filter_all.pack(side=tk.LEFT, padx=2)

        self.btn_filter_custom = tk.Button(filter_frame, text="菜单命令", font=("Segoe UI", 8),
                                           bg="#ffffff", relief=tk.GROOVE, padx=6, command=lambda: self.set_filter("menu_only"))
        self.btn_filter_custom.pack(side=tk.LEFT, padx=2)

        self.btn_filter_fallback = tk.Button(filter_frame, text="工具 / 子工具", font=("Segoe UI", 8),
                                             bg="#ffffff", relief=tk.GROOVE, padx=6, command=lambda: self.set_filter("tool_only"))
        self.btn_filter_fallback.pack(side=tk.LEFT, padx=2)

        # 搜索过滤框
        search_frame = tk.Frame(bot_title_bar, bg="#fafafa")
        search_frame.pack(side=tk.RIGHT, padx=10)
        tk.Label(search_frame, text="🔍 搜索:", font=("Segoe UI", 9), bg="#fafafa").pack(side=tk.LEFT, padx=3)
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", lambda *args: self.render_details())
        tk.Entry(search_frame, textvariable=self.search_var, width=18, font=("Segoe UI", 9)).pack(side=tk.LEFT, padx=3)

        # 快捷键明细表格
        detail_cols = ("shortcut", "action_name", "scope", "source", "details")
        self.tree_detail = ttk.Treeview(bottom_frame, columns=detail_cols, show="headings")
        self.tree_detail.heading("shortcut", text="快捷键 / 触发按键")
        self.tree_detail.heading("action_name", text="对应功能 / 操作名称")
        self.tree_detail.heading("scope", text="所属模块")
        self.tree_detail.heading("source", text="数据来源")
        self.tree_detail.heading("details", text="CSP 命令标识 / 节点")

        self.tree_detail.column("shortcut", width=150, anchor="center")
        self.tree_detail.column("action_name", width=220, anchor="w")
        self.tree_detail.column("scope", width=120, anchor="center")
        self.tree_detail.column("source", width=140, anchor="center")
        self.tree_detail.column("details", width=460, anchor="w")

        scy2 = ttk.Scrollbar(bottom_frame, orient=tk.VERTICAL, command=self.tree_detail.yview)
        self.tree_detail.configure(yscrollcommand=scy2.set)
        scy2.pack(side=tk.RIGHT, fill=tk.Y)
        self.tree_detail.pack(fill=tk.BOTH, expand=True)

        # 底部状态栏
        self.status_var = tk.StringVar(value="就绪")
        status_bar = tk.Label(self.root, textvariable=self.status_var, 
                              font=("Segoe UI", 8), fg="#7f8c8d", bg=self.bg_color, anchor="w", padx=15, pady=4)
        status_bar.pack(fill=tk.X, side=tk.BOTTOM)

    def do_scan(self):
        """读取配置并更新界面"""
        self.status_var.set("正在读取已保存的快捷键...")
        self.root.update_idletasks()

        ok = self.resolver.resolve()
        if not ok or not self.resolver.base_dir:
            self.status_var.set("未找到有效的 CSP 配置目录")
            messagebox.showwarning("提示", "未能在本机常见路径下定位到 CSP 配置文件夹。")
            return

        # 更新配置列表
        self.tree_cfg.delete(*self.tree_cfg.get_children())
        for cfg in self.resolver.configs:
            self.tree_cfg.insert("", tk.END, values=(
                cfg["category"],
                cfg["name"],
                cfg["feature_title"],
                cfg["size_str"],
                cfg["mtime"],
                cfg["full_path"]
            ))

        total = len(self.resolver.final_shortcuts)
        menu_count = sum(x["scope"] == "菜单命令" for x in self.resolver.final_shortcuts)
        tool_count = total - menu_count
        self.detail_summary_var.set(f"⌨️ 2. 已保存的快捷键 (共 {total} 项：菜单 {menu_count}，工具 {tool_count})")
        self.status_var.set("已读取用户配置文件。" + ("读取错误：" + "; ".join(self.resolver.errors) if self.resolver.errors else ""))

        self.render_details()

    def set_filter(self, mode):
        """切换显示过滤模式"""
        self.current_filter_mode = mode
        # 更新按钮样式
        self.btn_filter_all.configure(bg="#e0e0e0" if mode == "all" else "#ffffff", relief=tk.FLAT if mode == "all" else tk.GROOVE)
        self.btn_filter_custom.configure(bg="#e0e0e0" if mode == "menu_only" else "#ffffff", relief=tk.FLAT if mode == "menu_only" else tk.GROOVE)
        self.btn_filter_fallback.configure(bg="#e0e0e0" if mode == "tool_only" else "#ffffff", relief=tk.FLAT if mode == "tool_only" else tk.GROOVE)
        self.render_details()

    def render_details(self):
        """根据搜索与过滤条件渲染快捷键表格"""
        kw = self.search_var.get().strip().lower()
        self.tree_detail.delete(*self.tree_detail.get_children())

        match_count = 0
        for it in self.resolver.final_shortcuts:
            # 模式过滤
            if self.current_filter_mode == "menu_only" and it["scope"] != "菜单命令":
                continue
            if self.current_filter_mode == "tool_only" and it["scope"] != "工具 / 子工具":
                continue

            # 关键字过滤
            sc = it["shortcut"].lower()
            an = it["action_name"].lower()
            det = it["details"].lower()
            scope = it["scope"].lower()

            if not kw or (kw in sc or kw in an or kw in det or kw in scope):
                self.tree_detail.insert("", tk.END, values=(
                    it["shortcut"],
                    it["action_name"],
                    it["scope"],
                    it["source"],
                    it["details"]
                ))
                match_count += 1

        if kw:
            self.status_var.set(f"搜索 '{kw}' 匹配到 {match_count} 项结果")

    def load_custom_khc(self):
        """允许用户指定外部自定义 .khc 快捷键文件"""
        khc_file = filedialog.askopenfilename(
            title="选择用户自定义快捷键文件 (.khc)",
            filetypes=[("CSP 快捷键配置 (*.khc)", "*.khc"), ("所有文件 (*.*)", "*.*")]
        )
        if khc_file and os.path.exists(khc_file):
            self.resolver.custom_khc_path = khc_file
            self.khc_path_var.set(f"外部生效方案: {os.path.basename(khc_file)}")
            self.do_scan()
            messagebox.showinfo("成功", f"已读取快捷键文件:\n{khc_file}")

    def open_current_dir(self):
        """在资源管理器中打开当前配置目录"""
        if self.resolver.base_dir and os.path.exists(self.resolver.base_dir):
            subprocess.Popen(f'explorer "{self.resolver.base_dir}"')

    def export_data(self):
        """导出当前合并后的全景表"""
        script_dir = os.path.dirname(os.path.abspath(__file__))
        out_dir = os.path.join(script_dir, "outputs")
        os.makedirs(out_dir, exist_ok=True)

        save_path = filedialog.asksaveasfilename(
            title="选择导出位置",
            initialdir=out_dir,
            defaultextension=".md",
            initialfile="CSP_已保存快捷键.md",
            filetypes=[("Markdown 文档 (*.md)", "*.md"), ("HTML 网页报表 (*.html)", "*.html"), ("JSON 数据 (*.json)", "*.json")]
        )
        if not save_path:
            return

        ext = os.path.splitext(save_path)[1].lower()
        if ext == ".html":
            self._export_html(save_path)
        elif ext == ".json":
            with open(save_path, "w", encoding="utf-8") as f:
                json.dump({"shortcuts": self.resolver.final_shortcuts}, f, ensure_ascii=False, indent=2)
        else:
            self._export_markdown(save_path)

        messagebox.showinfo("导出成功", f"已保存快捷键对照表已导出至:\n{save_path}")

    def _export_markdown(self, path):
        lines = [
            "# CLIP STUDIO PAINT (CSP) 已保存快捷键对照表\n",
            f"> **生成时间**：{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}  ",
            "> **数据说明**：来自用户目录中的 .khc 和 .todb；包含原有默认项，不代表每项都曾手动修改。\n",
            "| 快捷键 / 触发按键 | 对应功能 / 操作名称 | 所属模块 | 数据来源 | CSP 命令标识 / 节点 |",
            "| :---: | :--- | :---: | :---: | :--- |"
        ]
        for it in self.resolver.final_shortcuts:
            cells = [str(it[k]).replace("|", "\\|").replace("\n", " ") for k in ("shortcut", "action_name", "scope", "source", "details")]
            lines.append(f"| **{cells[0]}** | {cells[1]} | {cells[2]} | {cells[3]} | {cells[4]} |")

        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines))

    def _export_html(self, path):
        html = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>CLIP STUDIO PAINT 已保存快捷键对照表</title>
<style>
  body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #f8fafc; color: #1e293b; padding: 24px; }}
  h1 {{ color: #0f172a; margin-bottom: 6px; }}
  .meta {{ color: #64748b; font-size: 14px; margin-bottom: 24px; }}
  table {{ width: 100%; border-collapse: collapse; background: #fff; box-shadow: 0 1px 3px rgba(0,0,0,0.1); border-radius: 8px; overflow: hidden; }}
  th, td {{ padding: 10px 14px; text-align: left; border-bottom: 1px solid #e2e8f0; font-size: 14px; }}
  th {{ background: #f1f5f9; color: #475569; font-weight: 600; }}
  tr:hover {{ background: #f8fafc; }}
  .kbd {{ display: inline-block; padding: 2px 7px; font-weight: bold; background: #e2e8f0; border-radius: 4px; border: 1px solid #cbd5e1; }}
</style>
</head>
<body>
<h1>🎨 CLIP STUDIO PAINT 已保存快捷键对照表</h1>
<div class="meta">来自用户配置文件；包含原有默认项，不代表每项都曾手动修改。生成时间：{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}</div>
<table>
  <tr><th>快捷键 / 触发按键</th><th>对应功能 / 操作名称</th><th>所属模块</th><th>数据来源</th><th>CSP 命令标识 / 节点</th></tr>
  {"".join(f"<tr><td><span class='kbd'>{escape(it['shortcut'])}</span></td><td><b>{escape(it['action_name'])}</b></td><td>{escape(it['scope'])}</td><td>{escape(it['source'])}</td><td>{escape(it['details'])}</td></tr>" for it in self.resolver.final_shortcuts)}
</table>
</body>
</html>"""
        with open(path, "w", encoding="utf-8") as f:
            f.write(html)


def main():
    if "--export" in sys.argv:
        r = CascadeShortcutResolver()
        r.resolve()
        script_dir = os.path.dirname(os.path.abspath(__file__))
        out_dir = os.path.join(script_dir, "outputs")
        os.makedirs(out_dir, exist_ok=True)
        md = os.path.join(out_dir, "CSP_已保存快捷键.md")
        html = os.path.join(out_dir, "CSP_已保存快捷键.html")
        app_dummy = CSPAppUI.__new__(CSPAppUI)
        app_dummy.resolver = r
        app_dummy._export_markdown(md)
        app_dummy._export_html(html)
        print(f"[+] 导出完成！已生成全景清单:\n    {md}\n    {html}")
        return

    if "--test" in sys.argv:
        root = tk.Tk()
        app = CSPAppUI(root)
        root.update()
        root.destroy()
        print("[+] GUI shortcut scan test passed successfully!")
        return

    root = tk.Tk()
    app = CSPAppUI(root)
    root.mainloop()


if __name__ == "__main__":
    main()
