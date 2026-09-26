#include "wb/color.hpp"

#include <algorithm>
#include <cmath>

namespace wb {
namespace {

float SrgbToLinear(float c) {
  c /= 255.f;
  return (c <= 0.04045f) ? (c / 12.92f) : std::pow((c + 0.055f) / 1.055f, 2.4f);
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

}  // namespace

Lab RgbToLab(uint8_t r, uint8_t g, uint8_t b) {
  const float R = SrgbToLinear(r);
  const float G = SrgbToLinear(g);
  const float B = SrgbToLinear(b);
  // sRGB D65
  const float X = R * 0.4124564f + G * 0.3575761f + B * 0.1804375f;
  const float Y = R * 0.2126729f + G * 0.7151522f + B * 0.0721750f;
  const float Z = R * 0.0193339f + G * 0.1191920f + B * 0.9503041f;
  return XyzToLab(X, Y, Z);
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
  for (int y = 0; y < bgr.height; ++y) {
    for (int x = 0; x < bgr.width; ++x) {
      const uint8_t* p = bgr.At(x, y);
      Lab lab = BgrToLab(p[0], p[1], p[2]);
      float* o = lab_out.At(x, y);
      o[0] = lab.L;
      o[1] = lab.a;
      o[2] = lab.b;
    }
  }
}

void BgrImageToGray(const ImageBGR& bgr, ImageF32& gray_out) {
  gray_out.Allocate(bgr.width, bgr.height);
  for (int y = 0; y < bgr.height; ++y) {
    for (int x = 0; x < bgr.width; ++x) {
      const uint8_t* p = bgr.At(x, y);
      // BT.601
      gray_out.At(x, y) = 0.114f * p[0] + 0.587f * p[1] + 0.299f * p[2];
    }
  }
}

}  // namespace wb
