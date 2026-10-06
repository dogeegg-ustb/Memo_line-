"""Update scheduling only: emits requests, never claims recognition has completed."""
import time
from catalog import BRUSH, CANVAS, NAVIGATOR, LAYERS, COLOR, TOOLBAR, TOOLGROUP

STATE_MODULES = {BRUSH: "brushState", TOOLGROUP: "subtoolState", CANVAS: "canvasViewState", NAVIGATOR: "canvasViewState", LAYERS: "currentLayerState", COLOR: "colorState"}


class UpdateEngine:
    def __init__(self, catalog, emit):
        self.catalog, self.emit = catalog, emit
        self.regions = {}
        self.hover = None
        self.pulses = {}
        self.gesture_panels = set()
        self.gesture_signature = None
        self.last_cursor = None
        self.ticks = 0
        self.suspended = False
        self.tool_state_targets = set() # Non-navigation tool targets such as color/layer state.
        self.corrected_canvas_roi = None
        self.pointer_operations = {}
        self.pointer_view_without_key = set()
        self.pointer_targets = set()
        self.pointer_refs = set()

    def record(self, kind, data, ticks=None, refs=()):
        self.emit(dict(type="state", kind=kind, ticks=self.ticks if ticks is None else ticks,
                       relatedEventIds=list(refs), data=data))

    def panel_at(self, x, y):
        return next((name for name, (rx, ry, w, h) in self.regions.items()
                     if rx <= x < rx + w and ry <= y < ry + h), None)

    def request(self, panels, reason, ticks, refs=(), matches=(), phase="prepare"):
        activation_panels = sorted(set(panels) & self.regions.keys())
        panels = self.capture_targets(activation_panels)
        if panels & {CANVAS, NAVIGATOR}:
            panels.update((CANVAS, NAVIGATOR))
        panels = sorted(set(panels) & self.regions.keys())
        if not panels or self.suspended:
            return
        self.record("panelUpdateRequested", dict(panels=panels, reason=reason, phase=phase,
            activationPanels=activation_panels,
            modules=sorted({STATE_MODULES[p] for p in panels}),
            regions={p: self.regions[p] for p in panels}, matches=list(matches),
            recognitionStatus="pendingModule"), ticks, refs)

    def flash_update(self, panels):
        panels = self.capture_targets(panels) & self.regions.keys()
        if panels & {CANVAS,NAVIGATOR}:
            panels.update({CANVAS,NAVIGATOR} & self.regions.keys())
        self.pulses.update({panel:time.monotonic()+0.8 for panel in panels})

    @staticmethod
    def capture_targets(panels):
        targets = set()
        for panel in panels:
            targets.update((BRUSH,TOOLGROUP) if panel in {TOOLBAR,TOOLGROUP} else (panel,))
        return targets

    @staticmethod
    def contains(roi, x, y):
        return bool(roi) and roi[0] <= x < roi[0]+roi[2] and roi[1] <= y < roi[1]+roi[3]

    def activation_panel_at(self, x, y):
        panel = self.panel_at(x, y)
        # The saved viewport minus the core-corrected viewport contains scrollbars.
        if panel == CANVAS and (not self.corrected_canvas_roi or self.contains(self.corrected_canvas_roi, x, y)):
            return None
        return panel

    def pointer_key(self, evt):
        data = evt["data"]
        return ("pen" if evt["kind"].startswith("pen") else "mouse", data.get("button", "pen"))

    def pointer_gestures(self, held_keys):
        """Canvas navigation requires a currently held Space or R, never a remembered tool."""
        keys = set(held_keys)
        if keys & {91, 92}:
            return []
        navigation = bool(keys & {32, 82})
        matches = []
        for match in self.catalog.match_gesture(held_keys):
            targets = [p for p in match["targets"] if navigation or p not in {CANVAS, NAVIGATOR}]
            if targets:
                matches.append(dict(match, targets=targets))
        if navigation and not any(set(m["targets"]) & {CANVAS, NAVIGATOR} for m in matches):
            matches.append(dict(action="pan" if 32 in keys else "rotate", targets=[CANVAS, NAVIGATOR],
                source="heldNavigationKey", navigationKey="Space" if 32 in keys else "R"))
        return matches

    def set_regions(self, regions):
        if self.regions != regions:
            self.hover = None
            self.pulses.clear()
            self.gesture_panels.clear()
            self.gesture_signature = None
            self.pointer_operations.clear()
            self.pointer_view_without_key.clear()
            self.pointer_targets.clear()
            self.pointer_refs.clear()
        self.regions = regions

    def handle(self, msg):
        self.ticks = max(self.ticks, msg.get("ticks", 0))
        if msg["type"] == "cursor":
            previous_cursor = self.last_cursor
            self.last_cursor = msg
            raw_panel = self.panel_at(msg["x"], msg["y"]) if msg["inCsp"] else None
            panel = self.activation_panel_at(msg["x"], msg["y"]) if msg["inCsp"] else None
            # Canvas hover is not a viewport change; scrollbar clicks are handled
            # by their mouse operation rather than a generic pen/cursor event.
            if panel == CANVAS:
                panel = None
            if panel != self.hover:
                if self.hover:
                    self.record("panelActivationEnded", dict(panel=self.hover, reason="cursorLeave"))
                self.hover = panel
                if panel:
                    self.request([panel], "cursorEnter", msg["ticks"])
            matches = self.pointer_gestures(msg.get("heldKeys", [])) if (
                raw_panel == CANVAS and msg.get("pointerDown") and msg.get("foreground")) else []
            if not matches and raw_panel == CANVAS and msg.get("pointerDown") and msg.get("foreground") and self.tool_state_targets:
                matches = [dict(action="selectedStateTool", targets=sorted(self.tool_state_targets), source="lastToolShortcut")]
            signature = (tuple(msg.get("relatedEventIds", [])),tuple(sorted(msg.get("heldKeys", [])))) if matches else None
            self.gesture_panels = {p for m in matches for p in m["targets"]}
            gesture_changed = signature != self.gesture_signature
            if gesture_changed:
                self.gesture_signature = signature
                if matches:
                    self.request(self.gesture_panels,"modifierPointerGesture",msg["ticks"],
                                 msg.get("relatedEventIds", []),matches)
            active_origins = set(msg.get("relatedEventIds", []))
            for key, origin in list(self.pointer_operations.items()):
                if origin not in active_origins:
                    del self.pointer_operations[key]
                    self.pointer_view_without_key.discard(key)
            if self.pointer_operations:
                self.pointer_targets.update(self.gesture_panels)
                self.pointer_refs.update(msg.get("relatedEventIds", []))
                moved = previous_cursor and (previous_cursor["x"],previous_cursor["y"]) != (msg["x"],msg["y"])
                if moved and msg.get("foreground") and msg.get("inCsp") and not (gesture_changed and matches):
                    targets = self.pointer_targets.copy()
                    if (not set(msg.get("heldKeys",[])) & {32,82}
                            and not self.pointer_view_without_key & self.pointer_operations.keys()):
                        targets.difference_update({CANVAS,NAVIGATOR})
                    self.request(targets,"pointerOperationChanged",msg["ticks"],msg.get("relatedEventIds",[]))
            # Stationary hover polling never resets the analysis quiet timer.
            if not msg.get("foreground") or not self.pointer_operations:
                self.pointer_operations.clear()
                self.pointer_view_without_key.clear()
                self.pointer_targets.clear()
                self.pointer_refs.clear()
        elif msg["type"] == "input":
            evt = msg["event"]
            self.ticks = max(self.ticks, evt["ticks"])
            data, kind = evt["data"], evt["kind"]
            if kind == "keyInput":
                matches = self.catalog.match_key(data["vk"], data.get("heldKeys", []))
                tools = [m for m in matches if "pointerTargets" in m]
                if tools:
                    self.tool_state_targets = {p for m in tools for p in m["pointerTargets"] if p not in {CANVAS, NAVIGATOR}}
                # Selecting a navigation tool does not itself change the viewport.
                matches = [dict(m, targets=[p for p in m["targets"] if p not in {CANVAS, NAVIGATOR}])
                           if "pointerTargets" in m else m for m in matches]
                self.record("shortcutResolved", dict(matches=matches,
                    resolution="matched" if matches else "unmapped", shortcutMatchPending=False), evt["ticks"], [evt["eventId"]])
                targets = {p for m in matches for p in m["targets"]}
                if self.pointer_operations:
                    self.pointer_targets.update(targets)
                    self.pointer_refs.add(evt["eventId"])
                    self.request(targets, "shortcutDuringPointer", evt["ticks"], [evt["eventId"]], matches)
                else:
                    self.request(targets, "shortcut", evt["ticks"], [evt["eventId"]], matches, "invalidate")
                if any(m.get("command", "").startswith(("windowshow", "windowhide", "windowbubble")) for m in matches):
                    self.suspended = True
                    self.set_regions({})
                    self.record("workspaceSuspended", dict(reason="paletteVisibilityShortcut", restartRequired=True), evt["ticks"], [evt["eventId"]])
            elif kind == "mouseWheel":
                panel = self.panel_at(data["x"],data["y"])
                matches = self.catalog.match_wheel(data.get("heldKeys", []),data.get("axis","vertical")) if panel in {CANVAS,NAVIGATOR} else []
                self.record("shortcutResolved",dict(matches=matches,inputType="mouseWheel",axis=data.get("axis","vertical"),
                    delta=data["delta"],resolution="matched" if matches else "unmapped",shortcutMatchPending=False),evt["ticks"],[evt["eventId"]])
                targets = {p for match in matches for p in match["targets"]}
                if panel and panel != CANVAS:
                    targets.add(panel)
                if self.pointer_operations:
                    self.pointer_targets.update(targets)
                    self.pointer_refs.add(evt["eventId"])
                    self.request(targets,"wheelDuringPointer",evt["ticks"],[evt["eventId"]],matches)
                else:
                    self.request(targets,"mouseWheel",evt["ticks"],[evt["eventId"]],matches,"invalidate")
            elif kind in {"penBegin", "penEnd", "mouseDown", "mouseUp"}:
                key = self.pointer_key(evt)
                if kind in {"mouseDown", "penBegin"}:
                    if not self.pointer_operations:
                        self.pointer_targets.clear()
                        self.pointer_refs.clear()
                    panel = self.activation_panel_at(data["x"], data["y"])
                    targets = {panel} if panel else set()
                    if panel == NAVIGATOR or (panel == CANVAS and kind == "mouseDown"):
                        self.pointer_view_without_key.add(key)
                    if self.panel_at(data["x"], data["y"]) == CANVAS:
                        # A pen point anywhere in the saved canvas ROI is drawing unless
                        # the current report carries a navigation key. ROI differences
                        # must not classify a normal stroke as a scrollbar operation.
                        if kind == "penBegin":
                            targets.discard(CANVAS)
                        matches = self.pointer_gestures(data.get("heldKeys", []))
                        targets.update(p for m in matches for p in m["targets"])
                        targets.update(self.tool_state_targets)
                    else:
                        self.tool_state_targets.clear()
                    self.pointer_operations[key] = evt["eventId"]
                    self.pointer_targets.update(targets)
                    self.pointer_refs.add(evt["eventId"])
                    self.request(targets, "pointerOperationStarted", evt["ticks"], [evt["eventId"]])
                elif key in self.pointer_operations:
                    del self.pointer_operations[key]
                    self.pointer_view_without_key.discard(key)
                    self.pointer_refs.add(evt["eventId"])
                    if not self.pointer_operations:
                        self.request(self.pointer_targets, "pointerReleased", evt["ticks"],
                                     sorted(self.pointer_refs), phase="invalidate")
                        self.pointer_targets.clear()
                        self.pointer_refs.clear()
                        self.gesture_panels.clear()
            elif kind in {"penSample", "mouseDrag"}:
                key = self.pointer_key(evt)
                if key in self.pointer_operations and self.panel_at(data["x"], data["y"]) == CANVAS:
                    matches = self.pointer_gestures(data.get("heldKeys", []))
                    targets = {p for m in matches for p in m["targets"]} - self.pointer_targets
                    if targets:
                        self.pointer_targets.update(targets)
                        self.pointer_refs.add(evt["eventId"])
                        self.request(targets, "modifierPointerGesture", evt["ticks"],
                                     [self.pointer_operations[key], evt["eventId"]], matches)

    def active_panels(self):
        if self.suspended:
            return set()
        now = time.monotonic()
        self.pulses = {p: until for p, until in self.pulses.items() if until > now}
        # Hover and armed gestures prepare monitoring only. Flash on actual updates.
        active = set(self.pulses)
        if active & {TOOLBAR, TOOLGROUP, BRUSH}:
            active.update({BRUSH, TOOLGROUP} & self.regions.keys())
        if active & {CANVAS, NAVIGATOR}:
            active.update({CANVAS, NAVIGATOR} & self.regions.keys())
        return active
