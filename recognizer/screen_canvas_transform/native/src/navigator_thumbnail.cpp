#include "wb/detector.hpp"

#include "wb/features.hpp"
#include "wb/image.hpp"
#include "wb/refine.hpp"
#include "wb/similarity.hpp"

#include <algorithm>
#include <cmath>
#include <queue>
#include <utility>
#include <vector>

namespace wb {
namespace {

DetectionOutput FailCii(Status status, const std::string& message, const std::string& capture_id) {
  DetectionOutput out;
  out.status = status;
  out.message = message;
  out.source_capture_id = capture_id;
  out.source_revision = "sct-cii-v4-orientation";
  return out;
}

std::pair<IntRect, Status> NormalizeNavigatorRoi(const IntRect& roi, int width, int height,
                                                 int min_size) {
  IntRect normalized = roi;
  if (normalized.left > normalized.right) std::swap(normalized.left, normalized.right);
  if (normalized.top > normalized.bottom) std::swap(normalized.top, normalized.bottom);
  normalized = normalized.Clamp(width, height);
  if (normalized.width() < min_size || normalized.height() < min_size)
    return {normalized, Status::RoiTooSmall};
  return {normalized, Status::Ok};
}

bool IsBackground(const BackgroundSimilarity& similarity, int x, int y) {
  return similarity.weak_mask.At(x, y) != 0 || similarity.strong_mask.At(x, y) != 0;
}

struct BackgroundComponent {
  IntRect bounds{};
  int area = 0;
  int strong_pixels = 0;
};

// Opposite background bands can be joined by a narrow gray rim at the left or
// right edge of the panel. Connected components then contain only one body,
// so inspect row coverage instead of requiring two separate components.
bool DetectThumbnailFromTopBottomBands(const BackgroundSimilarity& similarity,
                                       const IntRect& roi, IntRect& thumbnail,
                                       float& confidence, std::string& reason) {
  const int width = roi.width();
  const int height = roi.height();
  std::vector<int> background_per_row(height, 0);
  for (int y = roi.top; y < roi.bottom; ++y)
    for (int x = roi.left; x < roi.right; ++x)
      background_per_row[y - roi.top] += IsBackground(similarity, x, y) ? 1 : 0;

  auto is_background_row = [&](int y) {
    return background_per_row[y - roi.top] * 4 >= width * 3;
  };
  auto is_foreground_row = [&](int y) {
    return background_per_row[y - roi.top] * 4 <= width;
  };

  struct BandCandidate {
    IntRect rect{};
    float score = 0.f;
    float confidence = 0.f;
  };
  std::vector<BandCandidate> candidates;
  for (int y = roi.top; y < roi.bottom;) {
    if (!is_foreground_row(y)) { ++y; continue; }
    const int body_top = y;
    int last_foreground = y;
    for (int probe = y + 1; probe < roi.bottom; ++probe) {
      if (is_foreground_row(probe)) last_foreground = probe;
      else if (probe - last_foreground > 4) break;
    }
    const int body_bottom = last_foreground + 1;
    y = body_bottom;
    const int body_height = body_bottom - body_top;
    if (body_height < std::max(24, height / 8)) continue;

    int above = body_top - 1;
    while (above >= roi.top && body_top - above <= 4 && !is_background_row(above)) --above;
    if (above < roi.top || !is_background_row(above)) continue;
    const int upper_end = above + 1;
    while (above > roi.top && is_background_row(above - 1)) --above;
    const int upper_start = above;

    int below = body_bottom;
    while (below < roi.bottom && below - body_bottom < 4 && !is_background_row(below)) ++below;
    if (below >= roi.bottom || !is_background_row(below)) continue;
    const int lower_start = below;
    while (below < roi.bottom && is_background_row(below)) ++below;
    const int lower_end = below;
    if (upper_end - upper_start < 4 || lower_end - lower_start < 4) continue;

    int first_column = roi.right;
    int last_column = roi.left - 1;
    int supported_columns = 0;
    for (int x = roi.left; x < roi.right; ++x) {
      int foreground = 0;
      for (int yy = body_top; yy < body_bottom; ++yy)
        foreground += IsBackground(similarity, x, yy) ? 0 : 1;
      if (foreground * 4 < body_height * 3) continue;
      first_column = std::min(first_column, x);
      last_column = std::max(last_column, x);
      ++supported_columns;
    }
    if (supported_columns < std::max(48, width / 3)) continue;

    const IntRect rect{std::max(roi.left, first_column - 2), upper_start,
                       std::min(roi.right, last_column + 3), lower_end};
    if (!rect.valid() || rect.width() < 48 || rect.height() < 24) continue;
    const float band_support = std::min(1.f, float(std::min(upper_end - upper_start,
                                                         lower_end - lower_start)) / 12.f);
    candidates.push_back({rect, float(body_height * supported_columns),
                          std::min(1.f, 0.55f + 0.25f * supported_columns / width +
                                            0.2f * band_support)});
  }

  if (candidates.empty()) return false;
  std::sort(candidates.begin(), candidates.end(),
            [](const BandCandidate& a, const BandCandidate& b) { return a.score > b.score; });
  if (candidates.size() > 1 && candidates[1].score >= candidates[0].score * 0.92f &&
      RectIou(candidates[0].rect, candidates[1].rect) < 0.8f) {
    reason = "AmbiguousCandidates top-bottom background bands";
    return false;
  }
  thumbnail = candidates.front().rect;
  confidence = candidates.front().confidence;
  return true;
}

bool DetectThumbnailFromBackgroundComponents(const BackgroundSimilarity& similarity,
                                             const IntRect& roi, IntRect& thumbnail,
                                             float& confidence, std::string& reason,
                                             bool& top_bottom_bands) {
  top_bottom_bands = false;
  ImageU8 visited;
  visited.Allocate(similarity.similarity.width, similarity.similarity.height, 0);
  std::vector<BackgroundComponent> components;
  static constexpr int dx[4] = {1, -1, 0, 0};
  static constexpr int dy[4] = {0, 0, 1, -1};

  for (int y = roi.top; y < roi.bottom; ++y) {
    for (int x = roi.left; x < roi.right; ++x) {
      if (visited.At(x, y) || !IsBackground(similarity, x, y)) continue;

      BackgroundComponent component;
      component.bounds = {x, y, x + 1, y + 1};
      std::queue<std::pair<int, int>> pending;
      pending.push({x, y});
      visited.At(x, y) = 1;
      while (!pending.empty()) {
        const auto [cx, cy] = pending.front();
        pending.pop();
        ++component.area;
        if (similarity.strong_mask.At(cx, cy)) ++component.strong_pixels;
        component.bounds.left = std::min(component.bounds.left, cx);
        component.bounds.top = std::min(component.bounds.top, cy);
        component.bounds.right = std::max(component.bounds.right, cx + 1);
        component.bounds.bottom = std::max(component.bounds.bottom, cy + 1);

        for (int i = 0; i < 4; ++i) {
          const int nx = cx + dx[i];
          const int ny = cy + dy[i];
          if (nx < roi.left || nx >= roi.right || ny < roi.top || ny >= roi.bottom ||
              visited.At(nx, ny) || !IsBackground(similarity, nx, ny)) {
            continue;
          }
          visited.At(nx, ny) = 1;
          pending.push({nx, ny});
        }
      }
      if (component.area >= 16 && component.strong_pixels > 0)
        components.push_back(component);
    }
  }

  struct Pair {
    IntRect rect{};
    float score = 0.f;
  };
  std::vector<Pair> pairs;
  for (size_t i = 0; i < components.size(); ++i) {
    for (size_t j = i + 1; j < components.size(); ++j) {
      const BackgroundComponent* left = &components[i];
      const BackgroundComponent* right = &components[j];
      if (left->bounds.left > right->bounds.left) std::swap(left, right);

      const int overlap_top = std::max(left->bounds.top, right->bounds.top);
      const int overlap_bottom = std::min(left->bounds.bottom, right->bounds.bottom);
      const int overlap = overlap_bottom - overlap_top;
      const int gap = right->bounds.left - left->bounds.right;
      if (left->bounds.width() < 4 || right->bounds.width() < 4 || overlap < 12 || gap < 8)
        continue;

      const IntRect rect{left->bounds.left, overlap_top, right->bounds.right, overlap_bottom};
      if (!rect.valid() || rect.width() < 48 || rect.height() < 24 || rect.left < roi.left ||
          rect.top < roi.top || rect.right > roi.right || rect.bottom > roi.bottom) {
        continue;
      }

      const float overlap_ratio = static_cast<float>(overlap) /
                                  static_cast<float>(std::min(left->bounds.height(),
                                                              right->bounds.height()));
      const float area_score = std::log1p(static_cast<float>(left->area + right->area));
      pairs.push_back({rect, overlap_ratio * 2.f + area_score * 0.15f});
    }
  }

  if (pairs.empty()) {
    if (DetectThumbnailFromTopBottomBands(similarity, roi, thumbnail, confidence, reason)) {
      top_bottom_bands = true;
      return true;
    }
    if (reason.rfind("AmbiguousCandidates", 0) == 0) return false;
    reason = "no paired background components";
    return false;
  }
  std::sort(pairs.begin(), pairs.end(),
            [](const Pair& a, const Pair& b) { return a.score > b.score; });
  const auto contains = [](const IntRect& outer, const IntRect& inner) {
    return outer.left <= inner.left && outer.top <= inner.top &&
           outer.right >= inner.right && outer.bottom >= inner.bottom;
  };
  const auto describe = [](const Pair& pair) {
    const auto& r = pair.rect;
    return "[" + std::to_string(r.left) + "," + std::to_string(r.top) + "," +
           std::to_string(r.right) + "," + std::to_string(r.bottom) + "]";
  };
  for (size_t i = 1; i < pairs.size() && pairs[i].score >= pairs[0].score * 0.92f; ++i) {
    // A smaller component pair inside the best rectangle is evidence within
    // the same thumbnail, not a second thumbnail competing with it.
    if (RectIou(pairs[0].rect, pairs[i].rect) < 0.8f &&
        !contains(pairs[0].rect, pairs[i].rect) &&
        !contains(pairs[i].rect, pairs[0].rect)) {
      reason = "AmbiguousCandidates " + describe(pairs[0]) + " vs " + describe(pairs[i]);
      return false;
    }
  }

  thumbnail = pairs.front().rect;
  confidence = std::min(1.f, pairs.front().score / 2.5f);
  return true;
}

void IgnoreRedViewportLines(const ImageBGRA& image, FeatureMaps& features,
                            const IntRect& roi, const BackgroundModel& background) {
  const int w=roi.width(),h=roi.height();
  const size_t count=size_t(w)*h;
  std::vector<uint8_t> core(count,0),thin(count,0),ink(count,0);
  auto index=[&](int x,int y) {return size_t(y)*w+x;};
  auto inside=[&](int x,int y) {return x>=0 && y>=0 && x<w && y<h;};
  auto pixel=[&](int x,int y) {return image.Row(y+roi.top)+size_t(x+roi.left)*4;};
  for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
    const auto* p=pixel(x,y);
    core[index(x,y)]=p[2]>=100 && int(p[2])-std::max(p[0],p[1])>=30;
  }
  auto core_at=[&](int x,int y) {return inside(x,y) && core[index(x,y)];};
  auto nearby_core=[&](int x,int y) {
    for(int dy=-1;dy<=1;++dy) for(int dx=-1;dx<=1;++dx)
      if(core_at(x+dx,y+dy)) return true;
    return false;
  };
  // Only narrow straight strokes qualify. Red artwork remains foreground.
  for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
    if(!core[index(x,y)]) continue;
    for(int direction=0;direction<8;++direction) {
      const double angle=direction*3.14159265358979323846/8;
      const int nx=int(std::lround(5*std::cos(angle))),ny=int(std::lround(5*std::sin(angle)));
      if(core_at(x+nx,y+ny) || core_at(x-nx,y-ny)) continue;
      const int tx=int(std::lround(-6*std::sin(angle))),ty=int(std::lround(6*std::cos(angle)));
      const bool a=nearby_core(x+tx,y+ty),b=nearby_core(x-tx,y-ty);
      if((a || !inside(x+tx,y+ty)) && (b || !inside(x-tx,y-ty)) && (a || b)) {
        thin[index(x,y)]=1;
        break;
      }
    }
  }
  for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
    const auto* p=pixel(x,y);
    if(int(p[2])-std::max(p[0],p[1])<8) continue;
    for(int dy=-2;dy<=2 && !ink[index(x,y)];++dy) for(int dx=-2;dx<=2;++dx)
      if(inside(x+dx,y+dy) && thin[index(x+dx,y+dy)]) {
        ink[index(x,y)]=1;
        break;
      }
  }
  for(int y=0;y<h;++y) for(int x=0;x<w;++x) {
    if(!ink[index(x,y)]) continue;
    float* lab=features.lab.At(x+roi.left,y+roi.top);
    lab[0]=background.center_lab.L;
    lab[1]=background.center_lab.a;
    lab[2]=background.center_lab.b;
    // Feature extraction spreads the line's gradient/variance into adjacent
    // gray pixels. Clear that influence too, without changing their color or
    // the original capture used later to solve the viewport.
    for(int dy=-3;dy<=3;++dy) for(int dx=-3;dx<=3;++dx) {
      if(!inside(x+dx,y+dy)) continue;
      const int xx=x+dx+roi.left,yy=y+dy+roi.top;
      features.gradient_x.At(xx,yy)=0.f;
      features.gradient_y.At(xx,yy)=0.f;
      features.gradient_magnitude.At(xx,yy)=0.f;
      features.local_variance.At(xx,yy)=0.f;
    }
  }
}

}  // namespace

DetectionOutput WorkspaceBorderDetector::DetectCiiWithExternalBackground(
    const DetectionInput& in, const BackgroundModel& external_background) const {
  const std::string capture_id = in.capture_id ? in.capture_id : "";
  if (!in.bgra || in.width <= 0 || in.height <= 0)
    return FailCii(Status::InvalidInput, "invalid buffer", capture_id);

  try {
    auto [roi, roi_status] =
        NormalizeNavigatorRoi(in.user_roi, in.width, in.height, cfg_.min_roi_size_px);
    if (roi_status != Status::Ok)
      return FailCii(roi_status, "navigator ROI too small", capture_id);

    const float dpi_scale = std::max(in.dpi_x, in.dpi_y) / 96.f;
    ImageBGRA bgra = CopyBgraBuffer(in.bgra, in.width, in.height, in.stride);
    ImageBGR bgr = BgraToBgr(bgra);
    FeatureMaps features = ExtractFeatures(bgr, cfg_, dpi_scale, &roi);

    BackgroundModel background = external_background;
    if (background.strong_delta_e <= 0.f) background.strong_delta_e = 6.f;
    if (background.weak_delta_e <= 0.f) background.weak_delta_e = 12.f;

    IgnoreRedViewportLines(bgra, features, roi, background);

    const auto similarity = BuildSimilarity(features, background, roi, cfg_);
    IntRect detected;
    float confidence = 0.f;
    std::string reason;
    bool top_bottom_bands = false;
    if (!DetectThumbnailFromBackgroundComponents(similarity, roi, detected, confidence, reason,
                                                 top_bottom_bands)) {
      const Status status = reason.rfind("AmbiguousCandidates", 0) == 0
                                ? Status::AmbiguousCandidates
                                : Status::InsufficientGeometry;
      return FailCii(status, reason.empty() ? "no C-II thumbnail candidate" : reason, capture_id);
    }

    // The band candidate is derived from actual row/column support and includes
    // the visible canvas. Refining its open left/right edges can crop the paper
    // when it touches the navigator panel border.
    IntRect refined = detected;
    if (!top_bottom_bands &&
        !RefineRectangle(detected, features, background, cfg_, dpi_scale, refined))
      return FailCii(Status::RefinementFailed, "C-II refine failed", capture_id);

    refined = refined.Clamp(in.width, in.height);
    refined.left = std::max(refined.left, roi.left);
    refined.top = std::max(refined.top, roi.top);
    refined.right = std::min(refined.right, roi.right);
    refined.bottom = std::min(refined.bottom, roi.bottom);
    if (!refined.valid())
      return FailCii(Status::RectangleClosureFailed, "C-II refined rect invalid", capture_id);

    DetectionOutput out;
    out.status = Status::Ok;
    out.workspace_capture = refined;
    out.workspace_screen = {refined.left + in.origin_x, refined.top + in.origin_y,
                            refined.right + in.origin_x, refined.bottom + in.origin_y};
    out.grade = EvidenceGrade::C_II;
    out.confidence = confidence;
    out.message = top_bottom_bands ? "ok-cii-top-bottom-bands" : "ok-cii-background-components";
    out.source_capture_id = capture_id;
    out.background_model = background;
    out.has_background_model = true;
    out.source_revision = "sct-cii-v4-orientation";
    return out;
  } catch (const std::exception& ex) {
    return FailCii(Status::InvalidInput, ex.what(), capture_id);
  } catch (...) {
    return FailCii(Status::InvalidInput, "unknown exception", capture_id);
  }
}

DetectionOutput DetectNavigatorThumbnailCii(const DetectionInput& in,
                                             const BackgroundModel& background,
                                             const DetectorConfig* cfg) {
  WorkspaceBorderDetector detector(cfg ? *cfg : DetectorConfig{});
  return detector.DetectCiiWithExternalBackground(in, background);
}

}  // namespace wb
