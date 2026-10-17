#include "wb/color.hpp"

#include "wb/parallel.hpp"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>

namespace wb {
namespace {

float SrgbToLinear(float c) {
  c /= 255.f;
  return (c <= 0.04045f) ? (c / 12.92f) : std::pow((c + 0.055f) / 1.055f, 2.4f);
}

// SrgbToLinear of every 8-bit channel value, evaluated once instead of per pixel.
const float* SrgbLinearTable() {
  static const std::array<float, 256> table = [] {
    std::array<float, 256> t{};
    for (int i = 0; i < 256; ++i) t[i] = SrgbToLinear(static_cast<float>(i));
    return t;
  }();
  return table.data();
}

Lab XyzToLab(float X, float Y, float Z) {
  // D65 white
  constexpr float Xn = 0.95047f;
  constexpr float Yn = 1.00000f;
  constexpr float Zn = 1.08883f;
  auto f = [](float t) {
    constexpr float delta = 6.f / 29.f;
    if (t > delta * delta * delta) return std::cbrt(t);
    return t / (3.f * delta * delta) + 4.f / 29.f;
  };
  const float fx = f(X / Xn);
  const float fy = f(Y / Yn);
  const float fz = f(Z / Zn);
  Lab lab;
  lab.L = 116.f * fy - 16.f;
  lab.a = 500.f * (fx - fy);
  lab.b = 200.f * (fy - fz);
  return lab;
}

Lab LinearRgbToLab(float R, float G, float B) {
  // sRGB D65
  const float X = R * 0.4124564f + G * 0.3575761f + B * 0.1804375f;
  const float Y = R * 0.2126729f + G * 0.7151522f + B * 0.0721750f;
  const float Z = R * 0.0193339f + G * 0.1191920f + B * 0.9503041f;
  return XyzToLab(X, Y, Z);
}

}  // namespace

Lab RgbToLab(uint8_t r, uint8_t g, uint8_t b) {
  const float* linear = SrgbLinearTable();
  return LinearRgbToLab(linear[r], linear[g], linear[b]);
}

Lab BgrToLab(uint8_t b, uint8_t g, uint8_t r) { return RgbToLab(r, g, b); }

float DeltaE76(const Lab& a, const Lab& b) {
  const float dL = a.L - b.L;
  const float da = a.a - b.a;
  const float db = a.b - b.b;
  return std::sqrt(dL * dL + da * da + db * db);
}

float DeltaE76(const float* lab3, const Lab& center) {
  const float dL = lab3[0] - center.L;
  const float da = lab3[1] - center.a;
  const float db = lab3[2] - center.b;
  return std::sqrt(dL * dL + da * da + db * db);
}

void BgrImageToLab(const ImageBGR& bgr, ImageF32x3& lab_out) {
  lab_out.Allocate(bgr.width, bgr.height);
  const float* linear = SrgbLinearTable();
  ParallelRows(bgr.height, [&](int begin, int end) {
    for (int y = begin; y < end; ++y) {
      // UI captures are mostly runs of one color: reuse the previous pixel's Lab.
      uint32_t last = UINT32_MAX;
      Lab lab;
      for (int x = 0; x < bgr.width; ++x) {
        const uint8_t* p = bgr.At(x, y);
        const uint32_t key = p[0] | (p[1] << 8) | (p[2] << 16);
        if (key != last) {
          lab = LinearRgbToLab(linear[p[2]], linear[p[1]], linear[p[0]]);
          last = key;
        }
        float* o = lab_out.At(x, y);
        o[0] = lab.L;
        o[1] = lab.a;
        o[2] = lab.b;
      }
    }
  });
}

void BgrImageToGray(const ImageBGR& bgr, ImageF32& gray_out) {
  gray_out.Allocate(bgr.width, bgr.height);
  ParallelRows(bgr.height, [&](int begin, int end) {
    for (int y = begin; y < end; ++y) {
      for (int x = 0; x < bgr.width; ++x) {
        const uint8_t* p = bgr.At(x, y);
        // BT.601
        gray_out.At(x, y) = 0.114f * p[0] + 0.587f * p[1] + 0.299f * p[2];
      }
    }
  });
}

}  // namespace wb
