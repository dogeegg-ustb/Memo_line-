from __future__ import annotations

from typing import Any

from .models import RESULT_SCHEMA_VERSION, RoiRecognition, utc_now
from .property_catalog import normalize


class StateAssembler:
    def assemble(self, frame_id: int, captured_at: str, recognized_at: str, recognitions: list[RoiRecognition], timings_ms: dict[str, int], last_observed_at: str | None = None) -> dict[str, Any]:
        property_rois = [item for item in recognitions if item.role == "tool_properties"]
        list_rois = [item for item in recognitions if item.role == "tool_list"]
        property_candidates = [candidate for item in property_rois for candidate in item.brush_candidates]
        list_candidates = [candidate for item in list_rois for candidate in item.brush_candidates]
        selected_list = [candidate for candidate in list_candidates if candidate.get("status") == "selected"]
        property_names = [candidate for candidate in property_candidates if candidate.get("name")]
        def name_key(name):
            return normalize(name).replace('筆', '笔')
        # A hover row may also be colored. Only a unique match with the current
        # properties title can resolve multiple highlighted candidates.
        corroborated = [c for c in selected_list if any(name_key(c['name']) == name_key(p['name']) for p in property_names)]
        if len(corroborated) == 1 and len({name_key(p['name']) for p in property_names}) == 1:
            selected_list = corroborated
        evidence: list[dict[str, Any]] = []
        candidates: list[dict[str, Any]] = []
        names = {name_key(candidate["name"]) for candidate in property_names + selected_list}
        for candidate in property_names + selected_list:
            candidates.append(candidate)
            evidence.extend(candidate.get("evidence", []))
        if len(names) == 1:
            name = (property_names + selected_list)[0]['name']
            brush_status = "ok"
            source = "tool_properties_and_tool_list" if property_names and selected_list else ("tool_properties" if property_names else "tool_list")
            brush = {"name": name, "status": brush_status, "candidates": [], "evidence": evidence, "source": source, "incomplete": any(item.get("incomplete") for item in candidates)}
        elif len(names) > 1:
            brush = {"name": "ambiguous", "status": "ambiguous", "candidates": candidates, "evidence": evidence, "source": "conflicting_current_frame_evidence"}
        else:
            brush = {"name": "unknown", "status": "unknown", "candidates": [], "evidence": evidence, "source": "no_reliable_candidate"}

        parameters: list[dict[str, Any]] = []
        unresolved: list[dict[str, Any]] = []
        seen: set[tuple[str, str]] = set()
        for recognition in property_rois:
            for parameter in recognition.parameters:
                marker = (str(parameter.get("key")), str(parameter.get("label")))
                if marker not in seen:
                    parameters.append(parameter)
                    seen.add(marker)
            unresolved.extend(recognition.unresolved)
        for recognition in recognitions:
            if recognition.role == "tool_list":
                unresolved.extend(recognition.unresolved)
        brush["properties"] = parameters
        statuses = [brush["status"]] + [str(item.get("status", "partial")) for item in parameters]
        if brush["status"] == "ambiguous" or "ambiguous" in statuses:
            status = "ambiguous"
        elif not recognitions or all(not item.ocr for item in recognitions):
            status = "unavailable"
        elif unresolved or brush["status"] == "unknown" or any(item in ("unknown", "partial") for item in statuses):
            status = "partial"
        else:
            status = "ok"
        return {
            "schema_version": RESULT_SCHEMA_VERSION,
            "frame_id": frame_id,
            "captured_at": captured_at,
            "recognized_at": recognized_at,
            "last_observed_at": last_observed_at or recognized_at,
            "status": status,
            "brush": brush,
            "unresolved": unresolved,
            "timings_ms": timings_ms,
        }
