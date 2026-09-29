#include "wb/color.hpp"
#include "wb/detector.hpp"

#include <cstdio>
#include <vector>

namespace {

constexpr int kWidth = 420;
constexpr int kHeight = 480;
constexpr wb::IntRect kRoi{3, 16, 415, 470};

void Fill(std::vector<uint8_t>& image, wb::IntRect rect, uint8_t gray) {
  for (int y = rect.top; y < rect.bottom; ++y) {
    for (int x = rect.left; x < rect.right; ++x) {
      auto* pixel = image.data() + (size_t(y) * kWidth + x) * 4;
      pixel[0] = pixel[1] = pixel[2] = gray;
      pixel[3] = 255;
    }
  }
}

wb::DetectionOutput Detect(std::vector<uint8_t>& image) {
  wb::BackgroundModel background;
  background.center_lab = wb::BgrToLab(45, 45, 45);
  background.strong_delta_e = 3.5f;
  background.weak_delta_e = 6.f;
  wb::DetectionInput input;
  input.bgra = image.data();
  input.width = kWidth;
  input.height = kHeight;
  input.stride = kWidth * 4;
  input.user_roi = kRoi;
  input.dpi_x = input.dpi_y = 144.f;
  input.capture_id = "navigator-orientation-regression";
  return wb::DetectNavigatorThumbnailCii(input, background);
}

bool Covers(wb::IntRect outer, wb::IntRect canvas, int tolerance = 1) {
  return outer.left <= canvas.left + tolerance && outer.top <= canvas.top + tolerance &&
         outer.right >= canvas.right - tolerance && outer.bottom >= canvas.bottom - tolerance;
}

bool ExpectCase(const char* name, std::vector<uint8_t>& image, wb::IntRect canvas) {
  const auto result = Detect(image);
  std::printf("%s: %s [%d,%d,%d,%d] %s\n", name, wb::StatusName(result.status),
              result.workspace_capture.left, result.workspace_capture.top,
              result.workspace_capture.right, result.workspace_capture.bottom,
              result.message.c_str());
  return result.status == wb::Status::Ok && Covers(result.workspace_capture, canvas);
}

}  // namespace

int main() {
  int failures = 0;
  std::vector<uint8_t> image(size_t(kWidth) * kHeight * 4);
  constexpr wb::IntRect horizontal_canvas{60, 16, 360, 470};
  constexpr wb::IntRect vertical_canvas{3, 87, 415, 399};

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, horizontal_canvas, 255);
  if (!ExpectCase("left-right-background", image, horizontal_canvas)) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, vertical_canvas, 255);
  if (!ExpectCase("top-bottom-background", image, vertical_canvas)) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  Fill(image, {5, 87, 413, 399}, 255);
  if (!ExpectCase("top-bottom-connected-by-side-rim", image, {5, 87, 413, 399})) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 45);
  const auto no_canvas = Detect(image);
  if (no_canvas.status == wb::Status::Ok) ++failures;

  Fill(image, {0, 0, kWidth, kHeight}, 255);
  Fill(image, {kRoi.left, kRoi.top, kRoi.right, 87}, 45);
  const auto one_band = Detect(image);
  if (one_band.status == wb::Status::Ok) ++failures;

  std::printf("navigator orientation regression failures=%d\n", failures);
  return failures == 0 ? 0 : 1;
}
