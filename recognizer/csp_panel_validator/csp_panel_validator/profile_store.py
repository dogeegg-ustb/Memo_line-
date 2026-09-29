from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Any

from .models import Profile, SCHEMA_VERSION


class ProfileError(ValueError):
    pass


class ProfileStore:
    def __init__(self, root: str | Path):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)

    def list_profiles(self) -> list[Path]:
        return self.list_user_profiles()

    def list_user_profiles(self) -> list[Path]:
        """Only profiles created by the initialization flow are offered to the UI."""
        paths: list[Path] = []
        for path in sorted(self.root.glob("*.json")):
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            if data.get("profile_kind") == "user" and isinstance(data.get("target_window"), dict):
                paths.append(path)
        return paths

    def load(self, path: str | Path) -> Profile:
        path = Path(path)
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            profile = Profile.from_dict(data)
        except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
            raise ProfileError(f"无法加载配置 {path.name}: {exc}") from exc
        self.validate(profile)
        return profile

    def save(self, profile: Profile, path: str | Path | None = None) -> Path:
        self.validate(profile)
        path = Path(path) if path else self.root / self.safe_filename(profile.name)
        path.parent.mkdir(parents=True, exist_ok=True)
        fd, temp_name = tempfile.mkstemp(prefix=f"{path.stem}.", suffix=".tmp", dir=path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
                json.dump(profile.to_dict(), handle, ensure_ascii=False, indent=2)
                handle.write("\n")
            os.replace(temp_name, path)
        finally:
            if os.path.exists(temp_name):
                os.unlink(temp_name)
        return path

    @staticmethod
    def safe_filename(name: str) -> str:
        cleaned = "".join(ch if ch.isalnum() or ch in "-_ ." else "_" for ch in name).strip(" .")
        return (cleaned or "csp_profile") + ".json"

    @staticmethod
    def validate(profile: Profile) -> None:
        if profile.schema_version != SCHEMA_VERSION:
            raise ProfileError(f"不支持的 schema_version={profile.schema_version}，需要 {SCHEMA_VERSION}")
        if not profile.name.strip():
            raise ProfileError("配置名称不能为空")
        if not profile.property_rois():
            raise ProfileError("至少需要一个工具属性 ROI")
        width, height = profile.capture_size()
        if width <= 0 or height <= 0:
            raise ProfileError("捕获分辨率无效")
        seen: set[str] = set()
        for roi in profile.rois:
            if roi.id in seen:
                raise ProfileError(f"重复 ROI id: {roi.id}")
            seen.add(roi.id)
            if not roi.rect.contains_size(width, height):
                raise ProfileError(f"ROI {roi.id} 越过捕获图像边界")
        if profile.target_window:
            if profile.target_window.capture_scope != "client":
                raise ProfileError("当前版本只允许保存目标窗口客户区捕获")
            if not profile.target_window.exe_name or not profile.target_window.class_name:
                raise ProfileError("目标窗口匹配信息不完整")

    @staticmethod
    def window_compatibility(profile: Profile, window: dict[str, Any]) -> list[str]:
        reasons: list[str] = []
        binding = profile.target_window
        if binding is None:
            return ["配置没有目标 CSP 窗口绑定"]
        if str(window.get("exe_name", "")).lower() != binding.exe_name.lower():
            reasons.append("目标进程可执行文件变化")
        if binding.class_name and str(window.get("class_name", "")) != binding.class_name:
            reasons.append("目标窗口类变化")
        if tuple(window.get("capture_size", (0, 0))) != tuple(binding.capture_size):
            reasons.append(f"窗口客户区捕获尺寸变化: 配置 {binding.capture_size}, 当前 {window.get('capture_size')}")
        if abs(float(window.get("dpi", binding.dpi)) - binding.dpi) > 1.0:
            reasons.append(f"窗口 DPI 变化: 配置 {binding.dpi:g}, 当前 {float(window.get('dpi')):g}")
        if str(window.get("capture_scope", "client")) != "client":
            reasons.append("捕获范围不是客户区")
        return reasons

    @staticmethod
    def compatibility(profile: Profile, screen: dict[str, Any], capture_size: tuple[int, int] | None = None) -> list[str]:
        reasons: list[str] = []
        expected = profile.monitor
        if str(screen.get("screen_id", "")) != expected.screen_id:
            reasons.append("显示器标识变化")
        if tuple(screen.get("capture_size", (0, 0))) != expected.capture_size:
            reasons.append(f"捕获分辨率变化: 配置 {expected.capture_size}, 当前 {screen.get('capture_size')}")
        if capture_size and tuple(capture_size) != expected.capture_size:
            reasons.append(f"实际截图分辨率变化: 配置 {expected.capture_size}, 当前 {capture_size}")
        current_geometry = screen.get("geometry")
        if current_geometry and list(current_geometry) != expected.geometry.to_list():
            reasons.append("桌面坐标或逻辑尺寸变化")
        current_dpr = float(screen.get("device_pixel_ratio", expected.device_pixel_ratio))
        if abs(current_dpr - expected.device_pixel_ratio) > 0.05:
            reasons.append("DPI/缩放比例变化")
        return reasons
