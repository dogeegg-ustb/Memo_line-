"""
driver_reader.py - Python 绑定与数位板驱动压感/坐标映射求解器

用于与 recognizer/driver_reader CLI 协作，或直接读取导出的 JSON 配置文件，
支持硬件有效范围获取、三次贝塞尔 (Cubic Bézier) 压感曲线求解、起笔死区校准、
以及物理坐标系到屏幕坐标系的前向映射与反向映射。
"""

import json
import os
import subprocess
import sys
from dataclasses import dataclass
from typing import List, Optional, Tuple

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


class CubicBezierCurve:
    """三次贝塞尔曲线压感求解器（与 DriverReader C# 高精度二分求解算法保持一致）。"""

    def __init__(
        self,
        p0: Tuple[float, float],
        p1: Tuple[float, float],
        p2: Tuple[float, float],
        p3: Tuple[float, float],
    ):
        self.p0 = p0
        self.p1 = p1
        self.p2 = p2
        self.p3 = p3

    def get_point(self, t: float) -> Tuple[float, float]:
        u = 1.0 - t
        tt = t * t
        uu = u * u
        uuu = uu * u
        ttt = tt * t

        x = uuu * self.p0[0] + 3.0 * uu * t * self.p1[0] + 3.0 * u * tt * self.p2[0] + ttt * self.p3[0]
        y = uuu * self.p0[1] + 3.0 * uu * t * self.p1[1] + 3.0 * u * tt * self.p2[1] + ttt * self.p3[1]
        return x, y

    def evaluate_y(self, x: float) -> float:
        """给定输入硬件压力 x，二分法高精度求解 t 并返回对应的输出压力 y。"""
        if x <= self.p0[0]:
            return self.p0[1]
        if x >= self.p3[0]:
            return self.p3[1]

        low = 0.0
        high = 1.0
        for _ in range(20):
            mid = (low + high) * 0.5
            bx, _ = self.get_point(mid)
            if bx < x:
                low = mid
            else:
                high = mid

        t = (low + high) * 0.5
        _, by = self.get_point(t)
        return by


@dataclass
class RectArea:
    """矩形边界区域定义（物理区域或屏幕区域）。"""

    left: float
    top: float
    right: float
    bottom: float

    @property
    def width(self) -> float:
        return max(0.0, self.right - self.left)

    @property
    def height(self) -> float:
        return max(0.0, self.bottom - self.top)

    def __str__(self) -> str:
        return f"[{self.left:.2f}, {self.top:.2f}] → [{self.right:.2f}, {self.bottom:.2f}] (尺寸: {self.width:.2f} x {self.height:.2f})"

    @classmethod
    def from_dict(cls, data: Optional[dict]) -> Optional["RectArea"]:
        if not data:
            return None
        return cls(
            left=float(data.get("Left", 0.0)),
            top=float(data.get("Top", 0.0)),
            right=float(data.get("Right", 0.0)),
            bottom=float(data.get("Bottom", 0.0)),
        )


@dataclass
class CoordinateMapping:
    """数位板物理坐标系与屏幕显示坐标系之间的映射模型。"""

    mapping_mode: str
    physical_area: RectArea
    physical_unit: str
    screen_area: Optional[RectArea]
    screen_map_ratio: Optional[RectArea]
    screen_index: Optional[int]
    rotation_degrees: int
    lock_aspect_ratio: bool
    scale_x: Optional[float]
    scale_y: Optional[float]
    formula_summary: str

    @classmethod
    def from_dict(cls, data: Optional[dict]) -> Optional["CoordinateMapping"]:
        if not data:
            return None
        phys = RectArea.from_dict(data.get("PhysicalArea")) or RectArea(0, 0, 1, 1)
        screen = RectArea.from_dict(data.get("ScreenArea"))
        ratio = RectArea.from_dict(data.get("ScreenMapRatio"))
        return cls(
            mapping_mode=data.get("MappingMode", "Absolute"),
            physical_area=phys,
            physical_unit=data.get("PhysicalUnit", "Counts"),
            screen_area=screen,
            screen_map_ratio=ratio,
            screen_index=data.get("ScreenIndex"),
            rotation_degrees=int(data.get("RotationDegrees") or 0),
            lock_aspect_ratio=bool(data.get("LockAspectRatio", False)),
            scale_x=float(data["ScaleX"]) if data.get("ScaleX") is not None else None,
            scale_y=float(data["ScaleY"]) if data.get("ScaleY") is not None else None,
            formula_summary=data.get("FormulaSummary", ""),
        )

    def physical_to_screen(
        self,
        px: float,
        py: float,
        default_screen_width: float = 1920.0,
        default_screen_height: float = 1080.0,
    ) -> Tuple[float, float]:
        """将物理坐标映射为屏幕像素坐标。"""
        p_width = self.physical_area.width if self.physical_area.width > 0 else 1.0
        p_height = self.physical_area.height if self.physical_area.height > 0 else 1.0

        norm_x = min(max((px - self.physical_area.left) / p_width, 0.0), 1.0)
        norm_y = min(max((py - self.physical_area.top) / p_height, 0.0), 1.0)

        if self.rotation_degrees == 90:
            norm_x, norm_y = 1.0 - norm_y, norm_x
        elif self.rotation_degrees == 180:
            norm_x, norm_y = 1.0 - norm_x, 1.0 - norm_y
        elif self.rotation_degrees == 270:
            norm_x, norm_y = norm_y, 1.0 - norm_x

        if self.screen_area and self.screen_area.width > 0 and self.screen_area.height > 0:
            sx = self.screen_area.left + norm_x * self.screen_area.width
            sy = self.screen_area.top + norm_y * self.screen_area.height
            return sx, sy

        if self.screen_map_ratio:
            s_left = self.screen_map_ratio.left * default_screen_width
            s_top = self.screen_map_ratio.top * default_screen_height
            s_width = self.screen_map_ratio.width * default_screen_width
            s_height = self.screen_map_ratio.height * default_screen_height
            sx = s_left + norm_x * s_width
            sy = s_top + norm_y * s_height
            return sx, sy

        return norm_x * default_screen_width, norm_y * default_screen_height

    def screen_to_physical(
        self,
        sx: float,
        sy: float,
        default_screen_width: float = 1920.0,
        default_screen_height: float = 1080.0,
    ) -> Tuple[float, float]:
        """将屏幕像素坐标反向映射为数位板物理坐标。"""
        if self.screen_area and self.screen_area.width > 0 and self.screen_area.height > 0:
            norm_x = min(max((sx - self.screen_area.left) / self.screen_area.width, 0.0), 1.0)
            norm_y = min(max((sy - self.screen_area.top) / self.screen_area.height, 0.0), 1.0)
        elif self.screen_map_ratio:
            s_left = self.screen_map_ratio.left * default_screen_width
            s_top = self.screen_map_ratio.top * default_screen_height
            s_width = self.screen_map_ratio.width * default_screen_width or 1.0
            s_height = self.screen_map_ratio.height * default_screen_height or 1.0
            norm_x = min(max((sx - s_left) / s_width, 0.0), 1.0)
            norm_y = min(max((sy - s_top) / s_height, 0.0), 1.0)
        else:
            norm_x = min(max(sx / default_screen_width, 0.0), 1.0)
            norm_y = min(max(sy / default_screen_height, 0.0), 1.0)

        if self.rotation_degrees == 90:
            norm_x, norm_y = norm_y, 1.0 - norm_x
        elif self.rotation_degrees == 180:
            norm_x, norm_y = 1.0 - norm_x, 1.0 - norm_y
        elif self.rotation_degrees == 270:
            norm_x, norm_y = 1.0 - norm_y, norm_x

        px = self.physical_area.left + norm_x * self.physical_area.width
        py = self.physical_area.top + norm_y * self.physical_area.height
        return px, py


@dataclass
class DriverProfile:
    """数位板驱动硬件、压感特性及坐标系映射模型。"""

    vendor: str
    device_name: str
    config_path: str
    curve_type: str
    curve_summary: str
    recommended_pressure_max: float
    physical_width: Optional[float]
    physical_height: Optional[float]
    threshold_ratio: float
    gamma: Optional[float]
    bezier_curve: Optional[CubicBezierCurve]
    coordinate_mapping: Optional[CoordinateMapping] = None

    @classmethod
    def from_dict(cls, data: dict) -> "DriverProfile":
        bezier = None
        bp = data.get("BezierPoints")
        if bp and all(k in bp for k in ("P0", "P1", "P2", "P3")):
            p0 = (float(bp["P0"]["X"]), float(bp["P0"]["Y"]))
            p1 = (float(bp["P1"]["X"]), float(bp["P1"]["Y"]))
            p2 = (float(bp["P2"]["X"]), float(bp["P2"]["Y"]))
            p3 = (float(bp["P3"]["X"]), float(bp["P3"]["Y"]))
            bezier = CubicBezierCurve(p0, p1, p2, p3)

        coord_map = CoordinateMapping.from_dict(data.get("CoordinateMapping"))

        return cls(
            vendor=data.get("Vendor", ""),
            device_name=data.get("DeviceName", ""),
            config_path=data.get("ConfigPath", ""),
            curve_type=data.get("CurveType", "Linear"),
            curve_summary=data.get("CurveSummary", ""),
            recommended_pressure_max=float(data.get("RecommendedPressureMax") or 16383),
            physical_width=data.get("PhysicalWidth"),
            physical_height=data.get("PhysicalHeight"),
            threshold_ratio=float(data.get("ThresholdRatio") or 0.0),
            gamma=data.get("Gamma"),
            bezier_curve=bezier,
            coordinate_mapping=coord_map,
        )

    def transform_pressure(self, raw_pressure: float, pressure_max: Optional[float] = None) -> float:
        """
        根据驱动配置的压感曲线（贝塞尔/Gamma/死区阈值）变换原始硬件压力。
        
        Args:
            raw_pressure: 硬件原始采样压力 (例如 0~8192 或 0~16383)
            pressure_max: 最大压力上限（若不传则使用配置中的推荐上限）
        Returns:
            校准变换后的实际输出压力
        """
        effective_max = pressure_max or self.recommended_pressure_max
        if effective_max <= 0 or raw_pressure <= 0:
            return 0.0

        norm_input = min(max(raw_pressure / effective_max, 0.0), 1.0)

        # 起笔接触死区处理
        if self.threshold_ratio > 0:
            if norm_input < self.threshold_ratio:
                return 0.0
            norm_input = (norm_input - self.threshold_ratio) / (1.0 - self.threshold_ratio)

        # 贝塞尔响应曲线
        if self.bezier_curve:
            scale_x = self.bezier_curve.p3[0]
            scale_y = self.bezier_curve.p3[1]
            bx = norm_input * scale_x
            by = self.bezier_curve.evaluate_y(bx)
            norm_output = min(max(by / scale_y, 0.0), 1.0)
            return norm_output * effective_max

        # Gamma 幂律曲线
        if self.gamma and self.gamma > 0:
            norm_output = norm_input ** self.gamma
            return norm_output * effective_max

        # 默认线性
        return norm_input * effective_max


class DriverReader:
    """与 DriverReader CLI 交互的辅助管理类。"""

    _EXE_PATH: Optional[str] = None

    @classmethod
    def get_executable_path(cls) -> Optional[str]:
        if cls._EXE_PATH and os.path.isfile(cls._EXE_PATH):
            return cls._EXE_PATH

        base_dir = os.path.dirname(os.path.abspath(__file__))
        candidates = [
            os.path.join(base_dir, "publish", "DriverReader.exe"),
            os.path.join(base_dir, "bin", "Release", "net8.0", "win-x64", "DriverReader.exe"),
            os.path.join(base_dir, "bin", "Debug", "net8.0", "DriverReader.exe"),
        ]
        for c in candidates:
            if os.path.isfile(c):
                cls._EXE_PATH = c
                return c
        return None

    @classmethod
    def _run_cli(cls, args: List[str]) -> str:
        exe = cls.get_executable_path()
        if exe:
            cmd = [exe] + args
        else:
            base_dir = os.path.dirname(os.path.abspath(__file__))
            csproj = os.path.join(base_dir, "DriverReader.csproj")
            cmd = ["dotnet", "run", "--project", csproj, "--"] + args

        res = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            check=True,
        )
        return res.stdout

    @classmethod
    def auto(cls) -> List[DriverProfile]:
        """自动扫描本机数位板驱动配置并返回 DriverProfile 列表。"""
        out = cls._run_cli(["auto", "--json"])
        data = json.loads(out)
        return [DriverProfile.from_dict(item) for item in data]

    @classmethod
    def find_all(cls) -> List[DriverProfile]:
        """自动扫描本机数位板驱动配置（auto 别名）。"""
        return cls.auto()

    @classmethod
    def find_best(cls) -> Optional[DriverProfile]:
        """自动寻找并返回本机最推荐的数位板驱动配置（优先贝塞尔曲线/Gamma）。"""
        profiles = cls.auto()
        if not profiles:
            return None
        for p in profiles:
            if p.bezier_curve is not None:
                return p
        for p in profiles:
            if p.gamma is not None:
                return p
        return profiles[0]

    @classmethod
    def load(cls, config_path: str) -> DriverProfile:
        """解析指定驱动配置文件并返回 DriverProfile。"""
        out = cls._run_cli(["export", config_path])
        data = json.loads(out)
        return DriverProfile.from_dict(data)


if __name__ == "__main__":
    print("=== 测试 driver_reader.py Python 绑定 ===")
    profiles = DriverReader.auto()
    print(f"扫描到 {len(profiles)} 个数位板配置:")
    for i, p in enumerate(profiles, 1):
        print(f"[{i}] {p.device_name} ({p.vendor})")
        print(f"    配置文件: {p.config_path}")
        print(f"    特性描述: {p.curve_summary}")
        if p.physical_width and p.physical_height:
            unit = p.coordinate_mapping.physical_unit if p.coordinate_mapping else "Counts"
            print(f"    物理尺寸: {p.physical_width} x {p.physical_height} ({unit})")
        if p.coordinate_mapping:
            cm = p.coordinate_mapping
            print(f"    坐标映射模式: {cm.mapping_mode} (旋转: {cm.rotation_degrees}°)")
            if cm.screen_area:
                print(f"    目标屏幕像素: {cm.screen_area}")
            if cm.scale_x and cm.scale_y:
                print(f"    缩放系数: X={cm.scale_x:.4f} px/{cm.physical_unit}, Y={cm.scale_y:.4f} px/{cm.physical_unit}")
            # 测试前向与反向映射
            center_x = cm.physical_area.left + cm.physical_area.width * 0.5
            center_y = cm.physical_area.top + cm.physical_area.height * 0.5
            screen_x, screen_y = cm.physical_to_screen(center_x, center_y, 1920, 1080)
            rev_x, rev_y = cm.screen_to_physical(screen_x, screen_y, 1920, 1080)
            print(f"    测试映射物理中心 ({center_x:.1f}, {center_y:.1f}) -> 屏幕 ({screen_x:.1f}, {screen_y:.1f}) -> 反向 ({rev_x:.1f}, {rev_y:.1f})")
        print(f"    测试输入 50% 压力: {p.transform_pressure(p.recommended_pressure_max * 0.5):.1f}")
        print()
