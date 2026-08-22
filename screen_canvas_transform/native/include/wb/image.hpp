#pragma once

#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <vector>

namespace wb {

struct ImageBGRA {
  int width = 0;
  int height = 0;
  int stride = 0;  // bytes
  std::vector<uint8_t> data;

  uint8_t* Row(int y) { return data.data() + static_cast<size_t>(y) * stride; }
  const uint8_t* Row(int y) const { return data.data() + static_cast<size_t>(y) * stride; }

  void Allocate(int w, int h) {
    width = w;
    height = h;
    stride = w * 4;
    data.assign(static_cast<size_t>(stride) * h, 0);
  }
};

struct ImageBGR {
  int width = 0;
  int height = 0;
  std::vector<uint8_t> data;  // packed BGR, stride = width*3

  uint8_t* At(int x, int y) { return data.data() + (static_cast<size_t>(y) * width + x) * 3; }
  const uint8_t* At(int x, int y) const {
    return data.data() + (static_cast<size_t>(y) * width + x) * 3;
  }

  void Allocate(int w, int h) {
    width = w;
    height = h;
    data.assign(static_cast<size_t>(w) * h * 3, 0);
  }
};

struct ImageF32 {
  int width = 0;
  int height = 0;
  std::vector<float> data;

  float& At(int x, int y) { return data[static_cast<size_t>(y) * width + x]; }
  float At(int x, int y) const { return data[static_cast<size_t>(y) * width + x]; }
  float* Row(int y) { return data.data() + static_cast<size_t>(y) * width; }
  const float* Row(int y) const { return data.data() + static_cast<size_t>(y) * width; }

  void Allocate(int w, int h, float fill = 0.f) {
    width = w;
    height = h;
    data.assign(static_cast<size_t>(w) * h, fill);
  }
};

struct ImageF32x3 {
  int width = 0;
  int height = 0;
  std::vector<float> data;  // planar? packed Lab: L,a,b per pixel

  float* At(int x, int y) { return data.data() + (static_cast<size_t>(y) * width + x) * 3; }
  const float* At(int x, int y) const {
    return data.data() + (static_cast<size_t>(y) * width + x) * 3;
  }

  void Allocate(int w, int h) {
    width = w;
    height = h;
    data.assign(static_cast<size_t>(w) * h * 3, 0.f);
  }
};

struct ImageU8 {
  int width = 0;
  int height = 0;
  std::vector<uint8_t> data;

  uint8_t& At(int x, int y) { return data[static_cast<size_t>(y) * width + x]; }
  uint8_t At(int x, int y) const { return data[static_cast<size_t>(y) * width + x]; }
  uint8_t* Row(int y) { return data.data() + static_cast<size_t>(y) * width; }
  const uint8_t* Row(int y) const { return data.data() + static_cast<size_t>(y) * width; }

  void Allocate(int w, int h, uint8_t fill = 0) {
    width = w;
    height = h;
    data.assign(static_cast<size_t>(w) * h, fill);
  }
};

inline ImageBGR BgraToBgr(const ImageBGRA& bgra) {
  ImageBGR out;
  out.Allocate(bgra.width, bgra.height);
  for (int y = 0; y < bgra.height; ++y) {
    const uint8_t* src = bgra.Row(y);
    uint8_t* dst = out.data.data() + static_cast<size_t>(y) * bgra.width * 3;
    for (int x = 0; x < bgra.width; ++x) {
      dst[x * 3 + 0] = src[x * 4 + 0];
      dst[x * 3 + 1] = src[x * 4 + 1];
      dst[x * 3 + 2] = src[x * 4 + 2];
    }
  }
  return out;
}

inline ImageBGRA CopyBgraBuffer(const uint8_t* bgra, int width, int height, int stride) {
  if (!bgra || width <= 0 || height <= 0) throw std::runtime_error("invalid buffer");
  const int row_bytes = stride > 0 ? stride : width * 4;
  ImageBGRA out;
  out.Allocate(width, height);
  for (int y = 0; y < height; ++y) {
    std::memcpy(out.Row(y), bgra + static_cast<size_t>(y) * row_bytes,
                static_cast<size_t>(width) * 4);
  }
  return out;
}

}  // namespace wb
