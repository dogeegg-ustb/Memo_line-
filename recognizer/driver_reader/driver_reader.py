"""
driver_reader.py - Python 绑定与数位板驱动压感求解器

用于与 recognizer/driver_reader CLI 协作，或直接读取导出的 JSON 配置文件，
支持硬件有效范围获取、三次贝塞尔 (Cubic Bézier) 压感曲线求解与起笔死区校准。
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
class DriverProfile:
    """数位板驱动硬件与压感特性模型。"""

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
            # 缩放到控制点所在坐标系
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
            print(f"    物理尺寸: {p.physical_width} x {p.physical_height}")
        print(f"    测试输入 50% 压力: {p.transform_pressure(p.recommended_pressure_max * 0.5):.1f}")
        print()
