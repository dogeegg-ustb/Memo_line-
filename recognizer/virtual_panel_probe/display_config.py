"""Windows display topology (CCD: QueryDisplayConfig / SetDisplayConfig) for virtual monitors.

Attaching or detaching a monitor and moving it only change the current user's display layout, so
none of this needs administrator rights. A virtual monitor is recognised by the hardware ID of
the indirect display adapter its monitor device belongs to (e.g. ``Root\\MttVDD``). CCD reports
an indirect monitor's target under the GPU that renders it, so the monitor's parent device node is
checked instead of the path's adapter. This also works while the monitor is detached or cloned onto
another screen; neither state is visible to EnumDisplayMonitors.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes
from dataclasses import dataclass

USER32 = ctypes.WinDLL("user32", use_last_error=True)
CFGMGR32 = ctypes.WinDLL("cfgmgr32")

QDC_ALL_PATHS, QDC_ONLY_ACTIVE_PATHS = 0x1, 0x2
SDC_APPLY, SDC_USE_SUPPLIED_DISPLAY_CONFIG, SDC_SAVE_TO_DATABASE, SDC_ALLOW_CHANGES = 0x80, 0x20, 0x200, 0x400
PATH_ACTIVE = 0x1
MODE_IDX_INVALID = 0xFFFFFFFF
MODE_SOURCE, MODE_TARGET = 1, 2
GET_SOURCE_NAME, GET_TARGET_NAME = 1, 2
# Undocumented but stable since Windows 10 1607; the Settings app uses the same calls.
GET_DPI_SCALE, SET_DPI_SCALE = -3, -4
DPI_STEPS = (100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500)
CM_DRP_DEVICEDESC, CM_DRP_HARDWAREID = 0x1, 0x2
ERROR_INSUFFICIENT_BUFFER = 122


class LUID(ctypes.Structure):
    _fields_ = [("LowPart", wintypes.DWORD), ("HighPart", wintypes.LONG)]


class Rational(ctypes.Structure):
    _fields_ = [("Numerator", ctypes.c_uint32), ("Denominator", ctypes.c_uint32)]


class SourceInfo(ctypes.Structure):
    _fields_ = [("adapterId", LUID), ("id", ctypes.c_uint32), ("modeInfoIdx", ctypes.c_uint32),
                ("statusFlags", ctypes.c_uint32)]


class TargetInfo(ctypes.Structure):
    _fields_ = [("adapterId", LUID), ("id", ctypes.c_uint32), ("modeInfoIdx", ctypes.c_uint32),
                ("outputTechnology", ctypes.c_uint32), ("rotation", ctypes.c_uint32),
                ("scaling", ctypes.c_uint32), ("refreshRate", Rational), ("scanLineOrdering", ctypes.c_uint32),
                ("targetAvailable", wintypes.BOOL), ("statusFlags", ctypes.c_uint32)]


class PathInfo(ctypes.Structure):
    _fields_ = [("sourceInfo", SourceInfo), ("targetInfo", TargetInfo), ("flags", ctypes.c_uint32)]


class Region(ctypes.Structure):
    _fields_ = [("cx", ctypes.c_uint32), ("cy", ctypes.c_uint32)]


class VideoSignalInfo(ctypes.Structure):
    _fields_ = [("pixelRate", ctypes.c_uint64), ("hSyncFreq", Rational), ("vSyncFreq", Rational),
                ("activeSize", Region), ("totalSize", Region), ("videoStandard", ctypes.c_uint32),
                ("scanLineOrdering", ctypes.c_uint32)]


class SourceMode(ctypes.Structure):
    _fields_ = [("width", ctypes.c_uint32), ("height", ctypes.c_uint32), ("pixelFormat", ctypes.c_uint32),
                ("position", wintypes.POINTL)]


class ModeUnion(ctypes.Union):
    _fields_ = [("targetMode", VideoSignalInfo), ("sourceMode", SourceMode)]


class ModeInfo(ctypes.Structure):
    _fields_ = [("infoType", ctypes.c_uint32), ("id", ctypes.c_uint32), ("adapterId", LUID), ("info", ModeUnion)]


class DeviceInfoHeader(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int32), ("size", ctypes.c_uint32), ("adapterId", LUID), ("id", ctypes.c_uint32)]


class SourceName(ctypes.Structure):
    _fields_ = [("header", DeviceInfoHeader), ("viewGdiDeviceName", wintypes.WCHAR * 32)]


class TargetName(ctypes.Structure):
    _fields_ = [("header", DeviceInfoHeader), ("flags", ctypes.c_uint32), ("outputTechnology", ctypes.c_uint32),
                ("edidManufactureId", ctypes.c_uint16), ("edidProductCodeId", ctypes.c_uint16),
                ("connectorInstance", ctypes.c_uint32), ("monitorFriendlyDeviceName", wintypes.WCHAR * 64),
                ("monitorDevicePath", wintypes.WCHAR * 128)]


class DpiScaleGet(ctypes.Structure):
    _fields_ = [("header", DeviceInfoHeader), ("minScaleRel", ctypes.c_int32), ("curScaleRel", ctypes.c_int32),
                ("maxScaleRel", ctypes.c_int32)]


class DpiScaleSet(ctypes.Structure):
    _fields_ = [("header", DeviceInfoHeader), ("scaleRel", ctypes.c_int32)]


class DevMode(ctypes.Structure):
    # Display variant of DEVMODEW; the printer union members are not used.
    _fields_ = [("dmDeviceName", wintypes.WCHAR * 32), ("dmSpecVersion", wintypes.WORD),
                ("dmDriverVersion", wintypes.WORD), ("dmSize", wintypes.WORD),
                ("dmDriverExtra", wintypes.WORD), ("dmFields", wintypes.DWORD),
                ("dmPositionX", wintypes.LONG), ("dmPositionY", wintypes.LONG),
                ("dmDisplayOrientation", wintypes.DWORD), ("dmDisplayFixedOutput", wintypes.DWORD),
                ("dmColor", ctypes.c_short), ("dmDuplex", ctypes.c_short), ("dmYResolution", ctypes.c_short),
                ("dmTTOption", ctypes.c_short), ("dmCollate", ctypes.c_short),
                ("dmFormName", wintypes.WCHAR * 32), ("dmLogPixels", wintypes.WORD),
                ("dmBitsPerPel", wintypes.DWORD), ("dmPelsWidth", wintypes.DWORD),
                ("dmPelsHeight", wintypes.DWORD), ("dmDisplayFlags", wintypes.DWORD),
                ("dmDisplayFrequency", wintypes.DWORD), ("dmICMMethod", wintypes.DWORD),
                ("dmICMIntent", wintypes.DWORD), ("dmMediaType", wintypes.DWORD),
                ("dmDitherType", wintypes.DWORD), ("dmReserved1", wintypes.DWORD),
                ("dmReserved2", wintypes.DWORD), ("dmPanningWidth", wintypes.DWORD),
                ("dmPanningHeight", wintypes.DWORD)]


DM_POSITION, DM_PELSWIDTH, DM_PELSHEIGHT = 0x20, 0x80000, 0x100000
CDS_UPDATEREGISTRY = 0x1


USER32.GetDisplayConfigBufferSizes.argtypes = [ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32),
                                               ctypes.POINTER(ctypes.c_uint32)]
USER32.GetDisplayConfigBufferSizes.restype = ctypes.c_long
USER32.QueryDisplayConfig.argtypes = [ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(PathInfo),
                                      ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ModeInfo), ctypes.c_void_p]
USER32.QueryDisplayConfig.restype = ctypes.c_long
USER32.SetDisplayConfig.argtypes = [ctypes.c_uint32, ctypes.POINTER(PathInfo), ctypes.c_uint32,
                                    ctypes.POINTER(ModeInfo), ctypes.c_uint32]
USER32.SetDisplayConfig.restype = ctypes.c_long
USER32.DisplayConfigGetDeviceInfo.argtypes = [ctypes.c_void_p]
USER32.DisplayConfigGetDeviceInfo.restype = ctypes.c_long
USER32.DisplayConfigSetDeviceInfo.argtypes = [ctypes.c_void_p]
USER32.DisplayConfigSetDeviceInfo.restype = ctypes.c_long
CFGMGR32.CM_Locate_DevNodeW.argtypes = [ctypes.POINTER(wintypes.DWORD), wintypes.LPCWSTR, wintypes.ULONG]
CFGMGR32.CM_Get_DevNode_Registry_PropertyW.argtypes = [wintypes.DWORD, wintypes.ULONG, ctypes.c_void_p,
                                                       ctypes.c_void_p, ctypes.POINTER(wintypes.ULONG), wintypes.ULONG]
USER32.EnumDisplaySettingsW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(DevMode)]
USER32.ChangeDisplaySettingsExW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(DevMode), wintypes.HWND,
                                            wintypes.DWORD, ctypes.c_void_p]
USER32.ChangeDisplaySettingsExW.restype = ctypes.c_long
CFGMGR32.CM_Get_Parent.argtypes = [ctypes.POINTER(wintypes.DWORD), wintypes.DWORD, wintypes.ULONG]
CFGMGR32.CM_Get_Device_IDW.argtypes = [wintypes.DWORD, wintypes.LPWSTR, wintypes.ULONG, wintypes.ULONG]


def _key(luid: LUID) -> tuple[int, int]:
    return luid.LowPart, luid.HighPart


def _copy(path: PathInfo) -> PathInfo:
    clone = PathInfo()
    ctypes.pointer(clone)[0] = path
    return clone


def query(flags: int) -> tuple[list[PathInfo], list[ModeInfo]]:
    while True:
        path_count, mode_count = ctypes.c_uint32(), ctypes.c_uint32()
        code = USER32.GetDisplayConfigBufferSizes(flags, ctypes.byref(path_count), ctypes.byref(mode_count))
        if code:
            raise OSError(f"GetDisplayConfigBufferSizes 失败（{code}）")
        paths, modes = (PathInfo * path_count.value)(), (ModeInfo * mode_count.value)()
        code = USER32.QueryDisplayConfig(flags, ctypes.byref(path_count), paths, ctypes.byref(mode_count), modes, None)
        if code == ERROR_INSUFFICIENT_BUFFER:
            continue  # a monitor arrived between the two calls
        if code:
            raise OSError(f"QueryDisplayConfig 失败（{code}）")
        return [_copy(p) for p in paths[:path_count.value]], list(modes[:mode_count.value])


def apply(paths: list[PathInfo], modes: list[ModeInfo]) -> None:
    paths, modes = compact(paths, modes)
    path_array, mode_array = (PathInfo * len(paths))(*paths), (ModeInfo * len(modes))(*modes)
    code = USER32.SetDisplayConfig(len(paths), path_array, len(modes), mode_array,
                                   SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE)
    if code:
        raise OSError(f"SetDisplayConfig 失败（{code}）")


def compact(paths: list[PathInfo], modes: list[ModeInfo]) -> tuple[list[PathInfo], list[ModeInfo]]:
    """Drop modes no path refers to (e.g. those of removed paths) and renumber the references."""
    used: dict[int, int] = {}
    kept: list[ModeInfo] = []
    result = []
    for path in paths:
        path = _copy(path)
        for info in (path.sourceInfo, path.targetInfo):
            index = info.modeInfoIdx
            if index == MODE_IDX_INVALID:
                continue
            if index not in used:
                used[index] = len(kept)
                kept.append(modes[index])
            info.modeInfoIdx = used[index]
        result.append(path)
    return result, kept


def _device_info(request) -> None:
    code = USER32.DisplayConfigGetDeviceInfo(ctypes.byref(request))
    if code:
        raise OSError(f"DisplayConfigGetDeviceInfo({request.header.type}) 失败（{code}）")


def _header(request, kind: int, adapter: LUID, ident: int):
    request.header.type, request.header.size = kind, ctypes.sizeof(request)
    request.header.adapterId, request.header.id = adapter, ident
    return request


def target_name(adapter: LUID, target: int) -> TargetName:
    request = _header(TargetName(), GET_TARGET_NAME, adapter, target)
    _device_info(request)
    return request


def source_device(adapter: LUID, source: int) -> str:
    """GDI name of a source, e.g. \\\\.\\DISPLAY3."""
    request = _header(SourceName(), GET_SOURCE_NAME, adapter, source)
    _device_info(request)
    return request.viewGdiDeviceName


def instance_id(device_path: str) -> str:
    r"""\\?\ROOT#DISPLAY#0000#{guid} -> ROOT\DISPLAY\0000"""
    body = device_path.removeprefix("\\\\?\\")
    if "#{" in body:
        body = body[:body.rindex("#{")]
    return body.replace("#", "\\")


def parent_instance(instance: str) -> str:
    node, parent = wintypes.DWORD(), wintypes.DWORD()
    if CFGMGR32.CM_Locate_DevNodeW(ctypes.byref(node), instance, 0) or CFGMGR32.CM_Get_Parent(
            ctypes.byref(parent), node, 0):
        return ""
    buffer = ctypes.create_unicode_buffer(512)
    return "" if CFGMGR32.CM_Get_Device_IDW(parent, buffer, 512, 0) else buffer.value


def device_property(instance: str, prop: int) -> list[str]:
    node = wintypes.DWORD()
    if CFGMGR32.CM_Locate_DevNodeW(ctypes.byref(node), instance, 0):
        return []
    size = wintypes.ULONG(0)
    CFGMGR32.CM_Get_DevNode_Registry_PropertyW(node, prop, None, None, ctypes.byref(size), 0)
    if not size.value:
        return []
    buffer = ctypes.create_unicode_buffer(size.value // 2 + 1)
    if CFGMGR32.CM_Get_DevNode_Registry_PropertyW(node, prop, None, buffer, ctypes.byref(size), 0):
        return []
    return [part for part in buffer[:size.value // 2].split("\0") if part]


@dataclass(frozen=True)
class DeviceIdentity:
    instance: str
    description: str
    hardware_ids: tuple[str, ...]


_identities: dict[str, DeviceIdentity] = {}


def monitor_owner(monitor_path: str) -> DeviceIdentity:
    """The device a monitor belongs to: its GPU, or the indirect display adapter of a virtual monitor."""
    if monitor_path not in _identities:
        instance = parent_instance(instance_id(monitor_path)) if monitor_path else ""
        description = (device_property(instance, CM_DRP_DEVICEDESC) or [""])[0] if instance else ""
        hardware = tuple(device_property(instance, CM_DRP_HARDWAREID)) if instance else ()
        _identities[monitor_path] = DeviceIdentity(instance, description, hardware)
    return _identities[monitor_path]


@dataclass(frozen=True)
class Target:
    """One connected monitor, attached to the desktop or not."""
    adapter: tuple[int, int]
    target_id: int
    adapter_instance: str
    adapter_description: str
    monitor_name: str
    monitor_path: str
    active: bool
    source: tuple[tuple[int, int], int] | None  # (adapter, source id) while active
    device: str                                 # GDI name while active, else ""
    cloned: bool                                # shares its source with another monitor


def targets(hardware_ids: tuple[str, ...]) -> list[Target]:
    """Connected monitors that belong to a device whose hardware ID is in `hardware_ids`."""
    wanted = {h.lower() for h in hardware_ids}
    all_paths, _ = query(QDC_ALL_PATHS)
    active_paths, _ = query(QDC_ONLY_ACTIVE_PATHS)
    sharing: dict[tuple, int] = {}
    for path in active_paths:
        source = (_key(path.sourceInfo.adapterId), path.sourceInfo.id)
        sharing[source] = sharing.get(source, 0) + 1
    active = {(_key(p.targetInfo.adapterId), p.targetInfo.id): p for p in active_paths}
    found: dict[tuple, Target] = {}
    for path in all_paths:
        info = path.targetInfo
        key = (_key(info.adapterId), info.id)
        if key in found or not info.targetAvailable:
            continue
        try:
            name = target_name(info.adapterId, info.id)
            monitor_name, monitor_path = name.monitorFriendlyDeviceName, name.monitorDevicePath
        except OSError:
            continue
        identity = monitor_owner(monitor_path)
        if not wanted & {h.lower() for h in identity.hardware_ids}:
            continue
        live = active.get(key)
        source = (_key(live.sourceInfo.adapterId), live.sourceInfo.id) if live else None
        device = ""
        if live:
            try:
                device = source_device(live.sourceInfo.adapterId, live.sourceInfo.id)
            except OSError:
                pass
        found[key] = Target(key[0], info.id, identity.instance, identity.description, monitor_name, monitor_path,
                            live is not None, source, device, bool(source and sharing.get(source, 0) > 1))
    return sorted(found.values(), key=lambda t: (t.adapter_instance, t.target_id))


def attach(chosen: list[Target]) -> None:
    """Extend the desktop onto `chosen`: each gets its own source on its own adapter.

    A target that is cloned onto another screen is split off. Other monitors keep their paths and
    modes; Windows picks the new monitors' modes and initial positions.
    """
    keys = {(t.adapter, t.target_id) for t in chosen}
    all_paths, _ = query(QDC_ALL_PATHS)
    active_paths, modes = query(QDC_ONLY_ACTIVE_PATHS)
    keep = [p for p in active_paths if (_key(p.targetInfo.adapterId), p.targetInfo.id) not in keys]
    used = {(_key(p.sourceInfo.adapterId), p.sourceInfo.id) for p in keep}
    sources: dict[tuple[int, int], set[int]] = {}
    for path in all_paths:
        sources.setdefault(_key(path.sourceInfo.adapterId), set()).add(path.sourceInfo.id)
    # Only the path in use is listed for an active (e.g. cloned) target, so any path of the target
    # serves as the template and gets a free source of the same adapter.
    templates: dict[tuple, PathInfo] = {}
    for path in [*active_paths, *all_paths]:
        target = (_key(path.targetInfo.adapterId), path.targetInfo.id)
        if target in keys and target not in templates and _key(path.sourceInfo.adapterId) == target[0]:
            templates[target] = path
    missing = keys - templates.keys()
    for target, template in sorted(templates.items()):
        free = sorted(i for i in sources.get(target[0], ()) if (target[0], i) not in used)
        if not free:
            missing.add(target)
            continue
        path = _copy(template)
        path.flags |= PATH_ACTIVE
        path.sourceInfo.id = free[0]
        path.sourceInfo.modeInfoIdx = path.targetInfo.modeInfoIdx = MODE_IDX_INVALID
        keep.append(path)
        used.add((target[0], free[0]))
    if missing:
        raise OSError(f"{len(missing)} 个虚拟显示器没有可用的输出源")
    apply(keep, modes)


def detach(chosen: list[Target]) -> None:
    keys = {(t.adapter, t.target_id) for t in chosen}
    active_paths, modes = query(QDC_ONLY_ACTIVE_PATHS)
    keep = [p for p in active_paths if (_key(p.targetInfo.adapterId), p.targetInfo.id) not in keys]
    if len(keep) != len(active_paths):
        apply(keep, modes)


def move_sources(positions: dict[str, tuple[int, int]]) -> None:
    """Place active sources by GDI name (e.g. \\\\.\\DISPLAY3 -> (x, y)) in one SetDisplayConfig call."""
    if not positions:
        return
    active_paths, modes = query(QDC_ONLY_ACTIVE_PATHS)
    changed = False
    for path in active_paths:
        index = path.sourceInfo.modeInfoIdx
        if index == MODE_IDX_INVALID or modes[index].infoType != MODE_SOURCE:
            continue
        try:
            device = source_device(path.sourceInfo.adapterId, path.sourceInfo.id)
        except OSError:
            continue
        if device in positions:
            point = modes[index].info.sourceMode.position
            point.x, point.y = positions[device]
            changed = True
    if changed:
        apply(active_paths, modes)


def _source_of(device: str) -> SourceInfo | None:
    active_paths, _ = query(QDC_ONLY_ACTIVE_PATHS)
    for path in active_paths:
        try:
            if source_device(path.sourceInfo.adapterId, path.sourceInfo.id) == device:
                return path.sourceInfo
        except OSError:
            continue
    return None


def scale_percent(device: str) -> tuple[int, list[int]] | None:
    """(current %, allowed %) of a source's display scale, as in Settings > Display."""
    source = _source_of(device)
    if source is None:
        return None
    request = _header(DpiScaleGet(), GET_DPI_SCALE, source.adapterId, source.id)
    if USER32.DisplayConfigGetDeviceInfo(ctypes.byref(request)):
        return None
    recommended = abs(request.minScaleRel)
    allowed = [DPI_STEPS[i] for i in range(len(DPI_STEPS))
               if request.minScaleRel <= i - recommended <= request.maxScaleRel]
    current = recommended + request.curScaleRel
    return (DPI_STEPS[current] if 0 <= current < len(DPI_STEPS) else 0), allowed


def set_scale_percent(device: str, percent: int) -> bool:
    source = _source_of(device)
    state = scale_percent(device)
    if source is None or state is None or percent not in state[1]:
        return False
    get = _header(DpiScaleGet(), GET_DPI_SCALE, source.adapterId, source.id)
    if USER32.DisplayConfigGetDeviceInfo(ctypes.byref(get)):
        return False
    request = _header(DpiScaleSet(), SET_DPI_SCALE, source.adapterId, source.id)
    request.scaleRel = DPI_STEPS.index(percent) - abs(get.minScaleRel)
    return USER32.DisplayConfigSetDeviceInfo(ctypes.byref(request)) == 0


def modes(device: str) -> list[tuple[int, int]]:
    """Resolutions an attached source supports (as the driver reports them), smallest first."""
    found, index = set(), 0
    mode = DevMode(dmSize=ctypes.sizeof(DevMode))
    while USER32.EnumDisplaySettingsW(device, index, ctypes.byref(mode)):
        found.add((mode.dmPelsWidth, mode.dmPelsHeight))
        index += 1
    return sorted(found, key=lambda size: (size[0] * size[1], size))


def set_mode(device: str, width: int, height: int, position: tuple[int, int]) -> None:
    """Resolution and position of one attached source; the other monitors are not touched."""
    mode = DevMode(dmSize=ctypes.sizeof(DevMode))
    mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION
    mode.dmPelsWidth, mode.dmPelsHeight = width, height
    mode.dmPositionX, mode.dmPositionY = position
    code = USER32.ChangeDisplaySettingsExW(device, ctypes.byref(mode), None, CDS_UPDATEREGISTRY, None)
    if code:
        raise OSError(f"{device} 切换到 {width}x{height} 失败（{code}）")
