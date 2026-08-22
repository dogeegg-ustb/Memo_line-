#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string>
#include <vector>

namespace wb {

enum class Status : int {
  Ok = 0,
  InvalidInput = 1,
  RoiTooSmall = 2,
  NoStableWorkspaceBackground = 3,
  NoConnectedBackgroundEvidence = 4,
  InsufficientGeometry = 5,
  EndpointTruncated = 6,
  AmbiguousCandidates = 7,
  OuterBoundaryNotSeparable = 8,
  RectangleClosureFailed = 9,
  RefinementFailed = 10,
  IndependentValidationFailed = 11,
  Cancelled = 12,
};

enum class EvidenceGrade : int {
  None = 0,
  A = 1,
  B = 2,
  C_L = 3,
  C_II = 4,
};

enum class OuterSide : int {
  Left = 0,
  Top = 1,
  Right = 2,
  Bottom = 3,
};

inline int SideIndex(OuterSide s) { return static_cast<int>(s); }

inline const char* StatusName(Status s) {
  switch (s) {
    case Status::Ok:
      return "Ok";
    case Status::InvalidInput:
      return "InvalidInput";
    case Status::RoiTooSmall:
      return "RoiTooSmall";
    case Status::NoStableWorkspaceBackground:
      return "NoStableWorkspaceBackground";
    case Status::NoConnectedBackgroundEvidence:
      return "NoConnectedBackgroundEvidence";
    case Status::InsufficientGeometry:
      return "InsufficientGeometry";
    case Status::EndpointTruncated:
      return "EndpointTruncated";
    case Status::AmbiguousCandidates:
      return "AmbiguousCandidates";
    case Status::OuterBoundaryNotSeparable:
      return "OuterBoundaryNotSeparable";
    case Status::RectangleClosureFailed:
      return "RectangleClosureFailed";
    case Status::RefinementFailed:
      return "RefinementFailed";
    case Status::IndependentValidationFailed:
      return "IndependentValidationFailed";
    case Status::Cancelled:
      return "Cancelled";
  }
  return "Unknown";
}

struct Lab {
  float L = 0;
  float a = 0;
  float b = 0;
};

struct IntRect {
  int left = 0;
  int top = 0;
  int right = 0;
  int bottom = 0;

  int width() const { return right - left; }
  int height() const { return bottom - top; }
  int area() const { return std::max(0, width()) * std::max(0, height()); }
  bool valid() const { return width() > 0 && height() > 0; }

  bool ContainsPoint(int x, int y) const {
    return x >= left && x < right && y >= top && y < bottom;
  }

  bool IntersectsInterior(const IntRect& o) const {
    return left < o.right && right > o.left && top < o.bottom && bottom > o.top;
  }

  IntRect Clamp(int w, int h) const {
    IntRect r;
    r.left = std::max(0, std::min(left, w));
    r.top = std::max(0, std::min(top, h));
    r.right = std::max(r.left, std::min(right, w));
    r.bottom = std::max(r.top, std::min(bottom, h));
    return r;
  }

  IntRect Expand(int pad_x, int pad_y, int /*unused*/) const {
    return {left - pad_x, top - pad_y, right + pad_x, bottom + pad_y};
  }
};

inline float RectIou(const IntRect& a, const IntRect& b) {
  const int x0 = std::max(a.left, b.left);
  const int y0 = std::max(a.top, b.top);
  const int x1 = std::min(a.right, b.right);
  const int y1 = std::min(a.bottom, b.bottom);
  const int inter = std::max(0, x1 - x0) * std::max(0, y1 - y0);
  const int uni = a.area() + b.area() - inter;
  if (uni <= 0) return 0.f;
  return static_cast<float>(inter) / static_cast<float>(uni);
}

inline float Percentile(std::vector<float>& v, float pct) {
  if (v.empty()) return 0.f;
  pct = std::max(0.f, std::min(100.f, pct));
  const size_t i =
      static_cast<size_t>(std::round((pct / 100.f) * static_cast<float>(v.size() - 1)));
  std::nth_element(v.begin(), v.begin() + static_cast<std::ptrdiff_t>(i), v.end());
  return v[i];
}

struct SeedPatch {
  int seed_id = 0;
  OuterSide side = OuterSide::Left;
  int x = 0;
  int y = 0;
  int size = 0;
  Lab mean_lab{};
  bool accepted = false;
  std::string reject_reason;
};

struct BackgroundModel {
  Lab center_lab{};
  float strong_delta_e = 6.f;
  float weak_delta_e = 12.f;
  std::vector<int> seed_ids;
  float rectangular_support_score = 0.f;
  float confidence = 0.f;
};

struct Hypothesis {
  EvidenceGrade grade = EvidenceGrade::None;
  IntRect rect{};
  std::vector<OuterSide> observed_sides;
  std::vector<OuterSide> closed_sides;
  int model_index = -1;
  float score = 0.f;
  float confidence = 0.f;
  bool endpoints_truncated = false;
};

struct DetectionInput {
  const uint8_t* bgra = nullptr;
  int width = 0;
  int height = 0;
  int stride = 0;
  IntRect user_roi{};
  float dpi_x = 96.f;
  float dpi_y = 96.f;
  int origin_x = 0;
  int origin_y = 0;
  const char* capture_id = nullptr;
};

struct DetectionOutput {
  Status status = Status::InvalidInput;
  IntRect workspace_capture{};
  IntRect workspace_screen{};
  EvidenceGrade grade = EvidenceGrade::None;
  float confidence = 0.f;
  std::string message;
  std::string source_capture_id;
  std::vector<OuterSide> observed_sides;
  std::vector<OuterSide> closed_sides;
  BackgroundModel background_model{};
  bool has_background_model = false;
  std::string source_revision = "wb-cpu-ref-2";
};

}  // namespace wb
