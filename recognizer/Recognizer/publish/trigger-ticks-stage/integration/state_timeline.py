"""Resolve reserved packages; comparisons use state values rather than diagnostics."""
import json
import threading

MODULES = {"笔刷属性":"brushState", "图层":"currentLayerState", "色彩":"colorState", "画布视口":"canvasViewState"}

def synchronized(method):
    def call(self,*args,**kwargs):
        with self.lock:
            return method(self,*args,**kwargs)
    return call


def semantic_state(module, result):
    if module == "brushState":
        brush = result.get("brush") if isinstance(result,dict) else None
        if not brush or brush.get("status") in {"unknown","ambiguous"} or any(
                p.get("status") not in {"ok","disabled"} for p in brush.get("properties",[])):
            return None
        properties = [{k:p[k] for k in ("key","value","type","unit","enabled","status") if k in p}
                      for p in brush.get("properties",[]) if p.get("status") in {"ok","disabled"}]
        properties.sort(key=lambda p:json.dumps(p,sort_keys=True,ensure_ascii=False))
        return dict(name=brush.get("name"),properties=properties)
    if module == "colorState":
        if not isinstance(result,dict) or result.get("kind") == "unknown":
            return None
        return {key:result.get(key) for key in ("kind","rgb","hex")}
    if module == "canvasViewState":
        if not isinstance(result,dict) or not result.get("success"):
            return None
        state = {key:result.get(key) for key in ("canvasOriginScreenPx","ocrScalePercent","ocrRotationDegrees")}
        snapshot = result.get("snapshot") or {}
        state["transform"] = {k:snapshot.get(k) for k in ("canvasPixelWidth","canvasPixelHeight","scaleReference",
            "cumulativeRelativeScale","rotationDegrees","scaleGeometryEstimate")}
        return state
    if module == "clipState" and isinstance(result,dict):
        return {k:v for k,v in result.items() if k != "source"}
    return result


class StateTimeline:
    def __init__(self,emit,notify,on_update=None):
        self.emit,self.notify = emit,notify
        self.on_update = on_update
        self.packages = {}
        self.resolved = set()
        self.previous = {}
        self.lock = threading.RLock()

    @synchronized
    def expect(self,package_id,modules,initial=False):
        if package_id in self.resolved:
            return
        entry = self.packages.setdefault(package_id,dict(expected=set(),results={},initial=initial))
        entry["expected"].update(modules)

    @synchronized
    def not_requested(self,package_id):
        if package_id not in self.packages and package_id not in self.resolved:
            self.resolved.add(package_id)
            self.emit(dict(type="timelineResult",packageId=package_id,data=dict(status="unchanged",updates=[],reason="noStateActivation")))

    @synchronized
    def close(self, ticks):
        # Called only after both analysis executors have drained. Requests still
        # waiting in the UI (including one-shot retries) must not remain pending.
        for package_id,entry in list(self.packages.items()):
            for module in sorted(entry["expected"]-entry["results"].keys()):
                self.complete(package_id,module,None,dict(
                    reason="sessionStoppedBeforeStateResolved",completedTicks=ticks),
                    "会话结束时未取得该状态结果")

    @synchronized
    def complete_if_pending(self,package_id,module,result,evidence,error=None):
        entry = self.packages.get(package_id)
        if entry is None or module not in entry["expected"] or module in entry["results"]:
            return False
        self.complete(package_id,module,result,evidence,error)
        return True

    @synchronized
    def complete(self,package_id,module,result,evidence,error=None):
        entry = self.packages[package_id]
        update = self._update(module,result,evidence,error,entry["initial"])
        entry["results"][module] = update
        if self.on_update:
            self.on_update(package_id,update,entry["initial"],result)
        if entry["expected"] <= entry["results"].keys():
            updates = [entry["results"][m] for m in sorted(entry["expected"])]
            statuses = {u["status"] for u in updates}
            package_status = "error" if "error" in statuses else "ambiguous" if "ambiguous" in statuses else "unknown" if "unknown" in statuses else "changed" if "changed" in statuses else "unchanged"
            self.emit(dict(type="timelineResult",packageId=package_id,data=dict(status=package_status,
                initial=entry["initial"],updates=updates)))
            if entry["initial"]:
                self.notify(dict(type="initialStateReady",degraded=bool(statuses & {"error","ambiguous","unknown"}),
                    unavailableModules=[u["module"] for u in updates if u["status"] in {"error","ambiguous","unknown"}]))
            del self.packages[package_id]
            self.resolved.add(package_id)

    @synchronized
    def observe(self,module,result,evidence,error=None):
        """Publish an independent observation, such as a parsed saved Clip file."""
        update = self._update(module,result,evidence,error,False)
        if self.on_update:
            self.on_update(None,update,False,result)
        return update

    def _update(self,module,result,evidence,error,initial):
        state = semantic_state(module,result)
        canonical = json.dumps(state,sort_keys=True,ensure_ascii=False,separators=(",",":"))
        if error:
            status = "error"
        elif state is None:
            status = "unknown"
        elif initial or module not in self.previous or self.previous[module] != canonical:
            status = "changed"
        else:
            status = "unchanged"
        # Capture timing describes attribution, not recognition quality. A valid
        # latest observation stays usable even if another input arrived before
        # the screenshot. Preserve causalAmbiguous in evidence for diagnostics.
        if status in {"changed","unchanged"}:
            self.previous[module] = canonical
        update = dict(module=module,status=status,state=state,error=error,evidence=evidence)
        if state is None and result is not None:
            # Preserve partial observations without promoting them to a confirmed state.
            update["observedState"] = result
        return update
