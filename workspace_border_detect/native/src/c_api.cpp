#include "wb/c_api.h"

#include "wb/detector.hpp"

#include <cstdio>
#include <cstring>
#include <string>

namespace {

void CopyStr(char* dst, size_t n, const std::string& s) {
  if (!dst || n == 0) return;
  std::snprintf(dst, n, "%s", s.c_str());
}

}  // namespace

extern "C" {

WB_API int wb_detect(const WbDetectRequest* req, WbDetectResult* result) {
  if (!result) return static_cast<int>(wb::Status::InvalidInput);
  std::memset(result, 0, sizeof(*result));
  if (!req || !req->bgra || req->width <= 0 || req->height <= 0) {
    result->status = static_cast<int>(wb::Status::InvalidInput);
    CopyStr(result->message, sizeof(result->message), "InvalidInput");
    return result->status;
  }

  wb::DetectionInput in;
  in.bgra = req->bgra;
  in.width = req->width;
  in.height = req->height;
  in.stride = req->stride;
  in.user_roi = {req->user_roi.left, req->user_roi.top, req->user_roi.right, req->user_roi.bottom};
  in.dpi_x = req->dpi_x;
  in.dpi_y = req->dpi_y;
  in.origin_x = req->origin_x;
  in.origin_y = req->origin_y;
  in.capture_id = req->capture_id;

  wb::DetectionOutput out = wb::DetectWorkspace(in, nullptr);
  result->status = static_cast<int>(out.status);
  result->workspace_capture = {out.workspace_capture.left, out.workspace_capture.top,
                               out.workspace_capture.right, out.workspace_capture.bottom};
  result->workspace_screen = {out.workspace_screen.left, out.workspace_screen.top,
                              out.workspace_screen.right, out.workspace_screen.bottom};
  result->evidence_grade = static_cast<int>(out.grade);
  result->confidence = out.confidence;
  CopyStr(result->message, sizeof(result->message), out.message);
  CopyStr(result->source_capture_id, sizeof(result->source_capture_id), out.source_capture_id);
  return result->status;
}

WB_API const char* wb_status_name(int status) {
  return wb::StatusName(static_cast<wb::Status>(status));
}

}  // extern "C"
