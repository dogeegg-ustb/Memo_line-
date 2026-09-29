#include "wb/features.hpp"

#include "wb/color.hpp"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace wb {
namespace {

void ScharrGradients(const ImageF32& gray, ImageF32& gx, ImageF32& gy, ImageF32& mag) {
  const int w = gray.width;
  const int h = gray.height;
  gx.Allocate(w, h, 0.f);
  gy.Allocate(w, h, 0.f);
  mag.Allocate(w, h, 0.f);
  for (int y = 1; y < h - 1; ++y) {
    for (int x = 1; x < w - 1; ++x) {
      const float gxv =
          -3.f * gray.At(x - 1, y - 1) + 3.f * gray.At(x + 1, y - 1) - 10.f * gray.At(x - 1, y) +
          10.f * gray.At(x + 1, y) - 3.f * gray.At(x - 1, y + 1) + 3.f * gray.At(x + 1, y + 1);
      const float gyv =
          -3.f * gray.At(x - 1, y - 1) - 10.f * gray.At(x, y - 1) - 3.f * gray.At(x + 1, y - 1) +
          3.f * gray.At(x - 1, y + 1) + 10.f * gray.At(x, y + 1) + 3.f * gray.At(x + 1, y + 1);
      gx.At(x, y) = gxv;
      gy.At(x, y) = gyv;
      mag.At(x, y) = std::sqrt(gxv * gxv + gyv * gyv);
    }
  }
}

void BoxLocalVariance(const ImageF32& gray, int radius, ImageF32& out) {
  const int w = gray.width;
  const int h = gray.height;
  out.Allocate(w, h, 0.f);
  radius = std::max(1, radius);
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const int x0 = std::max(0, x - radius);
      const int x1 = std::min(w, x + radius + 1);
      const int y0 = std::max(0, y - radius);
      const int y1 = std::min(h, y + radius + 1);
      double sum = 0, sum2 = 0;
      int n = 0;
      for (int yy = y0; yy < y1; ++yy) {
        for (int xx = x0; xx < x1; ++xx) {
          const double v = gray.At(xx, yy);
          sum += v;
          sum2 += v * v;
          ++n;
        }
      }
      if (n <= 1) {
        out.At(x, y) = 0.f;
        continue;
      }
      const double mean = sum / n;
      out.At(x, y) = static_cast<float>(std::max(0.0, sum2 / n - mean * mean));
    }
  }
}

}  // namespace

ImageBGR DownsampleBgrBilinear(const ImageBGR& src, float scale) {
  ImageBGR out;
  if (scale >= 0.999f) {
    out = src;
    return out;
  }
  scale = std::max(0.05f, std::min(1.f, scale));
  const int nw = std::max(1, static_cast<int>(std::round(src.width * scale)));
  const int nh = std::max(1, static_cast<int>(std::round(src.height * scale)));
  out.Allocate(nw, nh);
  for (int y = 0; y < nh; ++y) {
    const float sy = (y + 0.5f) / scale - 0.5f;
    const int y0 = std::max(0, std::min(src.height - 1, static_cast<int>(std::floor(sy))));
    const int y1 = std::max(0, std::min(src.height - 1, y0 + 1));
    const float fy = sy - y0;
    for (int x = 0; x < nw; ++x) {
      const float sx = (x + 0.5f) / scale - 0.5f;
      const int x0 = std::max(0, std::min(src.width - 1, static_cast<int>(std::floor(sx))));
      const int x1 = std::max(0, std::min(src.width - 1, x0 + 1));
      const float fx = sx - x0;
      uint8_t* d = out.At(x, y);
      for (int c = 0; c < 3; ++c) {
        const float v00 = src.At(x0, y0)[c];
        const float v10 = src.At(x1, y0)[c];
        const float v01 = src.At(x0, y1)[c];
        const float v11 = src.At(x1, y1)[c];
        const float v0 = v00 * (1 - fx) + v10 * fx;
        const float v1 = v01 * (1 - fx) + v11 * fx;
        d[c] = static_cast<uint8_t>(std::round(v0 * (1 - fy) + v1 * fy));
      }
    }
  }
  return out;
}

IntRect ScaleRect(const IntRect& r, float scale) {
  return {static_cast<int>(std::floor(r.left * scale)), static_cast<int>(std::floor(r.top * scale)),
          static_cast<int>(std::ceil(r.right * scale)),
          static_cast<int>(std::ceil(r.bottom * scale))};
}

IntRect UnscaleRectFloorCeil(const IntRect& r, float scale) {
  if (scale <= 1e-6f) return r;
  return {static_cast<int>(std::floor(r.left / scale)), static_cast<int>(std::floor(r.top / scale)),
          static_cast<int>(std::ceil(r.right / scale)),
          static_cast<int>(std::ceil(r.bottom / scale))};
}

FeatureMaps ExtractFeatures(const ImageBGR& bgr, const DetectorConfig& cfg, float dpi_scale,
                            const IntRect* /*roi_hint*/) {
  FeatureMaps out;
  out.width = bgr.width;
  out.height = bgr.height;
  out.scale_to_capture = 1.f;
  BgrImageToLab(bgr, out.lab);
  BgrImageToGray(bgr, out.gray);
  ScharrGradients(out.gray, out.gradient_x, out.gradient_y, out.gradient_magnitude);
  const int short_side = std::min(bgr.width, bgr.height);
  const int radius = std::max(1, static_cast<int>(std::round(cfg.DpiPx(1.5f, dpi_scale, short_side))));
  BoxLocalVariance(out.gray, radius, out.local_variance);
  return out;
}

}  // namespace wb
